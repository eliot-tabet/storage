#region License
// Copyright (c) 2020 Jake Fowler
//
// Permission is hereby granted, free of charge, to any person 
// obtaining a copy of this software and associated documentation 
// files (the "Software"), to deal in the Software without 
// restriction, including without limitation the rights to use, 
// copy, modify, merge, publish, distribute, sublicense, and/or sell 
// copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following 
// conditions:
//
// The above copyright notice and this permission notice shall be 
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, 
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES 
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND 
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT 
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR 
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Cmdty.Core.Common;
using Cmdty.Core.Simulation;
using Cmdty.TimePeriodValueTypes;
using Cmdty.TimeSeries;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using Microsoft.Extensions.Logging;

namespace Cmdty.Storage
{
    public class LsmcStorageValuation
    {
        private readonly ILogger<LsmcStorageValuation> _logger;

        private const double FloatingPointTol = 1E-8;   // TODO better way to pick this.
        // This has been very roughly estimated. Probably there is a better way of splitting up progress by estimating the order of the backward and forward components.
        private const double BackwardPcntTime = 0.96;

        public LsmcStorageValuation(ILogger<LsmcStorageValuation> logger = null)
        {
            _logger = logger;
        }

        public static LsmcStorageValuation WithNoLogger => new LsmcStorageValuation();
        
        public LsmcStorageValuationResults<T> Calculate<T>(LsmcValuationParameters<T> lsmcParams)
            where T : ITimePeriod<T>
        {
            var stopwatches = new Stopwatches();
            stopwatches.All.Start();
            // TODO check spotSims is consistent with storage
            if (lsmcParams.Inventory < 0)
                throw new ArgumentException("Inventory cannot be negative.", nameof(lsmcParams.Inventory));

            if (lsmcParams.CurrentPeriod.CompareTo(lsmcParams.Storage.EndPeriod) > 0)
            {
                lsmcParams.OnProgressUpdate?.Invoke(1.0);
                return LsmcStorageValuationResults<T>.CreateExpiredResults();
            }

            if (lsmcParams.CurrentPeriod.Equals(lsmcParams.Storage.EndPeriod))
            {
                if (lsmcParams.Storage.MustBeEmptyAtEnd)
                {
                    if (lsmcParams.Inventory > 0)
                        throw new InventoryConstraintsCannotBeFulfilledException("Storage must be empty at end, but inventory is greater than zero.");
                    lsmcParams.OnProgressUpdate?.Invoke(1.0);
                    return LsmcStorageValuationResults<T>.CreateExpiredResults();
                }
                // Potentially P&L at end
                double spotPrice = lsmcParams.ForwardCurve[lsmcParams.CurrentPeriod];
                double npv = lsmcParams.Storage.TerminalStorageNpv(spotPrice, lsmcParams.Inventory);
                lsmcParams.OnProgressUpdate?.Invoke(1.0);
                return LsmcStorageValuationResults<T>.CreateEndPeriodResults(npv);
            }

            var basisFunctionList = lsmcParams.BasisFunctions.ToList();

            TimeSeries<T, InventoryRange> inventorySpace = StorageHelper.CalculateInventorySpace(lsmcParams.Storage, lsmcParams.Inventory, lsmcParams.CurrentPeriod);

            if (lsmcParams.ForwardCurve.Start.CompareTo(lsmcParams.CurrentPeriod) > 0)
                throw new ArgumentException("Forward curve starts too late. Must start on or before the current period.", nameof(lsmcParams.ForwardCurve));

            if (lsmcParams.ForwardCurve.End.CompareTo(inventorySpace.End) < 0)
                throw new ArgumentException("Forward curve does not extend until storage end period.", nameof(lsmcParams.ForwardCurve));

            // Perform backward induction
            _logger?.LogInformation("Starting spot price simulation.");
            stopwatches.PriceSimulation.Start();
            ISpotSimResults<T> spotSims = lsmcParams.SpotSimsGenerator();
            stopwatches.PriceSimulation.Stop();
            _logger?.LogInformation("Spot price simulation complete.");

            int numPeriods = inventorySpace.Count + 1; // +1 as inventorySpaceGrid doesn't contain first period
            var storageRegressValuesByPeriod = new Vector<double>[numPeriods][]; // 1st dimension is period, 2nd is inventory, 3rd is simulation number
            var inventorySpaceGrids = new double[numPeriods][];

            // Calculate NPVs at end period
            var endInventorySpace = inventorySpace[lsmcParams.Storage.EndPeriod]; // TODO this will probably break!
            var endInventorySpaceGrid = lsmcParams.GridCalc.GetGridPoints(endInventorySpace.MinInventory, endInventorySpace.MaxInventory)
                                            .ToArray();
            inventorySpaceGrids[numPeriods - 1] = endInventorySpaceGrid;

            var storageActualValuesNextPeriod = new Vector<double>[endInventorySpaceGrid.Length];
            ReadOnlySpan<double> endPeriodSimSpotPrices = spotSims.SpotPricesForPeriod(lsmcParams.Storage.EndPeriod).Span;

            int numSims = spotSims.NumSims;

            for (int i = 0; i < endInventorySpaceGrid.Length; i++)
            {
                double inventory = endInventorySpaceGrid[i];
                var storageValueBySim = new DenseVector(numSims);
                for (int simIndex = 0; simIndex < numSims; simIndex++)
                {
                    double simSpotPrice = endPeriodSimSpotPrices[simIndex];
                    storageValueBySim[simIndex] = lsmcParams.Storage.TerminalStorageNpv(simSpotPrice, inventory);
                }
                storageActualValuesNextPeriod[i] = storageValueBySim;
            }
            
            // Calculate discount factor function
            Day dayToDiscountTo = lsmcParams.CurrentPeriod.First<Day>(); // TODO IMPORTANT, this needs to change
            
            // Memoize the discount factor
            var discountFactorCache = new Dictionary<Day, double>(); // TODO do this in more elegant way and share with intrinsic calc
            double DiscountToCurrentDay(Day cashFlowDate)
            {
                if (!discountFactorCache.TryGetValue(cashFlowDate, out double discountFactor))
                {
                    discountFactor = lsmcParams.DiscountFactors(dayToDiscountTo, cashFlowDate);
                    discountFactorCache[cashFlowDate] = discountFactor;
                }
                return discountFactor;
            }

            Matrix<double> designMatrix = Matrix<double>.Build.Dense(numSims, basisFunctionList.Count);
            for (int i = 0; i < numSims; i++) // TODO see if Math.Net has simpler way of setting whole column to constant
                designMatrix[i, 0] = 1.0;
            
            // Loop back through other periods
            T startActiveStorage = inventorySpace.Start.Offset(-1);
            T[] periodsForResultsTimeSeries = startActiveStorage.EnumerateTo(inventorySpace.End).ToArray();

            int backCounter = numPeriods - 2;
            Vector<double> numSimsMemoryBuffer = Vector<double>.Build.Dense(numSims);
            double progress = 0.0;
            double backStepProgressPcnt = BackwardPcntTime / (periodsForResultsTimeSeries.Length - 1);

            _logger?.LogInformation("Starting backward induction.");
            stopwatches.BackwardInduction.Start();
            foreach (T period in periodsForResultsTimeSeries.Reverse().Skip(1))
            {
                double[] nextPeriodInventorySpaceGrid = inventorySpaceGrids[backCounter + 1];
                Vector<double>[] storageRegressValuesNextPeriod = new Vector<double>[nextPeriodInventorySpaceGrid.Length];

                if (period.Equals(lsmcParams.CurrentPeriod))
                {
                    // Current period, for which the price isn't random so expected storage values are just the average of the values for all sims
                    for (int i = 0; i < nextPeriodInventorySpaceGrid.Length; i++) // TODO parallelise?
                    {
                        Vector<double> storageValuesBySimNextPeriod = storageActualValuesNextPeriod[i];
                        double expectedStorageValueNextPeriod = storageValuesBySimNextPeriod.Average();
                        storageRegressValuesNextPeriod[i] = Vector<double>.Build.Dense(numSims, expectedStorageValueNextPeriod); // TODO this is a bit inefficent, review
                    }
                }
                else
                {
                    PopulateDesignMatrix(designMatrix, period, spotSims, basisFunctionList);
                    stopwatches.PseudoInverse.Start();
                    Matrix<double> pseudoInverse = designMatrix.PseudoInverse();
                    stopwatches.PseudoInverse.Stop();

                    // TODO doing the regressions for all next inventory could be inefficient as they might not all be needed
                    for (int i = 0; i < nextPeriodInventorySpaceGrid.Length; i++) // TODO parallelise?
                    {
                        Vector<double> storageValuesBySimNextPeriod = storageActualValuesNextPeriod[i];
                        Vector<double> regressResults = pseudoInverse.Multiply(storageValuesBySimNextPeriod);
                        Vector<double> estimatedContinuationValues = designMatrix.Multiply(regressResults);
                        storageRegressValuesNextPeriod[i] = estimatedContinuationValues;
                    }
                }
                storageRegressValuesByPeriod[backCounter + 1] = storageRegressValuesNextPeriod;
                
                double[] inventorySpaceGrid;
                if (period.Equals(startActiveStorage))
                    inventorySpaceGrid = new[] { lsmcParams.Inventory };
                else
                {
                    (double inventorySpaceMin, double inventorySpaceMax) = inventorySpace[period];
                    inventorySpaceGrid = lsmcParams.GridCalc.GetGridPoints(inventorySpaceMin, inventorySpaceMax)
                                                .ToArray();
                }
                (double nextStepInventorySpaceMin, double nextStepInventorySpaceMax) = inventorySpace[period.Offset(1)];

                var storageActualValuesThisPeriod = new Vector<double>[inventorySpaceGrid.Length]; // TODO change type to DenseVector?

                Day cmdtySettlementDate = lsmcParams.SettleDateRule(period);
                double discountFactorFromCmdtySettlement = DiscountToCurrentDay(cmdtySettlementDate);

                ReadOnlySpan<double> simulatedPrices;
                if (period.Equals(lsmcParams.CurrentPeriod))
                {
                    double spotPrice = lsmcParams.ForwardCurve[period];
                    simulatedPrices = Enumerable.Repeat(spotPrice, numSims).ToArray(); // TODO inefficient - review.
                }                
                else
                    simulatedPrices = spotSims.SpotPricesForPeriod(period).Span;

                for (int inventoryIndex = 0; inventoryIndex < inventorySpaceGrid.Length; inventoryIndex++)
                {
                    double inventory = inventorySpaceGrid[inventoryIndex];
                    InjectWithdrawRange injectWithdrawRange = lsmcParams.Storage.GetInjectWithdrawRange(period, inventory);
                    double inventoryLoss = lsmcParams.Storage.CmdtyInventoryPercentLoss(period) * inventory;
                    double[] decisionSet = StorageHelper.CalculateBangBangDecisionSet(injectWithdrawRange, inventory, inventoryLoss,
                        nextStepInventorySpaceMin, nextStepInventorySpaceMax, lsmcParams.NumericalTolerance);
                    IReadOnlyList<DomesticCashFlow> inventoryCostCashFlows = lsmcParams.Storage.CmdtyInventoryCost(period, inventory);
                    double inventoryCostNpv = inventoryCostCashFlows.Sum(cashFlow => cashFlow.Amount * DiscountToCurrentDay(cashFlow.Date));

                    double[] injectWithdrawCostNpvs = new double[decisionSet.Length];
                    double[] cmdtyUsedForInjectWithdrawVolume = new double[decisionSet.Length];
                    
                    var regressionContinuationValueByDecisionSet = new Vector<double>[decisionSet.Length];
                    var actualContinuationValueByDecisionSet = new Vector<double>[decisionSet.Length];
                    for (int decisionIndex = 0; decisionIndex < decisionSet.Length; decisionIndex++)
                    {
                        double decisionVolume = decisionSet[decisionIndex];

                        // Inject/Withdraw cost (same for all price sims)
                        injectWithdrawCostNpvs[decisionIndex] = InjectWithdrawCostNpv(lsmcParams.Storage, decisionVolume, period, inventory, DiscountToCurrentDay);

                        // Cmdty Used For Inject/Withdraw (same for all price sims)
                        cmdtyUsedForInjectWithdrawVolume[decisionIndex] = CmdtyVolumeConsumedOnDecision(lsmcParams.Storage, decisionVolume, period, inventory);

                        // Calculate continuation values
                        double inventoryAfterDecision = inventory + decisionVolume - inventoryLoss;
                        for (int inventoryGridIndex = 0; inventoryGridIndex < nextPeriodInventorySpaceGrid.Length; inventoryGridIndex++) // TODO use binary search?
                        {
                            double nextPeriodInventory = nextPeriodInventorySpaceGrid[inventoryGridIndex];
                            if (Math.Abs(nextPeriodInventory - inventoryAfterDecision) < 1E-8) // TODO get rid of hard coded constant
                            {
                                regressionContinuationValueByDecisionSet[decisionIndex] = storageRegressValuesNextPeriod[inventoryGridIndex];
                                actualContinuationValueByDecisionSet[decisionIndex] = storageActualValuesNextPeriod[inventoryGridIndex];
                                break;
                            }
                            if (nextPeriodInventory > inventoryAfterDecision)
                            {
                                // Linearly interpolate inventory space
                                double lowerInventory = nextPeriodInventorySpaceGrid[inventoryGridIndex - 1];
                                double upperInventory = nextPeriodInventory;
                                double inventoryGridSpace = upperInventory - lowerInventory;
                                double lowerWeight = (upperInventory - inventoryAfterDecision) / inventoryGridSpace;
                                double upperWeight = 1.0 - lowerWeight;
                                
                                // Regression storage values
                                Vector<double> lowerRegressStorageValues = storageRegressValuesNextPeriod[inventoryGridIndex - 1];
                                Vector<double> upperRegressStorageValues = storageRegressValuesNextPeriod[inventoryGridIndex];

                                var interpolatedRegressContinuationValue = 
                                    WeightedAverage<T>(lowerRegressStorageValues, 
                                        lowerWeight, upperRegressStorageValues, upperWeight, numSimsMemoryBuffer);

                                regressionContinuationValueByDecisionSet[decisionIndex] = interpolatedRegressContinuationValue;

                                // Actual (simulated) storage values
                                Vector<double> lowerActualStorageValues = storageActualValuesNextPeriod[inventoryGridIndex - 1];
                                Vector<double> upperActualStorageValues = storageActualValuesNextPeriod[inventoryGridIndex];

                                Vector<double> interpolatedActualContinuationValue =
                                        WeightedAverage<T>(lowerActualStorageValues, lowerWeight, 
                                            upperActualStorageValues, upperWeight, numSimsMemoryBuffer);
                                actualContinuationValueByDecisionSet[decisionIndex] = interpolatedActualContinuationValue;
                                break;
                            }
                        }
                    }

                    var storageValuesBySim = new DenseVector(numSims);
                    var decisionNpvsRegress = new double[decisionSet.Length];
                    for (int simIndex = 0; simIndex < numSims; simIndex++)
                    {
                        double simulatedSpotPrice = simulatedPrices[simIndex];
                        for (var decisionIndex = 0; decisionIndex < decisionSet.Length; decisionIndex++)
                        {
                            double decisionVolume = decisionSet[decisionIndex];

                            double injectWithdrawNpv = -decisionVolume * simulatedSpotPrice * discountFactorFromCmdtySettlement;
                            double cmdtyUsedForInjectWithdrawNpv = -cmdtyUsedForInjectWithdrawVolume[decisionIndex] * simulatedSpotPrice * 
                                                                   discountFactorFromCmdtySettlement;
                            double immediateNpv = injectWithdrawNpv - injectWithdrawCostNpvs[decisionIndex] + cmdtyUsedForInjectWithdrawNpv;

                            double continuationValue = regressionContinuationValueByDecisionSet[decisionIndex][simIndex]; // TODO potentially this array lookup could be quite costly

                            double totalNpv = immediateNpv + continuationValue - inventoryCostNpv; // TODO IMPORTANT check if inventoryCostNpv should be subtracted;
                            decisionNpvsRegress[decisionIndex] = totalNpv;
                        }
                        (double optimalRegressDecisionNpv, int indexOfOptimalDecision) = StorageHelper.MaxValueAndIndex(decisionNpvsRegress);
                        
                        // TODO do this tidier an potentially more efficiently
                        double adjustFromRegressToActualContinuation =  
                                                - regressionContinuationValueByDecisionSet[indexOfOptimalDecision][simIndex]
                                                + actualContinuationValueByDecisionSet[indexOfOptimalDecision][simIndex];
                        double optimalActualDecisionNpv = optimalRegressDecisionNpv + adjustFromRegressToActualContinuation;

                        storageValuesBySim[simIndex] = optimalActualDecisionNpv;
                    }
                    storageActualValuesThisPeriod[inventoryIndex] = storageValuesBySim;
                }

                inventorySpaceGrids[backCounter] = inventorySpaceGrid;
                storageActualValuesNextPeriod = storageActualValuesThisPeriod;
                backCounter--;
                progress += backStepProgressPcnt;
                lsmcParams.OnProgressUpdate?.Invoke(progress);
                lsmcParams.CancellationToken.ThrowIfCancellationRequested();
            }
            stopwatches.BackwardInduction.Stop();
            _logger?.LogInformation("Completed backward induction.");
            
            var inventoryBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var injectWithdrawVolumeBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var cmdtyConsumedBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var inventoryLossBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var netVolumeBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var pvByPeriodAndSim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var storageProfiles = new StorageProfile[periodsForResultsTimeSeries.Length];
            var pvBySim = new double[numSims];

            var deltas = new double[periodsForResultsTimeSeries.Length];

            var startingInventories = inventoryBySim[0];
            for (int i = 0; i < numSims; i++)
                startingInventories[i] = lsmcParams.Inventory;

            double forwardStepProgressPcnt = (1.0 - BackwardPcntTime) / periodsForResultsTimeSeries.Length;
            _logger?.LogInformation("Starting forward simulation.");
            stopwatches.ForwardSimulation.Start();
            for (int periodIndex = 0; periodIndex < periodsForResultsTimeSeries.Length - 1; periodIndex++) // TODO more clearly handle this -1
            {
                T period = periodsForResultsTimeSeries[periodIndex];
                Span<double> nextPeriodInventories = inventoryBySim[periodIndex + 1];

                Vector<double>[] regressContinuationValues = storageRegressValuesByPeriod[periodIndex + 1];
                double[] inventoryGridNexPeriod = inventorySpaceGrids[periodIndex + 1];
                Day cmdtySettlementDate = lsmcParams.SettleDateRule(period);
                double discountFactorFromCmdtySettlement = DiscountToCurrentDay(cmdtySettlementDate);
                double discountForDeltas = lsmcParams.DiscountDeltas ? discountFactorFromCmdtySettlement : 1.0;
                double sumSpotPriceTimesVolume = 0.0;

                ReadOnlySpan<double> simulatedPrices;
                if (period.Equals(lsmcParams.CurrentPeriod))
                {
                    double spotPrice = lsmcParams.ForwardCurve[period];
                    simulatedPrices = Enumerable.Repeat(spotPrice, numSims).ToArray(); // TODO inefficient - review, and share code with backward induction
                }
                else
                    simulatedPrices = spotSims.SpotPricesForPeriod(period).Span;
                
                (double nextStepInventorySpaceMin, double nextStepInventorySpaceMax) = inventorySpace[period.Offset(1)];
                Span<double> thisPeriodInventories = inventoryBySim[periodIndex];
                Span<double> thisPeriodInjectWithdrawVolumes = injectWithdrawVolumeBySim[periodIndex];
                Span<double> thisPeriodCmdtyConsumed = cmdtyConsumedBySim[periodIndex];
                Span<double> thisPeriodInventoryLoss = inventoryLossBySim[periodIndex];
                Span<double> thisPeriodNetVolume = netVolumeBySim[periodIndex];
                Span<double> thisPeriodPv = pvByPeriodAndSim[periodIndex];

                for (int simIndex = 0; simIndex < numSims; simIndex++)
                {
                    double simulatedSpotPrice = simulatedPrices[simIndex];
                    double inventory = thisPeriodInventories[simIndex];

                    InjectWithdrawRange injectWithdrawRange = lsmcParams.Storage.GetInjectWithdrawRange(period, inventory);
                    double inventoryLoss = lsmcParams.Storage.CmdtyInventoryPercentLoss(period) * inventory;
                    double[] decisionSet = StorageHelper.CalculateBangBangDecisionSet(injectWithdrawRange, inventory,
                        inventoryLoss, nextStepInventorySpaceMin, nextStepInventorySpaceMax, lsmcParams.NumericalTolerance);
                    IReadOnlyList<DomesticCashFlow> inventoryCostCashFlows = lsmcParams.Storage.CmdtyInventoryCost(period, inventory);
                    double inventoryCostNpv = inventoryCostCashFlows.Sum(cashFlow => cashFlow.Amount * DiscountToCurrentDay(cashFlow.Date));

                    var decisionNpvsRegress = new double[decisionSet.Length];
                    var cmdtyUsedForInjectWithdrawVolumes = new double[decisionSet.Length];
                    var immediatePv = new double[decisionSet.Length];

                    for (var decisionIndex = 0; decisionIndex < decisionSet.Length; decisionIndex++)
                    {
                        double decisionVolume = decisionSet[decisionIndex];
                        double inventoryAfterDecision = inventory + decisionVolume - inventoryLoss;

                        double cmdtyUsedForInjectWithdrawVolume = CmdtyVolumeConsumedOnDecision(lsmcParams.Storage, decisionVolume, period, inventory);

                        double injectWithdrawNpv = -decisionVolume * simulatedSpotPrice * discountFactorFromCmdtySettlement;
                        double cmdtyUsedForInjectWithdrawNpv = -cmdtyUsedForInjectWithdrawVolume * simulatedSpotPrice * discountFactorFromCmdtySettlement;

                        double injectWithdrawCostNpv = InjectWithdrawCostNpv(lsmcParams.Storage, decisionVolume, period, inventory, DiscountToCurrentDay);

                        double immediateNpv = injectWithdrawNpv - injectWithdrawCostNpv + cmdtyUsedForInjectWithdrawNpv - inventoryCostNpv; // TODO IMPORTANT check if inventoryCostNpv should be subtracted

                        double continuationValue =
                            InterpolateContinuationValue(inventoryAfterDecision, inventoryGridNexPeriod, regressContinuationValues, simIndex);

                        double totalNpv = immediateNpv + continuationValue; 
                        decisionNpvsRegress[decisionIndex] = totalNpv;
                        cmdtyUsedForInjectWithdrawVolumes[decisionIndex] = cmdtyUsedForInjectWithdrawVolume;
                        immediatePv[decisionIndex] = immediateNpv;
                    }
                    (double _, int indexOfOptimalDecision) = StorageHelper.MaxValueAndIndex(decisionNpvsRegress);
                    double optimalDecisionVolume = decisionSet[indexOfOptimalDecision];
                    double optimalNextStepInventory = inventory + optimalDecisionVolume - inventoryLoss;
                    nextPeriodInventories[simIndex] = optimalNextStepInventory;

                    double optimalCmdtyUsedForInjectWithdrawVolume = cmdtyUsedForInjectWithdrawVolumes[indexOfOptimalDecision];

                    sumSpotPriceTimesVolume += -(optimalDecisionVolume + optimalCmdtyUsedForInjectWithdrawVolume) * simulatedSpotPrice;

                    thisPeriodInjectWithdrawVolumes[simIndex] = optimalDecisionVolume;
                    thisPeriodCmdtyConsumed[simIndex] = optimalCmdtyUsedForInjectWithdrawVolume;
                    thisPeriodInventoryLoss[simIndex] = inventoryLoss;
                    thisPeriodNetVolume[simIndex] = -optimalDecisionVolume - optimalCmdtyUsedForInjectWithdrawVolume;
                    double optimalImmediatePv = immediatePv[indexOfOptimalDecision];
                    thisPeriodPv[simIndex] = optimalImmediatePv;
                    pvBySim[simIndex] += optimalImmediatePv;
                }

                storageProfiles[periodIndex] = new StorageProfile(Average(thisPeriodInventories), Average(thisPeriodInjectWithdrawVolumes),
                    Average(thisPeriodCmdtyConsumed), Average(thisPeriodInventoryLoss), Average(thisPeriodNetVolume), Average(thisPeriodPv));
                double forwardPrice = lsmcParams.ForwardCurve[period];
                double periodDelta = (sumSpotPriceTimesVolume / forwardPrice / numSims) * discountForDeltas;
                deltas[periodIndex] = periodDelta;
                progress += forwardStepProgressPcnt;
                lsmcParams.OnProgressUpdate?.Invoke(progress);
                lsmcParams.CancellationToken.ThrowIfCancellationRequested();
            }
            // Pv on final period
            double endPeriodPv = 0.0;
            if (!lsmcParams.Storage.MustBeEmptyAtEnd)
            {
                ReadOnlySpan<double> storageEndPeriodSpotPrices = spotSims.SpotPricesForPeriod(lsmcParams.Storage.EndPeriod).Span;
                Span<double> storageEndInventory = inventoryBySim[lsmcParams.Storage.EndPeriod];
                Span<double> storageEndPv = pvByPeriodAndSim[periodsForResultsTimeSeries.Length-1];
                for (int simIndex = 0; simIndex < numSims; simIndex++)
                {
                    double inventory = storageEndInventory[simIndex];
                    double spotPrice = storageEndPeriodSpotPrices[simIndex];
                    double terminalPv = lsmcParams.Storage.TerminalStorageNpv(spotPrice, inventory);
                    storageEndPv[simIndex] = terminalPv;
                    pvBySim[simIndex] += terminalPv;
                }
                endPeriodPv = Average(storageEndPv);
            }

            stopwatches.ForwardSimulation.Stop();
            _logger?.LogInformation("Completed forward simulation.");

            double forwardNpv = pvBySim.Average();
            _logger?.LogInformation("Forward Pv: " + forwardNpv.ToString("N", CultureInfo.InvariantCulture));

            // Calculate NPVs for first active period using current inventory
            // TODO this is unnecessarily introducing floating point error if the val date is during the storage active period and there should not be a Vector of simulated spot prices
            double storageNpv = storageActualValuesNextPeriod[0].Average();

            _logger?.LogInformation("Backward Pv: " + storageNpv.ToString("N", CultureInfo.InvariantCulture));

            double expectedFinalInventory = Average(inventoryBySim[inventoryBySim.NumRows - 1]);
            // Profile at storage end when no decisions can happen
            storageProfiles[storageProfiles.Length - 1] = new StorageProfile(expectedFinalInventory, 0.0, 0.0, 0.0, 0.0, endPeriodPv);

            var deltasSeries = new DoubleTimeSeries<T>(periodsForResultsTimeSeries[0], deltas);
            var storageProfileSeries = new TimeSeries<T, StorageProfile>(periodsForResultsTimeSeries[0], storageProfiles);
            
            // Calculate trigger prices
            _logger?.LogInformation("Started trigger price calculation.");
            int numTriggerPriceVolumes = 10; // TODO move to parameters?
            stopwatches.TriggerPriceCalc.Start();
            var triggerVolumeProfilesArray = new TriggerPriceVolumeProfiles[periodsForResultsTimeSeries.Length - 1];
            var triggerPricesArray = new TriggerPrices[periodsForResultsTimeSeries.Length - 1];
            for (int periodIndex = 0; periodIndex < periodsForResultsTimeSeries.Length - 1; periodIndex++)
            {
                T period = periodsForResultsTimeSeries[periodIndex];
                Vector<double>[] regressContinuationValues = storageRegressValuesByPeriod[periodIndex + 1];
                double[] inventoryGridNexPeriod = inventorySpaceGrids[periodIndex + 1];

                Day cmdtySettlementDate = lsmcParams.SettleDateRule(period);
                double discountFactorFromCmdtySettlement = DiscountToCurrentDay(cmdtySettlementDate);

                double expectedInventory = storageProfileSeries[period].Inventory;
                (double nextStepInventorySpaceMin, double nextStepInventorySpaceMax) = inventorySpace[period.Offset(1)];
                InjectWithdrawRange injectWithdrawRange = lsmcParams.Storage.GetInjectWithdrawRange(period, expectedInventory);
                double inventoryLoss = lsmcParams.Storage.CmdtyInventoryPercentLoss(period) * expectedInventory;
                double[] decisionSet = StorageHelper.CalculateBangBangDecisionSet(injectWithdrawRange, expectedInventory,
                    inventoryLoss, nextStepInventorySpaceMin, nextStepInventorySpaceMax, lsmcParams.NumericalTolerance);

                double maxInjectVolume = decisionSet.Max();
                var injectTriggerPrices = new List<TriggerPricePoint>();
                var triggerPricesBuilder = new TriggerPrices.Builder();
                if (maxInjectVolume > 0)
                {
                    double alternativeVolume = decisionSet.OrderByDescending(d => d).ElementAt(1); // Second highest decision volume, usually will be zero, but might not due to forced injection
                    if (maxInjectVolume > alternativeVolume)
                    {
                        (double alternativeContinuationValue, double alternativeDecisionCost, double alternativeCmdtyConsumed) = 
                            CalcAlternatives(lsmcParams.Storage, expectedInventory, alternativeVolume, inventoryLoss, inventoryGridNexPeriod, regressContinuationValues, period, DiscountToCurrentDay);
                        double[] triggerPriceVolumes = CalcInjectTriggerPriceVolumes<T>(maxInjectVolume, alternativeVolume, numTriggerPriceVolumes);

                        foreach (double triggerVolume in triggerPriceVolumes)
                        {
                            double injectTriggerPrice = CalcTriggerPrice(lsmcParams.Storage, expectedInventory, triggerVolume, inventoryLoss, inventoryGridNexPeriod, 
                                regressContinuationValues, alternativeContinuationValue, alternativeVolume, period, alternativeDecisionCost, 
                                alternativeCmdtyConsumed, discountFactorFromCmdtySettlement, DiscountToCurrentDay);
                            injectTriggerPrices.Add(new TriggerPricePoint(triggerVolume, injectTriggerPrice));
                        }

                        triggerPricesBuilder.MaxInjectTriggerPrice = injectTriggerPrices[injectTriggerPrices.Count - 1].Price;
                        triggerPricesBuilder.MaxInjectVolume = maxInjectVolume;
                    }
                }

                double maxWithdrawVolume = decisionSet.Min();
                var withdrawTriggerPrices = new List<TriggerPricePoint>();
                if (maxWithdrawVolume < 0)
                {
                    double alternativeVolume = decisionSet.OrderBy(d => d).ElementAt(1); // Second lowest decision volume, usually will be zero, but might not due to forced withdrawal
                    if (maxWithdrawVolume < alternativeVolume)
                    {
                        (double alternativeContinuationValue, double alternativeDecisionCost, double alternativeCmdtyConsumed) =
                            CalcAlternatives(lsmcParams.Storage, expectedInventory, alternativeVolume, inventoryLoss, inventoryGridNexPeriod, regressContinuationValues, period, DiscountToCurrentDay);
                        double[] triggerPriceVolumes = CalcWithdrawTriggerPriceVolumes<T>(maxWithdrawVolume, alternativeVolume, numTriggerPriceVolumes);

                        foreach (double triggerVolume in triggerPriceVolumes.Reverse())
                        {
                            double withdrawTriggerPrice = CalcTriggerPrice(lsmcParams.Storage, expectedInventory, triggerVolume, inventoryLoss, inventoryGridNexPeriod,
                                regressContinuationValues, alternativeContinuationValue, alternativeVolume, period, alternativeDecisionCost,
                                alternativeCmdtyConsumed, discountFactorFromCmdtySettlement, DiscountToCurrentDay);
                            withdrawTriggerPrices.Add(new TriggerPricePoint(triggerVolume, withdrawTriggerPrice));
                        }

                        triggerPricesBuilder.MaxWithdrawTriggerPrice = withdrawTriggerPrices[0].Price;
                        triggerPricesBuilder.MaxWithdrawVolume = maxWithdrawVolume;
                    }
                }

                triggerVolumeProfilesArray[periodIndex] = new TriggerPriceVolumeProfiles(injectTriggerPrices, withdrawTriggerPrices);
                triggerPricesArray[periodIndex] = triggerPricesBuilder.Build();
            }
            var triggerPriceVolumeProfiles = new TimeSeries<T, TriggerPriceVolumeProfiles>(periodsForResultsTimeSeries.First(), triggerVolumeProfilesArray);
            var triggerPrices = new TimeSeries<T, TriggerPrices>(periodsForResultsTimeSeries.First(), triggerPricesArray);
            stopwatches.TriggerPriceCalc.Stop();
            _logger?.LogInformation("Completed trigger price calculation.");


            var spotPricePanel = Panel.UseRawDataArray(spotSims.SpotPrices, spotSims.SimulatedPeriods, numSims);
            lsmcParams.OnProgressUpdate?.Invoke(1.0); // Progress with approximately 1.0 should have occured already, but might have been a bit off because of floating-point error.

            stopwatches.All.Stop();
            if (_logger != null)
            {
                string profilingReport = stopwatches.GenerateProfileReport();
                _logger.LogInformation("Profiling Report:");
                _logger.LogInformation(Environment.NewLine + profilingReport);
            }

            return new LsmcStorageValuationResults<T>(storageNpv, deltasSeries, storageProfileSeries, spotPricePanel, 
                inventoryBySim, injectWithdrawVolumeBySim, cmdtyConsumedBySim, inventoryLossBySim, netVolumeBySim, 
                triggerPrices, triggerPriceVolumeProfiles, pvByPeriodAndSim, pvBySim);
        }

        private static double CalcTriggerPrice<T>(ICmdtyStorage<T> storage, double expectedInventory, double triggerVolume, double inventoryLoss,
                double[] inventoryGridNexPeriod, Vector<double>[] regressContinuationValues, double alternativeContinuationValue, double alternativeVolume, T period,
                double alternativeDecisionCost, double alternativeCmdtyConsumed, double discountFactorFromCmdtySettlement, Func<Day, double> discountToCurrentDay) 
            where T : ITimePeriod<T>
        {
            double inventoryAfterTriggerVolume = expectedInventory + triggerVolume - inventoryLoss;
            double triggerVolumeContinuationValue = AverageContinuationValue(inventoryAfterTriggerVolume, inventoryGridNexPeriod, regressContinuationValues);
            double triggerVolumeContinuationValueChange = triggerVolumeContinuationValue - alternativeContinuationValue;

            double triggerVolumeExcessVolume = triggerVolume - alternativeVolume;
            double triggerVolumeInjectWithdrawCostChange =
                InjectWithdrawCostNpv(storage, triggerVolume, period, expectedInventory, discountToCurrentDay) // This will be positive value
                - alternativeDecisionCost;
            double cmdtyConsumedCostChange = CmdtyVolumeConsumedOnDecision(storage, triggerVolume, period, expectedInventory) - alternativeCmdtyConsumed;

            double triggerPrice = (triggerVolumeContinuationValueChange - triggerVolumeInjectWithdrawCostChange) /
                                        (discountFactorFromCmdtySettlement * (triggerVolumeExcessVolume + cmdtyConsumedCostChange));
            return triggerPrice;
        }

        private static double[] CalcInjectTriggerPriceVolumes<T>(double maxInjectVolume, double alternativeVolume, int numTriggerPriceVolumes)
            where T : ITimePeriod<T>
        {
            double triggerVolumeIncrement = (maxInjectVolume - alternativeVolume) / numTriggerPriceVolumes;
            var triggerPriceVolumes = new double[numTriggerPriceVolumes];
            triggerPriceVolumes[numTriggerPriceVolumes - 1] = maxInjectVolume; // Use exact volume directly to avoid floating point error
            for (int i = 1; i < numTriggerPriceVolumes; i++)
                triggerPriceVolumes[i - 1] = alternativeVolume + i * triggerVolumeIncrement;
            return triggerPriceVolumes;
        }

        private static double[] CalcWithdrawTriggerPriceVolumes<T>(double maxWithdrawVolume, double alternativeVolume, int numTriggerPriceVolumes)
            where T : ITimePeriod<T>
        {
            double triggerVolumeIncrement = (alternativeVolume - maxWithdrawVolume) / numTriggerPriceVolumes;
            var triggerPriceVolumes = new double[numTriggerPriceVolumes];
            for (int i = 0; i < numTriggerPriceVolumes; i++)
                triggerPriceVolumes[i] = maxWithdrawVolume + i * triggerVolumeIncrement;
            return triggerPriceVolumes;
        }

        private static (double alternativeContinuationValue, double alternativeDecisionCost, double alternativeCmdtyConsumed) CalcAlternatives<T>(
            ICmdtyStorage<T> storage, double expectedInventory, double alternativeVolume, double inventoryLoss, double[] inventoryGridNexPeriod,
            Vector<double>[] regressContinuationValues, T period, Func<Day, double> discountToPresent) where T : ITimePeriod<T>
        {
            double inventoryAfterAlternative = expectedInventory + alternativeVolume - inventoryLoss;
            double alternativeContinuationValue = AverageContinuationValue(inventoryAfterAlternative, inventoryGridNexPeriod, regressContinuationValues);
            double alternativeDecisionCost = InjectWithdrawCostNpv(storage, alternativeVolume, period, expectedInventory, discountToPresent);
            double alternativeCmdtyConsumed = CmdtyVolumeConsumedOnDecision(storage, alternativeVolume, period, expectedInventory);
            return (alternativeContinuationValue, alternativeDecisionCost, alternativeCmdtyConsumed);
        }

        private static double CmdtyVolumeConsumedOnDecision<T>(ICmdtyStorage<T> storage, double decisionVolume, T period, double inventory) 
            where T : ITimePeriod<T>
        {
            return decisionVolume > 0.0
                ? storage.CmdtyVolumeConsumedOnInject(period, inventory, decisionVolume)
                : storage.CmdtyVolumeConsumedOnWithdraw(period, inventory, -decisionVolume);
        }

        private static double InjectWithdrawCostNpv<T>(ICmdtyStorage<T> storage, double decisionVolume, T period, double inventory,
                                            Func<Day, double> discountToPresent) 
            where T : ITimePeriod<T>
        {
            IReadOnlyList<DomesticCashFlow> injectWithdrawCostCostCashFlows = decisionVolume > 0.0
                ? storage.InjectionCost(period, inventory, decisionVolume)
                : storage.WithdrawalCost(period, inventory, -decisionVolume);
            double injectWithdrawCostNpv = injectWithdrawCostCostCashFlows.Sum(cashFlow => cashFlow.Amount * discountToPresent(cashFlow.Date));
            return injectWithdrawCostNpv;
        }

        private static double Average(Span<double> span)
        {
            double sum = 0.0;
            // ReSharper disable once ForCanBeConvertedToForeach
            for (int i = 0; i < span.Length; i++)
                sum += span[i];
            return sum/span.Length;
        }

        private static double AverageContinuationValue(double inventoryAfterDecision, double[] inventoryGrid,
            Vector<double>[] storageRegressValuesNextPeriod)
        {
            (int lowerInventoryIndex, int upperInventoryIndex) = StorageHelper.BisectInventorySpace(inventoryGrid, inventoryAfterDecision);

            if (lowerInventoryIndex == upperInventoryIndex)
                return storageRegressValuesNextPeriod[lowerInventoryIndex].Average();

            double lowerInventory = inventoryGrid[lowerInventoryIndex];
            if (Math.Abs(inventoryAfterDecision - lowerInventory) < FloatingPointTol)
                return storageRegressValuesNextPeriod[lowerInventoryIndex].Average();

            double upperInventory = inventoryGrid[upperInventoryIndex];
            if (Math.Abs(inventoryAfterDecision - upperInventory) < FloatingPointTol)
                return storageRegressValuesNextPeriod[upperInventoryIndex].Average();

            double inventoryGridSpace = upperInventory - lowerInventory;
            double lowerWeight = (upperInventory - inventoryAfterDecision) / inventoryGridSpace;
            double upperWeight = 1.0 - lowerWeight;

            Vector<double> lowerStorageRegressValues = storageRegressValuesNextPeriod[lowerInventoryIndex];
            Vector<double> upperStorageRegressValues = storageRegressValuesNextPeriod[upperInventoryIndex];
            Vector<double> weightedAverageStorageRegressValues =
                lowerStorageRegressValues * lowerWeight + upperStorageRegressValues * upperWeight;

            return weightedAverageStorageRegressValues.Average();
        }

        private static double InterpolateContinuationValue(double inventoryAfterDecision, double[] inventoryGrid, 
                            Vector<double>[] storageRegressValuesNextPeriod, int simIndex)
        {
            // TODO look into the efficiency of memory access in this method and think about reordering dimension of arrays
            (int lowerInventoryIndex, int upperInventoryIndex) = StorageHelper.BisectInventorySpace(inventoryGrid, inventoryAfterDecision);

            if (lowerInventoryIndex == upperInventoryIndex)
                return storageRegressValuesNextPeriod[lowerInventoryIndex][simIndex];

            double lowerInventory = inventoryGrid[lowerInventoryIndex];
            if (Math.Abs(inventoryAfterDecision - lowerInventory) < FloatingPointTol)
                return storageRegressValuesNextPeriod[lowerInventoryIndex][simIndex];

            double upperInventory = inventoryGrid[upperInventoryIndex];
            if (Math.Abs(inventoryAfterDecision - upperInventory) < FloatingPointTol)
                return storageRegressValuesNextPeriod[upperInventoryIndex][simIndex];

            double inventoryGridSpace = upperInventory - lowerInventory;
            double lowerWeight = (upperInventory - inventoryAfterDecision) / inventoryGridSpace;
            double upperWeight = 1.0 - lowerWeight;

            double lowerStorageRegressValue = storageRegressValuesNextPeriod[lowerInventoryIndex][simIndex];
            double upperStorageRegressValue = storageRegressValuesNextPeriod[upperInventoryIndex][simIndex];

            return lowerStorageRegressValue * lowerWeight + upperStorageRegressValue * upperWeight;
        }

        private static Vector<double> WeightedAverage<T>(Vector<double> vector1,
            double weight1, Vector<double> vector2, double weight2, Vector<double> upperWeightedBuffer) where T : ITimePeriod<T>
        {
            Vector<double> interpolatedRegressContinuationValue = Vector<double>.Build.Dense(vector1.Count);
            vector1.Multiply(weight1, interpolatedRegressContinuationValue);
            vector2.Multiply(weight2, upperWeightedBuffer);
            upperWeightedBuffer.Add(interpolatedRegressContinuationValue, interpolatedRegressContinuationValue);
            return interpolatedRegressContinuationValue;
        }

        public static void PopulateDesignMatrix<T>(Matrix<double> designMatrix, T period, ISpotSimResults<T> spotSims,
            IReadOnlyList<BasisFunction> basisFunctions)
            where T : ITimePeriod<T>
        {
            ReadOnlySpan<double> spotPrices = spotSims.SpotPricesForPeriod(period).Span;
            int numSims = spotSims.NumSims;
            int numFactors = spotSims.NumFactors;
            ReadOnlyMemory<double>[] markovFactors = new ReadOnlyMemory<double>[numFactors];
            for (int i = 0; i < numFactors; i++)
                markovFactors[i] = spotSims.MarkovFactorsForPeriod(period, i);

            for (int basisIndex = 0; basisIndex < basisFunctions.Count; basisIndex++)
            {
                Span<double> designMatrixColumn = new Span<double>(designMatrix.AsColumnMajorArray(), basisIndex * numSims, numSims);
                BasisFunction basisFunction = basisFunctions[basisIndex];
                basisFunction(markovFactors, spotPrices, designMatrixColumn);
            }
        }

        private sealed class Stopwatches
        {
            public Stopwatch All { get; }
            public Stopwatch PriceSimulation { get; }
            public Stopwatch BackwardInduction { get; }
            public Stopwatch PseudoInverse { get; }
            public Stopwatch ForwardSimulation { get; }
            public Stopwatch TriggerPriceCalc { get; }

            public Stopwatches()
            {
                All = new Stopwatch();
                PriceSimulation = new Stopwatch();
                BackwardInduction = new Stopwatch();
                PseudoInverse = new Stopwatch();
                ForwardSimulation = new Stopwatch();
                TriggerPriceCalc = new Stopwatch();
            }

            public string GenerateProfileReport()
            {
                var stringBuilder = new StringBuilder();
                TimeSpan otherAll = All.Elapsed - PriceSimulation.Elapsed - BackwardInduction.Elapsed -
                                 ForwardSimulation.Elapsed - TriggerPriceCalc.Elapsed;
                TimeSpan otherBackwardInduction = BackwardInduction.Elapsed - PseudoInverse.Elapsed;

                string priceSimPercent =
                    (PriceSimulation.Elapsed.Ticks / (double) All.Elapsed.Ticks).ToString("P2", CultureInfo.InvariantCulture);
                string pseudoInversePercent =
                    (PseudoInverse.Elapsed.Ticks / (double)All.Elapsed.Ticks).ToString("P2", CultureInfo.InvariantCulture);
                string otherBackInductionPercent =
                    (otherBackwardInduction.Ticks / (double)All.Elapsed.Ticks).ToString("P2", CultureInfo.InvariantCulture);
                string forwardSimPercent =
                    (ForwardSimulation.Elapsed.Ticks / (double)All.Elapsed.Ticks).ToString("P2", CultureInfo.InvariantCulture);
                string triggerPricePercent =
                    (TriggerPriceCalc.Elapsed.Ticks / (double)All.Elapsed.Ticks).ToString("P2", CultureInfo.InvariantCulture);
                string otherPercent = (otherAll.Ticks / (double)All.Elapsed.Ticks).ToString("P2", CultureInfo.InvariantCulture);


                stringBuilder.AppendLine("Total:\t\t" + All.Elapsed.ToString("g", CultureInfo.InvariantCulture));
                stringBuilder.AppendLine($"Price sim:\t{PriceSimulation.Elapsed.ToString("g", CultureInfo.InvariantCulture)}\t({priceSimPercent})");
                stringBuilder.AppendLine($"Pseudo-inverse:\t{PseudoInverse.Elapsed.ToString("g", CultureInfo.InvariantCulture)}\t({pseudoInversePercent})");
                stringBuilder.AppendLine($"Other back ind:\t{otherBackwardInduction.ToString("g", CultureInfo.InvariantCulture)}\t({otherBackInductionPercent})");
                stringBuilder.AppendLine($"Fwd sim:\t{ForwardSimulation.Elapsed.ToString("g", CultureInfo.InvariantCulture)}\t({forwardSimPercent})");
                stringBuilder.AppendLine($"Trigger prices:\t{TriggerPriceCalc.Elapsed.ToString("g", CultureInfo.InvariantCulture)}\t({triggerPricePercent})");
                stringBuilder.AppendLine($"Other:\t\t{otherAll.ToString("g", CultureInfo.InvariantCulture)}\t({otherPercent})");

                return stringBuilder.ToString();
            }

        }

    }
}
