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
using System.Linq;
using System.Threading;
using Cmdty.Core.Common;
using Cmdty.Core.Simulation;
using Cmdty.Core.Simulation.MultiFactor;
using Cmdty.TimePeriodValueTypes;
using Cmdty.TimeSeries;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace Cmdty.Storage
{
    public static class LsmcStorageValuation
    {
        private const double FloatingPointTol = 1E-8;   // TODO better way to pick this.
        // This has been very roughly estimated. Probably there is a better way of splitting up progress by estimating the order of the backward and forward components.
        private const double BackwardPcntTime = 0.96;


        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance,
            MultiFactorParameters<T> modelParameters,
            int numSims,
            int? seed,
            IEnumerable<BasisFunction> basisFunctions,
            Action<double> onProgressUpdate = null)
            where T : ITimePeriod<T>
        {
            return Calculate(currentPeriod, startingInventory, forwardCurve, storage, settleDateRule, discountFactors,
                gridCalc, numericalTolerance, modelParameters, numSims, seed, basisFunctions, CancellationToken.None, onProgressUpdate);
        }
        
        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance,
            MultiFactorParameters<T> modelParameters,
            int numSims,
            int? seed,
            IEnumerable<BasisFunction> basisFunctions,
            CancellationToken cancellationToken,
            Action<double> onProgressUpdate = null)
            where T : ITimePeriod<T>
        {
            var normalGenerator = seed == null ? new MersenneTwisterGenerator(true) :
                new MersenneTwisterGenerator(seed.Value, true);
            return Calculate(currentPeriod, startingInventory, forwardCurve, storage, settleDateRule, discountFactors,
                gridCalc, numericalTolerance, modelParameters, normalGenerator, numSims, basisFunctions, 
                cancellationToken, onProgressUpdate);
        }

        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance,
            MultiFactorParameters<T> modelParameters,
            IStandardNormalGenerator normalGenerator,
            int numSims,
            IEnumerable<BasisFunction> basisFunctions,
            Action<double> onProgressUpdate = null)
            where T : ITimePeriod<T>
        {
            return Calculate(currentPeriod, startingInventory, forwardCurve, storage, settleDateRule, discountFactors,
                gridCalc, numericalTolerance, modelParameters,
                normalGenerator, numSims, basisFunctions, CancellationToken.None,
                onProgressUpdate);
        }


        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance,
            MultiFactorParameters<T> modelParameters,
            IStandardNormalGenerator normalGenerator,
            int numSims,
            IEnumerable<BasisFunction> basisFunctions,
            CancellationToken cancellationToken,
            Action<double> onProgressUpdate = null)
            where T : ITimePeriod<T>
        {
            if (currentPeriod.CompareTo(storage.EndPeriod) > 0)
            {
                onProgressUpdate?.Invoke(1.0);
                return LsmcStorageValuationResults<T>.CreateExpiredResults();
            }
            // TODO progress update for price simulation?
            // TODO allow intraday simulation?
            MultiFactorSpotSimResults<T> spotSims;
            if (currentPeriod.Equals(storage.EndPeriod))
            {
                // TODO think of more elegant way of doing this
                spotSims = new MultiFactorSpotSimResults<T>(new double[0], 
                    new double[0], new T[0], 0, numSims, modelParameters.NumFactors);
            }
            else
            {
                DateTime currentDate = currentPeriod.Start; // TODO IMPORTANT, this needs to change;
                T simStart = new[] { currentPeriod.Offset(1), storage.StartPeriod }.Max();
                var simulatedPeriods = simStart.EnumerateTo(storage.EndPeriod);
                var simulator = new MultiFactorSpotPriceSimulator<T>(modelParameters, currentDate, forwardCurve, simulatedPeriods, TimeFunctions.Act365, normalGenerator);
                spotSims = simulator.Simulate(numSims);
            }

            return Calculate(currentPeriod, startingInventory, forwardCurve, storage, settleDateRule, discountFactors,
                gridCalc, numericalTolerance, spotSims, basisFunctions, cancellationToken, onProgressUpdate);
        }

        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance, ISpotSimResults<T> spotSims,
            IEnumerable<BasisFunction> basisFunctions,
            Action<double> onProgressUpdate = null)
            where T : ITimePeriod<T>
        {
            return Calculate(currentPeriod, startingInventory,
                forwardCurve, storage, settleDateRule,
                discountFactors,
                gridCalc,
                numericalTolerance, spotSims, basisFunctions,
                CancellationToken.None, onProgressUpdate);
        }

        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance, ISpotSimResults<T> spotSims,
            IEnumerable<BasisFunction> basisFunctions,
            CancellationToken cancellationToken,
            Action<double> onProgressUpdate = null)
            where T : ITimePeriod<T>
        {
            // TODO check spotSims is consistent with storage
            if (startingInventory < 0)
                throw new ArgumentException("Inventory cannot be negative.", nameof(startingInventory));

            if (currentPeriod.CompareTo(storage.EndPeriod) > 0)
            {
                onProgressUpdate?.Invoke(1.0);
                return LsmcStorageValuationResults<T>.CreateExpiredResults();
            }

            if (currentPeriod.Equals(storage.EndPeriod))
            {
                if (storage.MustBeEmptyAtEnd)
                {
                    if (startingInventory > 0)
                        throw new InventoryConstraintsCannotBeFulfilledException("Storage must be empty at end, but inventory is greater than zero.");
                    onProgressUpdate?.Invoke(1.0);
                    return LsmcStorageValuationResults<T>.CreateExpiredResults();
                }
                // Potentially P&L at end
                double spotPrice = forwardCurve[currentPeriod];
                double npv = storage.TerminalStorageNpv(spotPrice, startingInventory);
                onProgressUpdate?.Invoke(1.0);
                return LsmcStorageValuationResults<T>.CreateEndPeriodResults(npv);
            }

            var basisFunctionList = basisFunctions.ToList();

            TimeSeries<T, InventoryRange> inventorySpace = StorageHelper.CalculateInventorySpace(storage, startingInventory, currentPeriod);

            if (forwardCurve.Start.CompareTo(currentPeriod) > 0)
                throw new ArgumentException("Forward curve starts too late. Must start on or before the current period.", nameof(forwardCurve));

            if (forwardCurve.End.CompareTo(inventorySpace.End) < 0)
                throw new ArgumentException("Forward curve does not extend until storage end period.", nameof(forwardCurve));

            // Perform backward induction
            int numPeriods = inventorySpace.Count + 1; // +1 as inventorySpaceGrid doesn't contain first period
            var storageRegressValuesByPeriod = new Vector<double>[numPeriods][]; // 1st dimension is period, 2nd is inventory, 3rd is simulation number
            var inventorySpaceGrids = new double[numPeriods][];

            // Calculate NPVs at end period
            var endInventorySpace = inventorySpace[storage.EndPeriod]; // TODO this will probably break!
            var endInventorySpaceGrid = gridCalc.GetGridPoints(endInventorySpace.MinInventory, endInventorySpace.MaxInventory)
                                            .ToArray();
            inventorySpaceGrids[numPeriods - 1] = endInventorySpaceGrid;

            var storageActualValuesNextPeriod = new Vector<double>[endInventorySpaceGrid.Length];
            ReadOnlySpan<double> endPeriodSimSpotPrices = spotSims.SpotPricesForPeriod(storage.EndPeriod).Span;

            int numSims = spotSims.NumSims;

            for (int i = 0; i < endInventorySpaceGrid.Length; i++)
            {
                double inventory = endInventorySpaceGrid[i];
                var storageValueBySim = new DenseVector(numSims);
                for (int simIndex = 0; simIndex < numSims; simIndex++)
                {
                    double simSpotPrice = endPeriodSimSpotPrices[simIndex];
                    storageValueBySim[simIndex] = storage.TerminalStorageNpv(simSpotPrice, inventory);
                }
                storageActualValuesNextPeriod[i] = storageValueBySim;
            }
            
            // Calculate discount factor function
            Day dayToDiscountTo = currentPeriod.First<Day>(); // TODO IMPORTANT, this needs to change
            
            // Memoize the discount factor
            var discountFactorCache = new Dictionary<Day, double>(); // TODO do this in more elegant way and share with intrinsic calc
            double DiscountToCurrentDay(Day cashFlowDate)
            {
                if (!discountFactorCache.TryGetValue(cashFlowDate, out double discountFactor))
                {
                    discountFactor = discountFactors(dayToDiscountTo, cashFlowDate);
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

            foreach (T period in periodsForResultsTimeSeries.Reverse().Skip(1))
            {
                double[] nextPeriodInventorySpaceGrid = inventorySpaceGrids[backCounter + 1];
                Vector<double>[] storageRegressValuesNextPeriod = new Vector<double>[nextPeriodInventorySpaceGrid.Length];

                if (period.Equals(currentPeriod))
                {
                    // Current period, for which the price isn't random so expected storage values are just the average of the values for all sims
                    for (int i = 0; i < nextPeriodInventorySpaceGrid.Length; i++) // TODO parallelise 
                    {
                        Vector<double> storageValuesBySimNextPeriod = storageActualValuesNextPeriod[i];
                        double expectedStorageValueNextPeriod = storageValuesBySimNextPeriod.Average();
                        storageRegressValuesNextPeriod[i] = Vector<double>.Build.Dense(numSims, expectedStorageValueNextPeriod); // TODO this is a bit inefficent, review
                    }
                }
                else
                {
                    PopulateDesignMatrix(designMatrix, period, spotSims, basisFunctionList);
                    Matrix<double> pseudoInverse = designMatrix.PseudoInverse();

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
                {
                    inventorySpaceGrid = new[] { startingInventory };
                }
                else
                {
                    (double inventorySpaceMin, double inventorySpaceMax) = inventorySpace[period];
                    inventorySpaceGrid = gridCalc.GetGridPoints(inventorySpaceMin, inventorySpaceMax)
                                                .ToArray();
                }
                (double nextStepInventorySpaceMin, double nextStepInventorySpaceMax) = inventorySpace[period.Offset(1)];

                var storageActualValuesThisPeriod = new Vector<double>[inventorySpaceGrid.Length]; // TODO change type to DenseVector?

                Day cmdtySettlementDate = settleDateRule(period);
                double discountFactorFromCmdtySettlement = DiscountToCurrentDay(cmdtySettlementDate);

                ReadOnlySpan<double> simulatedPrices;
                if (period.Equals(currentPeriod))
                {
                    double spotPrice = forwardCurve[period];
                    simulatedPrices = Enumerable.Repeat(spotPrice, numSims).ToArray(); // TODO inefficient - review.
                }                
                else
                    simulatedPrices = spotSims.SpotPricesForPeriod(period).Span;

                for (int inventoryIndex = 0; inventoryIndex < inventorySpaceGrid.Length; inventoryIndex++)
                {
                    double inventory = inventorySpaceGrid[inventoryIndex];
                    InjectWithdrawRange injectWithdrawRange = storage.GetInjectWithdrawRange(period, inventory);
                    double inventoryLoss = storage.CmdtyInventoryPercentLoss(period) * inventory;
                    double[] decisionSet = StorageHelper.CalculateBangBangDecisionSet(injectWithdrawRange, inventory, inventoryLoss,
                        nextStepInventorySpaceMin, nextStepInventorySpaceMax, numericalTolerance);
                    IReadOnlyList<DomesticCashFlow> inventoryCostCashFlows = storage.CmdtyInventoryCost(period, inventory);
                    double inventoryCostNpv = inventoryCostCashFlows.Sum(cashFlow => cashFlow.Amount * DiscountToCurrentDay(cashFlow.Date));

                    double[] injectWithdrawCostNpvs = new double[decisionSet.Length];
                    double[] cmdtyUsedForInjectWithdrawVolume = new double[decisionSet.Length];
                    
                    var regressionContinuationValueByDecisionSet = new Vector<double>[decisionSet.Length];
                    var actualContinuationValueByDecisionSet = new Vector<double>[decisionSet.Length];
                    for (int decisionIndex = 0; decisionIndex < decisionSet.Length; decisionIndex++)
                    {
                        double decisionVolume = decisionSet[decisionIndex];

                        // Inject/Withdraw cost (same for all price sims)
                        IReadOnlyList<DomesticCashFlow> injectWithdrawCostCostCashFlows = decisionVolume > 0.0
                            ? storage.InjectionCost(period, inventory, decisionVolume)
                            : storage.WithdrawalCost(period, inventory, -decisionVolume);

                        double injectWithdrawCostNpv = injectWithdrawCostCostCashFlows.Sum(cashFlow => cashFlow.Amount * DiscountToCurrentDay(cashFlow.Date));
                        injectWithdrawCostNpvs[decisionIndex] = injectWithdrawCostNpv;

                        // Cmdty Used For Inject/Withdraw (same for all price sims)
                        cmdtyUsedForInjectWithdrawVolume[decisionIndex] = decisionVolume > 0.0
                            ? storage.CmdtyVolumeConsumedOnInject(period, inventory, decisionVolume)
                            : storage.CmdtyVolumeConsumedOnWithdraw(period, inventory, -decisionVolume);

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
                onProgressUpdate?.Invoke(progress);
                cancellationToken.ThrowIfCancellationRequested();
            }
            
            var inventoryBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var injectWithdrawVolumeBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var cmdtyConsumedBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var inventoryLossBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var netVolumeBySim = new Panel<T, double>(periodsForResultsTimeSeries, numSims);
            var storageProfiles = new StorageProfile[periodsForResultsTimeSeries.Length];

            var deltas = new double[periodsForResultsTimeSeries.Length];

            var startingInventories = inventoryBySim[0];
            for (int i = 0; i < numSims; i++)
                startingInventories[i] = startingInventory;

            double forwardStepProgressPcnt = (1.0 - BackwardPcntTime) / periodsForResultsTimeSeries.Length;
            for (int periodIndex = 0; periodIndex < periodsForResultsTimeSeries.Length - 1; periodIndex++) // TODO more clearly handle this -1
            {
                T period = periodsForResultsTimeSeries[periodIndex];
                Span<double> nextPeriodInventories = inventoryBySim[periodIndex + 1];

                Vector<double>[] regressContinuationValues = storageRegressValuesByPeriod[periodIndex + 1];
                double[] inventoryGridNexPeriod = inventorySpaceGrids[periodIndex + 1];
                Day cmdtySettlementDate = settleDateRule(period);
                double discountFactorFromCmdtySettlement = DiscountToCurrentDay(cmdtySettlementDate);

                double sumSpotPriceTimesVolume = 0.0;

                ReadOnlySpan<double> simulatedPrices;
                if (period.Equals(currentPeriod))
                {
                    double spotPrice = forwardCurve[period];
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
                for (int simIndex = 0; simIndex < numSims; simIndex++)
                {
                    double simulatedSpotPrice = simulatedPrices[simIndex];
                    double inventory = thisPeriodInventories[simIndex];

                    InjectWithdrawRange injectWithdrawRange = storage.GetInjectWithdrawRange(period, inventory);
                    double inventoryLoss = storage.CmdtyInventoryPercentLoss(period) * inventory;
                    double[] decisionSet = StorageHelper.CalculateBangBangDecisionSet(injectWithdrawRange, inventory,
                        inventoryLoss,
                        nextStepInventorySpaceMin, nextStepInventorySpaceMax, numericalTolerance);
                    IReadOnlyList<DomesticCashFlow> inventoryCostCashFlows =
                        storage.CmdtyInventoryCost(period, inventory);
                    double inventoryCostNpv = inventoryCostCashFlows.Sum(cashFlow =>
                        cashFlow.Amount * DiscountToCurrentDay(cashFlow.Date));

                    var decisionNpvsRegress = new double[decisionSet.Length];
                    var cmdtyUsedForInjectWithdrawVolumes = new double[decisionSet.Length];
                    for (var decisionIndex = 0; decisionIndex < decisionSet.Length; decisionIndex++)
                    {
                        double decisionVolume = decisionSet[decisionIndex];
                        double inventoryAfterDecision = inventory + decisionVolume - inventoryLoss;

                        double cmdtyUsedForInjectWithdrawVolume = decisionVolume > 0.0
                            ? storage.CmdtyVolumeConsumedOnInject(period, inventory, decisionVolume)
                            : storage.CmdtyVolumeConsumedOnWithdraw(period, inventory, -decisionVolume);

                        double injectWithdrawNpv = -decisionVolume * simulatedSpotPrice * discountFactorFromCmdtySettlement;
                        double cmdtyUsedForInjectWithdrawNpv = -cmdtyUsedForInjectWithdrawVolume * simulatedSpotPrice *
                                                               discountFactorFromCmdtySettlement;

                        IReadOnlyList<DomesticCashFlow> injectWithdrawCostCostCashFlows = decisionVolume > 0.0
                            ? storage.InjectionCost(period, inventory, decisionVolume)
                            : storage.WithdrawalCost(period, inventory, -decisionVolume);
                        double injectWithdrawCostNpv = injectWithdrawCostCostCashFlows.Sum(cashFlow => cashFlow.Amount * DiscountToCurrentDay(cashFlow.Date));

                        double immediateNpv = injectWithdrawNpv - injectWithdrawCostNpv + cmdtyUsedForInjectWithdrawNpv;

                        double continuationValue =
                            InterpolateContinuationValue(inventoryAfterDecision, inventoryGridNexPeriod, regressContinuationValues, simIndex);

                        double totalNpv = immediateNpv + continuationValue - inventoryCostNpv; // TODO IMPORTANT check if inventoryCostNpv should be subtracted;
                        decisionNpvsRegress[decisionIndex] = totalNpv;
                        cmdtyUsedForInjectWithdrawVolumes[decisionIndex] = cmdtyUsedForInjectWithdrawVolume;
                    }
                    (double optimalRegressDecisionNpv, int indexOfOptimalDecision) = StorageHelper.MaxValueAndIndex(decisionNpvsRegress);
                    double optimalDecisionVolume = decisionSet[indexOfOptimalDecision];
                    double optimalNextStepInventory = inventory + optimalDecisionVolume - inventoryLoss;
                    nextPeriodInventories[simIndex] = optimalNextStepInventory;

                    double optimalCmdtyUsedForInjectWithdrawVolume = cmdtyUsedForInjectWithdrawVolumes[indexOfOptimalDecision];

                    sumSpotPriceTimesVolume += -(optimalDecisionVolume + optimalCmdtyUsedForInjectWithdrawVolume) * simulatedSpotPrice;

                    thisPeriodInjectWithdrawVolumes[simIndex] = optimalDecisionVolume;
                    thisPeriodCmdtyConsumed[simIndex] = optimalCmdtyUsedForInjectWithdrawVolume;
                    thisPeriodInventoryLoss[simIndex] = inventoryLoss;
                    thisPeriodNetVolume[simIndex] = -optimalDecisionVolume - optimalCmdtyUsedForInjectWithdrawVolume;
                }

                storageProfiles[periodIndex] = new StorageProfile(Average(thisPeriodInventories), Average(thisPeriodInjectWithdrawVolumes),
                    Average(thisPeriodCmdtyConsumed), Average(thisPeriodInventoryLoss), Average(thisPeriodNetVolume));
                double forwardPrice = forwardCurve[period];
                double periodDelta = sumSpotPriceTimesVolume / forwardPrice / numSims;
                deltas[periodIndex] = periodDelta;
                progress += forwardStepProgressPcnt;
                onProgressUpdate?.Invoke(progress);
                cancellationToken.ThrowIfCancellationRequested();
            }

            double expectedFinalInventory = Average(inventoryBySim[inventoryBySim.NumRows - 1]);
            // Profile at storage end when no decisions can happen
            storageProfiles[storageProfiles.Length - 1] = new StorageProfile(expectedFinalInventory, 0.0, 0.0, 0.0, 0.0);

            // TODO calculate PV from forward loop and compare?
            // Calculate NPVs for first active period using current inventory
            // TODO this is unnecessarily introducing floating point error if the val date is during the storage active period and there should not be a Vector of simulated spot prices
            double storageNpv = storageActualValuesNextPeriod[0].Average();

            var deltasSeries = new DoubleTimeSeries<T>(periodsForResultsTimeSeries[0], deltas);
            var storageProfileSeries = new TimeSeries<T, StorageProfile>(periodsForResultsTimeSeries[0], storageProfiles);
            onProgressUpdate?.Invoke(1.0); // Progress with approximately 1.0 should have occured already, but might have been a bit off because of floating-point error.


            // Calculate trigger prices
            int numTriggerPrices = 3; // TODO move this to the parameters
            var triggerPricePairs = new TriggerPricePair[numTriggerPrices];
            for (int periodIndex = 0; periodIndex < numTriggerPrices; periodIndex++)
            {
                T period = periodsForResultsTimeSeries[periodIndex];
                Vector<double>[] regressContinuationValues = storageRegressValuesByPeriod[periodIndex + 1];
                double[] inventoryGridNexPeriod = inventorySpaceGrids[periodIndex + 1];

                double expectedInventory = storageProfileSeries[period].Inventory;
                (double nextStepInventorySpaceMin, double nextStepInventorySpaceMax) = inventorySpace[period.Offset(1)];
                InjectWithdrawRange injectWithdrawRange = storage.GetInjectWithdrawRange(period, expectedInventory);
                double inventoryLoss = storage.CmdtyInventoryPercentLoss(period) * expectedInventory;
                double[] decisionSet = StorageHelper.CalculateBangBangDecisionSet(injectWithdrawRange, expectedInventory,
                    inventoryLoss, nextStepInventorySpaceMin, nextStepInventorySpaceMax, numericalTolerance);

                double maxInjectVolume = decisionSet.Max();
                TriggerPriceInfo injectTriggerPriceInfo = null;
                if (maxInjectVolume > 0)
                {
                    double alternativeVolume = decisionSet.OrderByDescending(d => d).ElementAt(1); // Second highest decision volume, usually will be zero, but might not due to forced injection
                    if (maxInjectVolume > alternativeVolume)
                    {

                    }
                }

                double maxWithdraw = decisionSet.Min();
                TriggerPriceInfo withdrawTriggerPriceInfo = null;
                if (maxWithdraw < 0)
                {
                    double alternativeVolume = decisionSet.OrderBy(d => d).ElementAt(1); // Second lowest decision volume, usually will be zero, but might not due to forced withdrawal
                    if (maxWithdraw < alternativeVolume)
                    {

                    }
                }

                triggerPricePairs[periodIndex] = new TriggerPricePair(injectTriggerPriceInfo, withdrawTriggerPriceInfo);
            }
            var triggerPrices = new TimeSeries<T, TriggerPricePair>(periodsForResultsTimeSeries.Take(numTriggerPrices), triggerPricePairs);

            var spotPricePanel = Panel.UseRawDataArray(spotSims.SpotPrices, spotSims.SimulatedPeriods, numSims);
            return new LsmcStorageValuationResults<T>(storageNpv, deltasSeries, storageProfileSeries, spotPricePanel, 
                inventoryBySim, injectWithdrawVolumeBySim, cmdtyConsumedBySim, inventoryLossBySim, netVolumeBySim, triggerPrices);
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
            Vector<double>[] storageRegressValuesNextPeriod) // TODO should this be the regress or actual storage values
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

    }
}
