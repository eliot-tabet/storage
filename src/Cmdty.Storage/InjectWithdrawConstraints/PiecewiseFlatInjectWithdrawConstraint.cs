#region License
// Copyright (c) 2021 Jake Fowler
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
using JetBrains.Annotations;

namespace Cmdty.Storage
{
    public sealed class PiecewiseFlatInjectWithdrawConstraint : IInjectWithdrawConstraint
    {
        private readonly InjectWithdrawRangeByInventory[] _injectWithdrawRanges;
        private readonly double[] _inventories;

        public PiecewiseFlatInjectWithdrawConstraint([NotNull] IEnumerable<InjectWithdrawRangeByInventory> injectWithdrawRanges)
        {
            if (injectWithdrawRanges == null) throw new ArgumentNullException(nameof(injectWithdrawRanges));

            _injectWithdrawRanges = injectWithdrawRanges.OrderBy(injectWithdrawRange => injectWithdrawRange.Inventory)
                                                        .ToArray();
            if (_injectWithdrawRanges.Length < 2)
                throw new ArgumentException("Inject/withdraw ranges collection must contain at least two elements.", nameof(injectWithdrawRanges));

            _inventories = _injectWithdrawRanges.Select(injectWithdrawRange => injectWithdrawRange.Inventory)
                .ToArray();
        }

        public InjectWithdrawRange GetInjectWithdrawRange(double inventory)
        {
            if (inventory < _inventories[0] || inventory > _inventories[_inventories.Length - 1])
                throw new ArgumentException($"Value of inventory is outside of the interval [{_inventories[0]}, {_inventories[_inventories.Length - 1]}].", nameof(inventory));
            int searchIndex = Array.BinarySearch(_inventories, inventory);
            int index = searchIndex >= 0 ? searchIndex : ~searchIndex - 1; 
            return _injectWithdrawRanges[index].InjectWithdrawRange;
        }

        public double InventorySpaceUpperBound(double nextPeriodInventorySpaceLowerBound, double nextPeriodInventorySpaceUpperBound,
            double currentPeriodMinInventory, double currentPeriodMaxInventory, double inventoryPercentLoss)
        {
            throw new NotImplementedException();
        }

        public double InventorySpaceLowerBound(double nextPeriodInventorySpaceLowerBound, double nextPeriodInventorySpaceUpperBound,
            double currentPeriodMinInventory, double currentPeriodMaxInventory, double inventoryPercentLoss)
        {
            throw new NotImplementedException();
        }
    }
}
