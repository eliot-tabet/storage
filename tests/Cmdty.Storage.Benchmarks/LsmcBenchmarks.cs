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
using BenchmarkDotNet.Attributes;
using Cmdty.Core.Simulation.MultiFactor;
using Cmdty.Storage.LsmcValuation;
using Cmdty.TimePeriodValueTypes;
using Cmdty.TimeSeries;
using MathNet.Numerics;
using TimeSeriesFactory = Cmdty.TimeSeries.TimeSeries;

namespace Cmdty.Storage.Benchmarks
{
    public class LsmcBenchmarks
    {
        private const int NumSims = 1_000;
        private const double Inventory = 5_685;
        private const int RandomSeed = 11;
        private const int RegressMaxDegree = 2;
        private const bool RegressCrossProducts = false;
        private const double NumTolerance = 1E-10;
        // TODO make these static?
        private readonly IDoubleStateSpaceGridCalc _gridCalc;
        private readonly Day _valDate;
        private readonly Func<Day, Day, double> _flatInterestRateDiscounter;
        private readonly CmdtyStorage<Day> _simpleDailyStorage;
        private readonly MultiFactorParameters<Day> _1FDailyMultiFactorParams;
        private readonly Func<Day, Day> _settleDateRule;
        private readonly TimeSeries<Day, double> _forwardCurve;
        public LsmcBenchmarks()
        {
            _valDate = new Day(2019, 8, 29);

            #region Set Up Simple Storage
            var storageStart = new Day(2019, 12, 1);
            var storageEnd = new Day(2020, 4, 1);
            const double maxWithdrawalRate = 850.0;
            const double maxInjectionRate = 625.0;
            const double maxInventory = 52_500.0;
            const double constantInjectionCost = 1.25;
            const double constantWithdrawalCost = 0.93;

            _simpleDailyStorage = CmdtyStorage<Day>.Builder
                .WithActiveTimePeriod(storageStart, storageEnd)
                .WithConstantInjectWithdrawRange(-maxWithdrawalRate, maxInjectionRate)
                .WithZeroMinInventory()
                .WithConstantMaxInventory(maxInventory)
                .WithPerUnitInjectionCost(constantInjectionCost, injectionDate => injectionDate)
                .WithNoCmdtyConsumedOnInject()
                .WithPerUnitWithdrawalCost(constantWithdrawalCost, withdrawalDate => withdrawalDate)
                .WithNoCmdtyConsumedOnWithdraw()
                .WithNoCmdtyInventoryLoss()
                .WithNoInventoryCost()
                .MustBeEmptyAtEnd()
                .Build();
            #endregion Set Up Simple Storage

            const double oneFactorMeanReversion = 12.5;
            const double oneFactorSpotVol = 0.95;
            _1FDailyMultiFactorParams = MultiFactorParameters.For1Factor(oneFactorMeanReversion, 
                    TimeSeriesFactory.ForConstantData(_valDate, storageEnd, oneFactorSpotVol));

            const double flatInterestRate = 0.055;
            _flatInterestRateDiscounter = StorageHelper.CreateAct65ContCompDiscounter(flatInterestRate);

            const int numInventorySpacePoints = 100;
            _gridCalc = FixedSpacingStateSpaceGridCalc.CreateForFixedNumberOfPointsOnGlobalInventoryRange(_simpleDailyStorage, numInventorySpacePoints);

            _settleDateRule = deliveryDate => Month.FromDateTime(deliveryDate.Start).Offset(1).First<Day>() + 19; // Settlement on 20th of following month

            const double baseForwardPrice = 53.5;
            const double forwardSeasonalFactor = 24.6;
            _forwardCurve = TimeSeriesFactory.FromMap(_valDate, storageEnd, day =>
            {
                int daysForward = day.OffsetFrom(_valDate);
                return baseForwardPrice + Math.Sin(2.0 * Math.PI / 365.0 * daysForward) * forwardSeasonalFactor;
            });
        }

        //[Benchmark]
        public double ValueSimpleDailyStorageOneFactor_ManagedNumerics()
        {
            Control.UseManaged();
            LsmcStorageValuationResults<Day> results = LsmcStorageValuation.Calculate(_valDate, Inventory,
                _forwardCurve, _simpleDailyStorage, _settleDateRule, _flatInterestRateDiscounter, _gridCalc, NumTolerance,
                _1FDailyMultiFactorParams, NumSims, RandomSeed, RegressMaxDegree, RegressCrossProducts);
            return results.Npv;
        }

        [Benchmark]
        public double ValueSimpleDailyStorageOneFactor_MklNumerics()
        {
            Control.UseNativeMKL();
            LsmcStorageValuationResults<Day> results = LsmcStorageValuation.Calculate(_valDate, Inventory,
                _forwardCurve, _simpleDailyStorage, _settleDateRule, _flatInterestRateDiscounter, _gridCalc, NumTolerance,
                _1FDailyMultiFactorParams, NumSims, RandomSeed, RegressMaxDegree, RegressCrossProducts);
            return results.Npv;
        }

        // TODO:
        // With and without MKL
        // Different number of simulations
        // Different grid spacing
        // Different number of factors

    }
}
