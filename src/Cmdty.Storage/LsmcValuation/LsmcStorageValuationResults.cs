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

using Cmdty.Core.Common;
using Cmdty.TimePeriodValueTypes;
using Cmdty.TimeSeries;

namespace Cmdty.Storage
{
    public sealed class LsmcStorageValuationResults<T>
        where T : ITimePeriod<T>
    {
        public double Npv { get; }
        public DoubleTimeSeries<T> Deltas {get;}
        public TimeSeries<T, StorageProfile> ExpectedStorageProfile { get; }
        public Panel<T, double> SpotPriceBySim { get; }
        public Panel<T, double> InventoryBySim { get; }
        public Panel<T, double> InjectWithdrawVolumeBySim { get; }
        public Panel<T, double> CmdtyConsumedBySim { get; }
        public Panel<T, double> InventoryLossBySim { get; }
        public Panel<T, double> NetVolumeBySim { get; }
        public TimeSeries<T, TriggerPricePair> TriggerPrices { get; }

        public LsmcStorageValuationResults(double npv, DoubleTimeSeries<T> deltas, TimeSeries<T, StorageProfile> expectedStorageProfile, 
            Panel<T, double> spotPriceBySim, 
            Panel<T, double> inventoryBySim, Panel<T, double> injectWithdrawVolumeBySim, Panel<T, double> cmdtyConsumedBySim, 
            Panel<T, double> inventoryLossBySim, Panel<T, double> netVolumeBySim, TimeSeries<T, TriggerPricePair> triggerPrices)
        {
            Npv = npv;
            Deltas = deltas;
            ExpectedStorageProfile = expectedStorageProfile;
            SpotPriceBySim = spotPriceBySim;
            InventoryBySim = inventoryBySim;
            InjectWithdrawVolumeBySim = injectWithdrawVolumeBySim;
            CmdtyConsumedBySim = cmdtyConsumedBySim;
            InventoryLossBySim = inventoryLossBySim;
            NetVolumeBySim = netVolumeBySim;
            TriggerPrices = triggerPrices;
        }

        public static LsmcStorageValuationResults<T> CreateExpiredResults()
        {
            return new LsmcStorageValuationResults<T>(0.0, DoubleTimeSeries<T>.Empty, TimeSeries<T, StorageProfile>.Empty,
                Panel<T, double>.CreateEmpty(), Panel<T, double>.CreateEmpty(),
                Panel<T, double>.CreateEmpty(), 
                Panel<T, double>.CreateEmpty(), Panel<T, double>.CreateEmpty(),
                Panel<T, double>.CreateEmpty(), TimeSeries<T, TriggerPricePair>.Empty);
        }

        public static LsmcStorageValuationResults<T> CreateEndPeriodResults(double npv)
        {
            return new LsmcStorageValuationResults<T>(npv, DoubleTimeSeries<T>.Empty, TimeSeries<T, StorageProfile>.Empty,
                Panel<T, double>.CreateEmpty(), Panel<T, double>.CreateEmpty(), 
                Panel<T, double>.CreateEmpty(),
                Panel<T, double>.CreateEmpty(), Panel<T, double>.CreateEmpty(),
                Panel<T, double>.CreateEmpty(), TimeSeries<T, TriggerPricePair>.Empty);
        }

    }
}
