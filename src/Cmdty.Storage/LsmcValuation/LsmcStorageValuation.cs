#region License
// Copyright (c) 2019 Jake Fowler
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
using Cmdty.Core.Simulation.MultiFactor;
using Cmdty.TimePeriodValueTypes;
using Cmdty.TimeSeries;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.LinearAlgebra.Factorization;

namespace Cmdty.Storage.LsmcValuation
{
    public static class LsmcStorageValuation
    {

        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
            TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule,
            Func<Day, Day, double> discountFactors,
            Func<ICmdtyStorage<T>, IDoubleStateSpaceGridCalc> gridCalcFactory, IInterpolatorFactory interpolatorFactory,
            double numericalTolerance, MultiFactorSpotSimResults<T> spotSims, int regressMaxPolyDegree, bool regressCrossProducts)
            where T : ITimePeriod<T>
        {
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

            IDoubleStateSpaceGridCalc gridCalc = gridCalcFactory(storage);
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
            
            
            foreach (T period in periodsForResultsTimeSeries.Reverse().Skip(1))
            {
                PopulateDesignMatrix(designMatrix, period, spotSims, regressMaxPolyDegree, regressCrossProducts);
                Svd<double> svd = designMatrix.Svd(false); // TODO does computeVectors parameter matter for solution?
                // TODO use svd.Solve method, or I can I do this in a more efficient way, e.g. compute intermediate values myself? Check Math.Net source code.

                double[] nextPeriodInventorySpaceGrid = inventorySpaceGrids[backCounter + 1];
                Vector<double>[] storageActualValuesNextPeriod = storageValuesByPeriod[backCounter + 1];

                // TODO doing the regressions for all next inventory could be inefficient as they might not all be needed
                Vector<double>[] storageEstimatedValuesNextPeriod = new Vector<double>[nextPeriodInventorySpaceGrid.Length];
                for (int i = 0; i < nextPeriodInventorySpaceGrid.Length; i++) // TODO parallelise 
                {
                    Vector<double> storageValuesBySimNextPeriod = storageActualValuesNextPeriod[i];
                    Vector<double> regressResults = svd.Solve(storageValuesBySimNextPeriod);
                    Vector<double> estimatedContinuationValues = designMatrix.Multiply(regressResults);
                    storageEstimatedValuesNextPeriod[i] = estimatedContinuationValues;
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

                var storageValuesByInventory = new Vector<double>[inventorySpaceGrid.Length];

                Day cmdtySettlementDate = settleDateRule(period);
                double discountFactorFromCmdtySettlement = DiscountToCurrentDay(cmdtySettlementDate);

                ReadOnlySpan<double> simulatedPrices = spotSims.SpotPricesForPeriod(period).Span;

                for (int i = 0; i < inventorySpaceGrid.Length; i++)
                {
                    double inventory = inventorySpaceGrid[i];
                    InjectWithdrawRange injectWithdrawRange = storage.GetInjectWithdrawRange(period, inventory);
                    double inventoryLoss = storage.CmdtyInventoryPercentLoss(period) * inventory;
                    double[] decisionSet = StorageHelper.CalculateBangBangDecisionSet(injectWithdrawRange, inventory, inventoryLoss,
                        nextStepInventorySpaceMin, nextStepInventorySpaceMax, numericalTolerance);
                    IReadOnlyList<DomesticCashFlow> inventoryCostCashFlows = storage.CmdtyInventoryCost(period, inventory);
                    double inventoryCostNpv = inventoryCostCashFlows.Sum(cashFlow => cashFlow.Amount * DiscountToCurrentDay(cashFlow.Date));


                    for (int simIndex = 0; simIndex < numSims; simIndex++)
                    {
                        double simulatedSpotPrice = simulatedPrices[simIndex];
                        for (var j = 0; j < decisionSet.Length; j++)
                        {
                            double decisionVolume = decisionSet[j];
                            // TODO split StorageImmediateNpvForDecision into parts which are dependent on inventory and the other parts of the calc and do the inventory dependent calcs outside of this loop
                            (double immediateNpv, double cmdtyConsumed) = StorageHelper.StorageImmediateNpvForDecision(storage, period, inventory,
                                decisionVolume, simulatedSpotPrice, discountFactorFromCmdtySettlement, DiscountToCurrentDay);

                            double inventoryAfterDecision = inventory + decisionVolume - inventoryLoss;
                            // TODO calc future value:
                            // 

                        }

                    }

                    // TODO regress future cash flow versus factors
                    // TODO for each simulation decide optimal decision
                }

                inventorySpaceGrids[backCounter] = inventorySpaceGrid;
                storageValuesByPeriod[backCounter] = storageValuesByInventory;
                backCounter--;
            }

            // TODO Loop forward from start inventory choosing optimal decisions (like with intrinsic valuation)

            throw new NotImplementedException();
        }

        private static void PopulateDesignMatrix<T>(Matrix<double> designMatrix, T period, MultiFactorSpotSimResults<T> spotSims, 
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
                        int colIndex = 1 + factorIndex * regressMaxPolyDegree + polyDegree;
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
