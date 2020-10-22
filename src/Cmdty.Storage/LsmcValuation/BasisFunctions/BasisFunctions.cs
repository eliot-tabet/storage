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

namespace Cmdty.Storage
{
    public static class BasisFunctions
    {
        public static BasisFunction Ones
        {
            get
            {
                static void Ones(ReadOnlyMemory<double>[] markovFactors, ReadOnlySpan<double> spotPriceBySim,
                    Span<double> designMatrixCol)
                {
                    for (int i = 0; i < designMatrixCol.Length; i++)
                        designMatrixCol[i] = 1.0;
                }
                return Ones;
            }
        }

        public static BasisFunction SpotPricePower(int power)
        {
            void BasisFunc(ReadOnlyMemory<double>[] markovFactors, ReadOnlySpan<double> spotPriceBySim,
                Span<double> designMatrixCol)
            {
                for (int i = 0; i < designMatrixCol.Length; i++)
                    designMatrixCol[i] = Math.Pow(spotPriceBySim[i], power);
            }
            return BasisFunc;
        }

        public static BasisFunction MarkovFactorPower(int markovFactor, int power)
        {
            void BasisFunc(ReadOnlyMemory<double>[] markovFactors, ReadOnlySpan<double> spotPriceBySim,
                Span<double> designMatrixCol)
            {
                ReadOnlySpan<double> markovFactorBySim = markovFactors[markovFactor].Span;
                for (int i = 0; i < designMatrixCol.Length; i++)
                    designMatrixCol[i] = Math.Pow(markovFactorBySim[i], power);
            }
            return BasisFunc;
        }

        public static IEnumerable<BasisFunction> MarkovFactorAllPositiveIntegerPowersUpTo(int markovFactor, int maxPower)
        {
            MaxPowerPrecondition(maxPower);
            for (int i = 0; i < maxPower; i++)
                yield return MarkovFactorPower(markovFactor, i + 1);
        }

        private static void MaxPowerPrecondition(int maxPower)
        {
            if (maxPower < 1)
                throw new ArgumentException("Maximum power must be greater than zero.");
        }

        public static IEnumerable<BasisFunction> AllMarkovFactorAllPositiveIntegerPowersUpTo(int maxPower, int numMarkovFactors)
        {
            MaxPowerPrecondition(maxPower);
            for (int i = 0; i < numMarkovFactors; i++)
                foreach (BasisFunction basisFunction in MarkovFactorAllPositiveIntegerPowersUpTo(i, maxPower))
                    yield return basisFunction;
        }

        public static BasisFunction Generic(int spotPower, Dictionary<int, int> markovFactorPowers)
        {
            if (spotPower < 0)
                throw new ArgumentException("Spot power must be non-negative.");
            if (markovFactorPowers.Any(pair => pair.Value < 0))
                throw new ArgumentException("Markov factor powers must be non-negative.");

            BasisFunction basisFunction;

            if (markovFactorPowers.Count == 0) // Just a spot power
            {
                basisFunction = SpotPricePower(spotPower);
            }
            else
            {
                basisFunction = (markovFactors, spotPriceBySim, designMatrixCol) =>
                {
                    if (spotPower == 0)
                    {
                        for (int simIndex = 0; simIndex < designMatrixCol.Length; simIndex++)
                            designMatrixCol[simIndex] = 1.0;
                    }
                    else
                    {
                        for (int simIndex = 0; simIndex < designMatrixCol.Length; simIndex++)
                            designMatrixCol[simIndex] = Math.Pow(spotPriceBySim[simIndex], spotPower); // TODO replace with power function optimised for integer?
                    }

                    foreach (int markovIndex in markovFactorPowers.Keys)
                    {
                        int markovPower = markovFactorPowers[markovIndex];
                        if (markovPower > 0)
                        {
                            ReadOnlySpan<double> markovFactorSims = markovFactors[markovIndex].Span;
                            for (int simIndex = 0; simIndex < designMatrixCol.Length; simIndex++)
                            {
                                designMatrixCol[simIndex] *= Math.Pow(markovFactorSims[simIndex], markovPower); // TODO replace with power function optimised for integer?
                            }
                        }
                    }
                };
            }
            return basisFunction;
        }

        public class Builder
        {
            public int SpotPower { get; }
            public Dictionary<int, int> MarkovFactorPowers { get; }

            public Builder(int spotPower, Dictionary<int, int> markovFactorPowers)
            {
                SpotPower = spotPower;
                MarkovFactorPowers = markovFactorPowers;
            }

            public Builder(int spotPower) : this(spotPower, new Dictionary<int, int>()) { }

            public static Builder Spot => new Builder(1);

            public static Builder Factor(int factorIndex) 
                => new Builder(0, new Dictionary<int, int>(){{factorIndex, 1}});

            public static Builder X0 => Factor(0);
            public static Builder X1 => Factor(1);
            public static Builder X2 => Factor(2);
            public static Builder X3 => Factor(3);
            public static Builder X4 => Factor(4);
            public static Builder X5 => Factor(5);
            public static Builder X6 => Factor(6);
            public static Builder X7 => Factor(7);
            public static Builder X8 => Factor(8);
            public static Builder X9 => Factor(9);

            public static Builder operator *(Builder builder1, Builder builder2)
            {
                int spotPower = builder1.SpotPower + builder2.SpotPower;
                Dictionary<int, int> markovPowers =
                    builder1.MarkovFactorPowers.ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (int markovIndex in builder2.MarkovFactorPowers.Keys)
                {
                    if (markovPowers.ContainsKey(markovIndex))
                        markovPowers[markovIndex] = markovPowers[markovIndex] + builder2.MarkovFactorPowers[markovIndex];
                    else
                        markovPowers[markovIndex] = builder2.MarkovFactorPowers[markovIndex];
                }
                return new Builder(spotPower, markovPowers);
            }

            public Builder Pow(int power)
            {
                int spotPower = SpotPower * power;
                Dictionary<int, int> markovPowers =
                    MarkovFactorPowers.ToDictionary(pair => pair.Key, pair => pair.Value * power);
                return new Builder(spotPower, markovPowers);
            }

            public static implicit operator BasisFunction(Builder builder) =>
                BasisFunctions.Generic(builder.SpotPower, builder.MarkovFactorPowers);

            public static implicit operator BasisFunctionsBuilder(Builder builder) => new BasisFunctionsBuilder(builder); // TODO could replace this with implicit case on BasisFunctionsBuilder

            public static BasisFunctionsBuilder operator +(Builder builder1, Builder builder2)
            {
                return new BasisFunctionsBuilder(new BasisFunction[]{ builder1, builder2});
            }
        }

    }
}