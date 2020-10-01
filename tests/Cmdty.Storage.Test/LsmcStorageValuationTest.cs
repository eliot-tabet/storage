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
using System.Globalization;
using Cmdty.Core.Simulation.MultiFactor;
using Cmdty.Storage.LsmcValuation;
using Cmdty.TimePeriodValueTypes;
using Cmdty.TimeSeries;
using MathNet.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Cmdty.Storage.Test
{
    public sealed class LsmcStorageValuationTest
    {
        private readonly ITestOutputHelper _testOutputHelper;

        public LsmcStorageValuationTest(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
        }

        [Fact]
        public void Calculate_StorageLikeCallOptionsOneFactor_NpvEqualsBlack76()
        {
            var valDate = new Day(2019, 8, 29);
            const int numInventorySpacePoints = 100;
            const int numSims = 2_000;
            const int seed = 11;

            (DoubleTimeSeries<Day> forwardCurve, DoubleTimeSeries<Day> spotVolCurve) =
                TestHelper.CreateDailyTestForwardAndSpotVolCurves(valDate, new Day(2020, 4, 1));
            const double meanReversion = 16.5;
            const double interestRate = 0.09;
            const double numTolerance = 1E-10;
            const int regressMaxDegree = 2;
            const bool regressCrossProducts = false;

            TestHelper.CallOptionLikeTestData testData = TestHelper.CreateThreeCallsLikeStorageTestData(forwardCurve);

            var multiFactorParams = MultiFactorParameters.For1Factor(meanReversion, spotVolCurve);
            
            Day SettleDateRule(Day settleDate) => testData.SettleDates[Month.FromDateTime(settleDate.Start)];
            Func<Day, Day, double> discounter = StorageHelper.CreateAct65ContCompDiscounter(interestRate);
            IDoubleStateSpaceGridCalc gridSpace = 
                FixedSpacingStateSpaceGridCalc.CreateForFixedNumberOfPointsOnGlobalInventoryRange(testData.Storage, numInventorySpacePoints);

            Control.UseNativeMKL();
            LsmcStorageValuationResults<Day> results = LsmcStorageValuation.Calculate(valDate, testData.Inventory,
                forwardCurve,
                testData.Storage, SettleDateRule, discounter, gridSpace, numTolerance,
                multiFactorParams, numSims, seed, regressMaxDegree, regressCrossProducts);

            // Calculate value of equivalent call options
            double expectStorageValue = 0.0;
            foreach (TestHelper.CallOption option in testData.CallOptions)
            {
                double impliedVol = TestHelper.OneFactorImpliedVol(valDate, option.ExpiryDate, spotVolCurve, meanReversion);
                double forwardPrice = forwardCurve[option.ExpiryDate];
                double black76Value = TestHelper.Black76CallOptionValue(valDate, forwardPrice,
                                          impliedVol, interestRate, option.StrikePrice, option.ExpiryDate,
                                          option.SettleDate) * option.NotionalVolume;
                expectStorageValue += black76Value;
            }

            _testOutputHelper.WriteLine(results.Npv.ToString(CultureInfo.InvariantCulture));

            double percentError = (results.Npv - expectStorageValue) / expectStorageValue;
            const double percentErrorLowerBound = -0.02; // Calculated value cannot be more than 2% lower than call options
            const double percentErrorUpperBound = 0.0; // Calculate value will not be higher than call options as LSMC is a lower bound approximation

            Assert.InRange(percentError, percentErrorLowerBound, percentErrorUpperBound);
        }

    }
}
