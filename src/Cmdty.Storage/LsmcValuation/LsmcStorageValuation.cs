//#define RUN_DELTAS
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

        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance,
            MultiFactorParameters<T> modelParameters,
            int numSims,
            int? seed,
            int regressMaxPolyDegree, bool regressCrossProducts)
            where T : ITimePeriod<T>
        {
            var normalGenerator = seed == null ? new MersenneTwisterGenerator(true) : 
                new MersenneTwisterGenerator(seed.Value, true);
            return Calculate(currentPeriod, startingInventory, forwardCurve, storage, settleDateRule, discountFactors,
                gridCalc, numericalTolerance, modelParameters, normalGenerator, numSims, regressMaxPolyDegree,
                regressCrossProducts);
        }

        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance,
            MultiFactorParameters<T> modelParameters,
            IStandardNormalGenerator normalGenerator,
            int numSims,
            int regressMaxPolyDegree, bool regressCrossProducts)
            where T : ITimePeriod<T>
        {
            if (currentPeriod.CompareTo(storage.EndPeriod) > 0)
                return LsmcStorageValuationResults<T>.CreateExpiredResults();

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
                gridCalc, numericalTolerance, spotSims, regressMaxPolyDegree, regressCrossProducts);
        }

        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            IDoubleStateSpaceGridCalc gridCalc,
            double numericalTolerance, ISpotSimResults<T> spotSims, int regressMaxPolyDegree, bool regressCrossProducts)
            where T : ITimePeriod<T>
        {
            // TODO check spotSims is consistent with storage
            if (startingInventory < 0)
                throw new ArgumentException("Inventory cannot be negative.", nameof(startingInventory));

            if (currentPeriod.CompareTo(storage.EndPeriod) > 0)
                return LsmcStorageValuationResults<T>.CreateExpiredResults();

            if (currentPeriod.Equals(storage.EndPeriod))
            {
                if (storage.MustBeEmptyAtEnd)
                {
                    if (startingInventory > 0)
                        throw new InventoryConstraintsCannotBeFulfilledException("Storage must be empty at end, but inventory is greater than zero.");
                    return LsmcStorageValuationResults<T>.CreateExpiredResults();
                }
                // Potentially P&L at end
                double spotPrice = forwardCurve[currentPeriod];
                double npv = storage.TerminalStorageNpv(spotPrice, startingInventory);
                return LsmcStorageValuationResults<T>.CreateEndPeriodResults(npv);
            }
            
            TimeSeries<T, InventoryRange> inventorySpace = StorageHelper.CalculateInventorySpace(storage, startingInventory, currentPeriod);

            if (forwardCurve.Start.CompareTo(currentPeriod) > 0)
                throw new ArgumentException("Forward curve starts too late. Must start on or before the current period.", nameof(forwardCurve));

            if (forwardCurve.End.CompareTo(inventorySpace.End) < 0)
                throw new ArgumentException("Forward curve does not extend until storage end period.", nameof(forwardCurve));

            // Perform backward induction
            int numPeriods = inventorySpace.Count + 1; // +1 as inventorySpaceGrid doesn't contain first period
            var storageValuesByPeriod = new Vector<double>[numPeriods][]; // 1st dimension is period, 2nd is inventory, 3rd is simulation number
            var inventorySpaceGrids = new double[numPeriods][];

            // Calculate NPVs at end period
            var endInventorySpace = inventorySpace[storage.EndPeriod]; // TODO this will probably break!
            var endInventorySpaceGrid = gridCalc.GetGridPoints(endInventorySpace.MinInventory, endInventorySpace.MaxInventory)
                                            .ToArray();
            inventorySpaceGrids[numPeriods - 1] = endInventorySpaceGrid;

            var storageValuesEndPeriod = new Vector<double>[endInventorySpaceGrid.Length];
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
                storageValuesEndPeriod[i] = storageValueBySim;
            }
            storageValuesByPeriod[numPeriods - 1] = storageValuesEndPeriod;
            
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

            int numFactors = spotSims.NumFactors;
            int numNonCrossMonomials = regressMaxPolyDegree * numFactors;
            int numCrossMonomials = regressCrossProducts ? numNonCrossMonomials * (numNonCrossMonomials - 1) / 2 : 0;
            int numMonomials = 1 /*constant independent variable*/ + numNonCrossMonomials + numCrossMonomials;

            Matrix<double> designMatrix = Matrix<double>.Build.Dense(numSims, numMonomials);
            for (int i = 0; i < numSims; i++) // TODO see if Math.Net has simpler way of setting whole column to constant
            {
                designMatrix[i, 0] = 1.0;
            }
            
            // Loop back through other periods
            T startActiveStorage = inventorySpace.Start.Offset(-1);
            T[] periodsForResultsTimeSeries = startActiveStorage.EnumerateTo(inventorySpace.End).ToArray();

            int backCounter = numPeriods - 2;

            Vector<double> numSimsMemoryBuffer = Vector<double>.Build.Dense(numSims);

            foreach (T period in periodsForResultsTimeSeries.Reverse().Skip(1))
            {

                double[] nextPeriodInventorySpaceGrid = inventorySpaceGrids[backCounter + 1];
                Vector<double>[] storageRegressValuesNextPeriod = new Vector<double>[nextPeriodInventorySpaceGrid.Length];
                Vector<double>[] storageActualValuesNextPeriod = storageValuesByPeriod[backCounter + 1];

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
                    PopulateDesignMatrix(designMatrix, period, spotSims, regressMaxPolyDegree, regressCrossProducts);
                    Matrix<double> pseudoInverse = designMatrix.PseudoInverse();

                    // TODO doing the regressions for all next inventory could be inefficient as they might not all be needed
                    for (int i = 0; i < nextPeriodInventorySpaceGrid.Length; i++) // TODO parallelise 
                    {
                        Vector<double> storageValuesBySimNextPeriod = storageActualValuesNextPeriod[i];
                        Vector<double> regressResults = pseudoInverse.Multiply(storageValuesBySimNextPeriod);
                        Vector<double> estimatedContinuationValues = designMatrix.Multiply(regressResults);
                        storageRegressValuesNextPeriod[i] = estimatedContinuationValues;
                    }
                }
                
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

                var storageValuesByInventory = new Vector<double>[inventorySpaceGrid.Length]; // TODO change type to DenseVector?

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
                                regressionContinuationValueByDecisionSet[decisionIndex] =
                                    storageRegressValuesNextPeriod[inventoryGridIndex];
                                actualContinuationValueByDecisionSet[decisionIndex] =
                                    storageActualValuesNextPeriod[inventoryGridIndex];
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
                    storageValuesByInventory[inventoryIndex] = storageValuesBySim;
                }

                inventorySpaceGrids[backCounter] = inventorySpaceGrid;
                storageValuesByPeriod[backCounter] = storageValuesByInventory;
                backCounter--;
            }



#if RUN_DELTAS
            var inventories = new double[periodsForResultsTimeSeries.Length + 1][]; // TODO rethink this + 1
            var deltas = new double[periodsForResultsTimeSeries.Length];

            var startingInventories = new double[numSims];
            for (int i = 0; i < numSims; i++)
                startingInventories[i] = startingInventory; // TODO ch
            inventories[0] = startingInventories;

            for (int periodIndex = 0; periodIndex < periodsForResultsTimeSeries.Length; periodIndex++)
            {
                T period = periodsForResultsTimeSeries[periodIndex];
                var nextPeriodInventories = new double[numSims];
                inventories[periodIndex + 1] = nextPeriodInventories;

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
                double[] thisPeriodInventories = inventories[periodIndex];
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

                        double continuationValue = regressionContinuationValueByDecisionSet[decisionIndex][simIndex]; // TODO potentially this array lookup could be quite costly

                        double totalNpv = immediateNpv + continuationValue - inventoryCostNpv; // TODO IMPORTANT check if inventoryCostNpv should be subtracted;
                        decisionNpvsRegress[decisionIndex] = totalNpv;
                        cmdtyUsedForInjectWithdrawVolumes[decisionIndex] = cmdtyUsedForInjectWithdrawVolume;
                    }
                    (double optimalRegressDecisionNpv, int indexOfOptimalDecision) = StorageHelper.MaxValueAndIndex(decisionNpvsRegress);
                    double optimalDecisionVolume = decisionSet[indexOfOptimalDecision];
                    double optimalNextStepInventory = inventory + optimalDecisionVolume - indexOfOptimalDecision;
                    nextPeriodInventories[simIndex] = optimalNextStepInventory;

                    double optimalCmdtyUsedForInjectWithdrawVolume = cmdtyUsedForInjectWithdrawVolumes[indexOfOptimalDecision];

                    sumSpotPriceTimesVolume += -(optimalDecisionVolume + optimalCmdtyUsedForInjectWithdrawVolume) * simulatedSpotPrice;
                }

                double forwardPrice = forwardCurve[period];
                double periodDelta = sumSpotPriceTimesVolume / forwardPrice / numSims;
                deltas[periodIndex] = periodDelta;
            }
#endif

            // Calculate NPVs for first active period using current inventory
            // TODO this is unnecesarily introducing floating point error if the val date is during the storage active period and there should not be a Vector of simulated spot prices
            double storageNpv = storageValuesByPeriod[0][0].Average(); // TODO use non-linq average?

            return new LsmcStorageValuationResults<T>(storageNpv, null, null, null);
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

        private static void PopulateDesignMatrix<T>(Matrix<double> designMatrix, T period, ISpotSimResults<T> spotSims, 
            int regressMaxPolyDegree, bool regressCrossProducts)
            where T : ITimePeriod<T>
        {
            // TODO think about if rearranging loop orders could minimize cache misses
            for (int factorIndex = 0; factorIndex < spotSims.NumFactors; factorIndex++)
            {
                ReadOnlySpan<double> factorSims = spotSims.MarkovFactorsForPeriod(period, factorIndex).Span;
                for (int simIndex = 0; simIndex < spotSims.NumSims; simIndex++)
                {
                    double factorSim = factorSims[simIndex];
                    for (int polyDegree = 1; polyDegree <= regressMaxPolyDegree; polyDegree++)
                    {
                        double monomial = Math.Pow(factorSim, polyDegree);
                        int colIndex = factorIndex * regressMaxPolyDegree + polyDegree;
                        designMatrix[simIndex, colIndex] = monomial;
                    }
                }
            }

            if (regressCrossProducts)
            {
                throw new NotImplementedException(); // TODO
            }
        }

    }
}
