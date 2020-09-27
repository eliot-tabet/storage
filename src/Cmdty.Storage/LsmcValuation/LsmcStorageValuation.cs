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
using Cmdty.TimePeriodValueTypes;
using Cmdty.TimeSeries;

namespace Cmdty.Storage.LsmcValuation
{
    public static class LsmcStorageValuation
    {

        public static LsmcStorageValuationResults<T> Calculate<T>(T currentPeriod, double startingInventory,
                TimeSeries<T, double> forwardCurve, ICmdtyStorage<T> storage, Func<T, Day> settleDateRule, Func<Day, Day, double> discountFactors,
                Func<ICmdtyStorage<T>, IDoubleStateSpaceGridCalc> gridCalcFactory, IInterpolatorFactory interpolatorFactory,
                double numericalTolerance)
            where T : ITimePeriod<T>
        {
            if (startingInventory < 0)
                throw new ArgumentException("Inventory cannot be negative.", nameof(startingInventory));

            TimeSeries<T, InventoryRange> inventorySpace = StorageHelper.CalculateInventorySpace(storage, startingInventory, currentPeriod);

            if (forwardCurve.Start.CompareTo(currentPeriod) > 0)
                throw new ArgumentException("Forward curve starts too late. Must start on or before the current period.", nameof(forwardCurve));

            if (forwardCurve.End.CompareTo(inventorySpace.End) < 0)
                throw new ArgumentException("Forward curve does not extend until storage end period.", nameof(forwardCurve));

            // Perform backward induction
            int numPeriods = inventorySpace.Count + 1; // +1 as inventorySpaceGrid doesn't contain first period

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

            // Loop back through other periods
            T startActiveStorage = inventorySpace.Start.Offset(-1);
            T[] periodsForResultsTimeSeries = startActiveStorage.EnumerateTo(inventorySpace.End).ToArray();

            int backCounter = numPeriods - 2;
            IDoubleStateSpaceGridCalc gridCalc = gridCalcFactory(storage);

            foreach (T periodLoop in periodsForResultsTimeSeries.Reverse().Skip(1))
            {
                double[] inventorySpaceGrid;
                if (periodLoop.Equals(startActiveStorage))
                {
                    inventorySpaceGrid = new[] { startingInventory };
                }
                else
                {
                    (double inventorySpaceMin, double inventorySpaceMax) = inventorySpace[periodLoop];
                    inventorySpaceGrid = gridCalc.GetGridPoints(inventorySpaceMin, inventorySpaceMax)
                                                .ToArray();
                }
                (double nextStepInventorySpaceMin, double nextStepInventorySpaceMax) = inventorySpace[periodLoop.Offset(1)];

                Day cmdtySettlementDate = settleDateRule(periodLoop);
                double discountFactorFromCmdtySettlement = DiscountToCurrentDay(cmdtySettlementDate);

                for (int i = 0; i < inventorySpaceGrid.Length; i++)
                {
                    // TODO regress future cash flow versus factors
                    // TODO for each simulation decide optimal decision
                }

                backCounter--;
            }

            throw new NotImplementedException();
        }

    }
}
