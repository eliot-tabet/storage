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

using System.Collections.Generic;
using System.Linq;

namespace Cmdty.Storage
{
    /// <summary>
    /// Used for combining basis functions.
    /// </summary>
    public class BasisFunctionsBuilder
    {
        public IEnumerable<BasisFunction> Functions { get; }

        public BasisFunctionsBuilder(BasisFunction basisFunction) =>
            Functions = new[] { basisFunction };

        public BasisFunctionsBuilder(IEnumerable<BasisFunction> basisFunctions) =>
            Functions = basisFunctions;

        public static implicit operator BasisFunction[](BasisFunctionsBuilder builder) => builder.Functions.ToArray();

        public static implicit operator List<BasisFunction>(BasisFunctionsBuilder builder) => builder.Functions.ToList();

        public static BasisFunctionsBuilder operator +(BasisFunctionsBuilder builder1, BasisFunctionsBuilder builder2) 
            => Combine(builder1, builder2);

        public static BasisFunctionsBuilder Combine(BasisFunctionsBuilder builder1, BasisFunctionsBuilder builder2)
            => new BasisFunctionsBuilder(builder1.Functions.Concat(builder2.Functions));

        public static BasisFunctionsBuilder Ones => new BasisFunctionsBuilder(BasisFunctions.Ones);

        public static BasisFunctionsBuilder SpotPricePower(int power) => new BasisFunctionsBuilder(BasisFunctions.SpotPricePower(power));

        public static BasisFunctionsBuilder MarkovFactorPower(int markovFactor, int power) 
            => new BasisFunctionsBuilder(BasisFunctions.MarkovFactorPower(markovFactor, power));

        public static BasisFunctionsBuilder MarkovFactorAllPositiveIntegerPowersUpTo(int markovFactor, int maxPower) 
            => new BasisFunctionsBuilder(BasisFunctions.MarkovFactorAllPositiveIntegerPowersUpTo(markovFactor, maxPower));
        
        public static BasisFunctionsBuilder AllMarkovFactorAllPositiveIntegerPowersUpTo(int maxPower, int numMarkovFactors)
            => new BasisFunctionsBuilder(BasisFunctions.AllMarkovFactorAllPositiveIntegerPowersUpTo(maxPower, numMarkovFactors));

    }
}
