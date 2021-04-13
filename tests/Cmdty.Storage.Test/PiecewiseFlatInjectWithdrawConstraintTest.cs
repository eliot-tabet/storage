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
using Xunit;

namespace Cmdty.Storage.Test
{
    public sealed class PiecewiseFlatInjectWithdrawConstraintTest
    {
        private readonly PiecewiseFlatInjectWithdrawConstraint _injectWithdrawConstraint;
        private readonly List<InjectWithdrawRangeByInventory> _injectWithdrawRanges;

        public PiecewiseFlatInjectWithdrawConstraintTest()
        {
            _injectWithdrawRanges = new List<InjectWithdrawRangeByInventory>
            {
                (inventory: 0.0, (minInjectWithdrawRate: -44.85, maxInjectWithdrawRate: 56.8)),
                (inventory: 100.0, (minInjectWithdrawRate: -45.01, maxInjectWithdrawRate: 54.5)),
                (inventory: 300.0, (minInjectWithdrawRate: -45.78, maxInjectWithdrawRate: 52.01)),
                (inventory: 600.0, (minInjectWithdrawRate: -46.17, maxInjectWithdrawRate: 51.9)),
                (inventory: 800.0, (minInjectWithdrawRate: -46.99, maxInjectWithdrawRate: 50.8)),
                (inventory: 1000.0, (minInjectWithdrawRate: -47.12, maxInjectWithdrawRate: 50.01))
            };
            _injectWithdrawConstraint = new PiecewiseFlatInjectWithdrawConstraint(_injectWithdrawRanges);
        }

        [Theory]
        [InlineData(99.0, -44.85, 56.8)]
        [InlineData(610.85, -46.17, 51.9)]
        [InlineData(999.99, -46.99, 50.8)]
        public void GetInjectWithdrawRange_AsExpected(double inventory, double expectedMinInjectWithdraw, double expectedMaxInjectWithdraw)
        {
            (double minInjectWithdraw, double maxInjectWithdraw) = _injectWithdrawConstraint.GetInjectWithdrawRange(inventory);
            Assert.Equal(expectedMinInjectWithdraw, minInjectWithdraw);
            Assert.Equal(expectedMaxInjectWithdraw, maxInjectWithdraw);
        }
        
        [Fact]
        public void GetInjectWithdrawRange_InventoryEqualsPiecewisePillars_EqualToInputsAtInventoryPillars()
        {
            foreach ((double inventoryPillar, InjectWithdrawRange inputInjectWithdrawRange) in _injectWithdrawRanges)
            {
                InjectWithdrawRange outputInjectWithdrawRange = _injectWithdrawConstraint.GetInjectWithdrawRange(inventoryPillar);
                Assert.Equal(inputInjectWithdrawRange.MinInjectWithdrawRate, outputInjectWithdrawRange.MinInjectWithdrawRate);
                Assert.Equal(inputInjectWithdrawRange.MaxInjectWithdrawRate, outputInjectWithdrawRange.MaxInjectWithdrawRate);
            }
        }

        [Fact]
        public void GetInjectWithdrawRange_InventoryBelowMinInventory_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => _injectWithdrawConstraint.GetInjectWithdrawRange(-0.01));
        }
        
        [Fact]
        public void GetInjectWithdrawRange_InventoryAboveMaxInventory_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => _injectWithdrawConstraint.GetInjectWithdrawRange(1000.001));
        }

    }
}
