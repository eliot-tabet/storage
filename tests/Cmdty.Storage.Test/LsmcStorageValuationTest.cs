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

        // TODO:
        // One factor value the same as trinomial
        // Two factor canonical the same as one-factor
        // Zero mean reversion the same as intrinsic. Try with one and two factors.
        // Zero/low vol the same as intrinsic

        // TODO refactor this to share code with trinomial test
        [Fact(Skip = "Figure out why this isn't passing. Numbers are close, but not exact, where the should be.")]
        public void Calculate_StorageWithForcedInjectAndWithdraw_NpvEqualsTrivialIntrinsicCalc()
        {
            var currentDate = new Day(2019, 8, 29);
            const int numInventorySpacePoints = 500;
            const int numSims = 1_000;
            const int seed = 11;
            const int regressMaxDegree = 2;
            const bool regressCrossProducts = false;
            const double numTolerance = 1E-10;

            var storageStart = new Day(2019, 12, 1);
            var storageEnd = new Day(2020, 4, 1);

            const double storageStartingInventory = 0.0;
            const double minInventory = 0.0;
            const double maxInventory = 10_000.0;

            const double forcedInjectionRate = 211.5;
            const int forcedInjectionNumDays = 20;
            var forcedInjectionStart = new Day(2019, 12, 20);

            const double injectionPerUnitCost = 1.23;
            const double injectionCmdtyConsumed = 0.01;

            const double forcedWithdrawalRate = 187.54;
            const int forcedWithdrawalNumDays = 15;
            var forcedWithdrawalStart = new Day(2020, 2, 5);

            const double withdrawalPerUnitCost = 0.98;
            const double withdrawalCmdtyConsumed = 0.015;

            (DoubleTimeSeries<Day> forwardCurve, DoubleTimeSeries<Day> spotVolCurve) = TestHelper.CreateDailyTestForwardAndSpotVolCurves(currentDate, storageEnd);
            const double meanReversion = 16.5;
            const double interestRate = 0.09;

            TimeSeries<Month, Day> settlementDates = new TimeSeries<Month, Day>.Builder()
            {
                { new Month(2019, 12),  new Day(2020, 1, 20)},
                { new Month(2020, 1),  new Day(2020, 2, 18)},
                { new Month(2020, 2),  new Day(2020, 3, 21)},
                { new Month(2020, 3),  new Day(2020, 4, 22)}
            }.Build();

            var injectWithdrawConstraints = new List<InjectWithdrawRangeByInventoryAndPeriod<Day>>
            {
                (period: storageStart, injectWithdrawRanges: new List<InjectWithdrawRangeByInventory>
                {
                    (inventory: minInventory, (minInjectWithdrawRate: 0.0, maxInjectWithdrawRate: 0.0)),
                    (inventory: maxInventory, (minInjectWithdrawRate: 0.0, maxInjectWithdrawRate: 0.0))
                }),
                (period: forcedInjectionStart, injectWithdrawRanges: new List<InjectWithdrawRangeByInventory>
                {
                    (inventory: minInventory, (minInjectWithdrawRate: forcedInjectionRate, maxInjectWithdrawRate: forcedInjectionRate)),
                    (inventory: maxInventory, (minInjectWithdrawRate: forcedInjectionRate, maxInjectWithdrawRate: forcedInjectionRate))
                }),
                (period: forcedInjectionStart.Offset(forcedInjectionNumDays), injectWithdrawRanges: new List<InjectWithdrawRangeByInventory>
                {
                    (inventory: minInventory, (minInjectWithdrawRate: 0.0, maxInjectWithdrawRate: 0.0)),
                    (inventory: maxInventory, (minInjectWithdrawRate: 0.0, maxInjectWithdrawRate: 0.0))
                }),
                (period: forcedWithdrawalStart, injectWithdrawRanges: new List<InjectWithdrawRangeByInventory>
                {
                    (inventory: minInventory, (minInjectWithdrawRate: -forcedWithdrawalRate, maxInjectWithdrawRate: -forcedWithdrawalRate)),
                    (inventory: maxInventory, (minInjectWithdrawRate: -forcedWithdrawalRate, maxInjectWithdrawRate: -forcedWithdrawalRate))
                }),
                (period: forcedWithdrawalStart.Offset(forcedWithdrawalNumDays), injectWithdrawRanges: new List<InjectWithdrawRangeByInventory>
                {
                    (inventory: minInventory, (minInjectWithdrawRate: 0.0, maxInjectWithdrawRate: 0.0)),
                    (inventory: maxInventory, (minInjectWithdrawRate: 0.0, maxInjectWithdrawRate: 0.0))
                }),
            };

            Day InjectionCostPaymentTerms(Day injectionDate)
            {
                return injectionDate.Offset(10);
            }

            Day WithdrawalCostPaymentTerms(Day withdrawalDate)
            {
                return withdrawalDate.Offset(4);
            }

            CmdtyStorage<Day> storage = CmdtyStorage<Day>.Builder
                .WithActiveTimePeriod(storageStart, storageEnd)
                .WithTimeAndInventoryVaryingInjectWithdrawRatesPolynomial(injectWithdrawConstraints)
                .WithPerUnitInjectionCost(injectionPerUnitCost, InjectionCostPaymentTerms)
                .WithFixedPercentCmdtyConsumedOnInject(injectionCmdtyConsumed)
                .WithPerUnitWithdrawalCost(withdrawalPerUnitCost, WithdrawalCostPaymentTerms)
                .WithFixedPercentCmdtyConsumedOnWithdraw(withdrawalCmdtyConsumed)
                .WithNoCmdtyInventoryLoss()
                .WithNoInventoryCost()
                .WithTerminalInventoryNpv((cmdtySpotPrice, inventory) => 0.0)
                .Build();

            var flatInterestRateDiscounter = StorageHelper.CreateAct65ContCompDiscounter(interestRate);
            var multiFactorParams1Factor = MultiFactorParameters.For1Factor(meanReversion, spotVolCurve);
            var gridCalc = FixedSpacingStateSpaceGridCalc.CreateForFixedNumberOfPointsOnGlobalInventoryRange(storage, numInventorySpacePoints);
            Day SettleDateRule(Day deliveryDate) => settlementDates[Month.FromDateTime(deliveryDate.Start)];

            LsmcStorageValuationResults<Day> valuationResults = LsmcStorageValuation.Calculate(currentDate, storageStartingInventory,
                forwardCurve, storage, SettleDateRule, flatInterestRateDiscounter, gridCalc, numTolerance,
                multiFactorParams1Factor, numSims, seed, regressMaxDegree, regressCrossProducts);

            // Calculate the NPV Manually

            // Period of forced injection
            double injectionPv = 0.0;
            for (int i = 0; i < forcedInjectionNumDays; i++)
            {
                Day injectionDate = forcedInjectionStart.Offset(i);
                double forwardPrice = forwardCurve[injectionDate];

                Day cmdtySettlementDate = settlementDates[Month.FromDateTime(injectionDate.Start)];
                double cmdtyDiscountFactor =
                    Act365ContCompoundDiscountFactor(currentDate, cmdtySettlementDate, interestRate);

                Day injectionCostSettlementDate = InjectionCostPaymentTerms(injectionDate);
                double injectCostDiscountFactor =
                    Act365ContCompoundDiscountFactor(currentDate, injectionCostSettlementDate, interestRate);

                double cmdtyBoughtPv = -forwardPrice * forcedInjectionRate * (1 + injectionCmdtyConsumed) * cmdtyDiscountFactor;
                double injectCostPv = -injectionPerUnitCost * forcedInjectionRate * injectCostDiscountFactor;

                injectionPv += cmdtyBoughtPv + injectCostPv;
            }

            // Period of forced withdrawal
            double withdrawalPv = 0.0;
            for (int i = 0; i < forcedWithdrawalNumDays; i++)
            {
                Day withdrawalDate = forcedWithdrawalStart.Offset(i);
                double forwardPrice = forwardCurve[withdrawalDate];

                Day cmdtySettlementDate = settlementDates[Month.FromDateTime(withdrawalDate.Start)];
                double cmdtyDiscountFactor =
                    Act365ContCompoundDiscountFactor(currentDate, cmdtySettlementDate, interestRate);

                Day withdrawalCostSettlementDate = WithdrawalCostPaymentTerms(withdrawalDate);
                double withdrawalCostDiscountFactor =
                    Act365ContCompoundDiscountFactor(currentDate, withdrawalCostSettlementDate, interestRate);

                double cmdtySoldPv = forwardPrice * forcedWithdrawalRate * (1 - withdrawalCmdtyConsumed) * cmdtyDiscountFactor;
                double withdrawalCostPv = -withdrawalPerUnitCost * forcedWithdrawalRate * withdrawalCostDiscountFactor;

                withdrawalPv += cmdtySoldPv + withdrawalCostPv;
            }

            double expectedNpv = injectionPv + withdrawalPv;

            Assert.Equal(expectedNpv, valuationResults.Npv, 10);
        }

        private static double Act365ContCompoundDiscountFactor(Day currentDate, Day paymentDate, double interestRate)
        {
            return Math.Exp(-paymentDate.OffsetFrom(currentDate) / 365.0 * interestRate);
        }

    }
}
