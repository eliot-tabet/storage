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
using Xunit;
using static Cmdty.Storage.BasisFunctions.Builder;
//using Microsoft.CodeAnalysis.CSharp.Scripting;
//using Microsoft.CodeAnalysis.Scripting;

namespace Cmdty.Storage.Test
{
    public sealed class BasisBuilderTest
    {

        [Fact]
        public void SomeTests()
        {
            BasisFunction spotPrice = Spot;

            BasisFunction spotPriceSquared1 = Spot.Pow(2);
            BasisFunction basis = Spot * Spot * X1 * X0.Pow(3);

            const int numSims = 3;
            var spotPriceSims = new double[] {25.69, 21.88, 16.78};
            var markovFactors = new ReadOnlyMemory<double>[]
            {
                new double[]{ 0.56, 0.12, 1.55},
                new double[]{ 1.08, 2.088, 0.988}
            };

            var result = new double[numSims];
            basis(markovFactors, spotPriceSims, result);

            for (int i = 0; i < numSims; i++)
            {
                double expectedBasis = spotPriceSims[i] * spotPriceSims[i] * markovFactors[1].Span[i] * Math.Pow(markovFactors[0].Span[i], 3);
                Assert.Equal(expectedBasis, result[i]);
            }

            BasisFunction[] basisFunctions = Spot + Spot * Spot + Spot.Pow(2) + X0 + X2 * X0.Pow(2);

            Assert.Equal(5, basisFunctions.Length);
        }

        //[Fact]
        //public void EvalExpression()
        //{
        //    ScriptOptions options = ScriptOptions.Default
        //        .WithReferences(@"C:\Users\Jake\source\repos\storage\tests\Cmdty.Storage.Test\bin\Release\netcoreapp3.1\Cmdty.Storage.dll")
        //        .WithImports("Cmdty.Storage.BasisFunctions.Builder");

        //    BasisFunction basis = CSharpScript.EvaluateAsync<BasisFunction>(
        //        "Spot * Spot * X1 * X0.Pow(3)", options).Result;

        //    const int numSims = 3;
        //    var spotPriceSims = new double[] { 25.69, 21.88, 16.78 };
        //    var markovFactors = new ReadOnlyMemory<double>[]
        //    {
        //        new double[]{ 0.56, 0.12, 1.55},
        //        new double[]{ 1.08, 2.088, 0.988}
        //    };

        //    var result = new double[numSims];
        //    basis(markovFactors, spotPriceSims, result);

        //    for (int i = 0; i < numSims; i++)
        //    {
        //        double expectedBasis = spotPriceSims[i] * spotPriceSims[i] * markovFactors[1].Span[i] * Math.Pow(markovFactors[0].Span[i], 3);
        //        Assert.Equal(expectedBasis, result[i]);
        //    }
        //}

    }
}
