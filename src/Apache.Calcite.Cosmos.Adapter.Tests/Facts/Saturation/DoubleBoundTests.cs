using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The double a number rounds to (<see cref="DoubleBound"/>), at the rounding boundaries, each converted both ways: a
    /// decimal to the double its text parses as, and that double back against the decimal, exactly; and the cast, which
    /// rounds twice, caught where it misses.
    /// </summary>
    public sealed class DoubleBoundTests
    {

        /// <summary>
        /// 0.1's double lies above it and no decimal holds it; 2^53 and 1e22 are doubles; 2^53 + 1 ties to the even 2^53;
        /// and at 9007199254740994.4 the cast misses. Each double is the nearest: its neighbours lie either side.
        /// </summary>
        [Theory]
        [InlineData("0.1", 1)]
        [InlineData("9007199254740992", 0)]
        [InlineData("9007199254740993", -1)]
        [InlineData("10000000000000000000000", 0)]
        [InlineData("9007199254740994.4", -1)]
        public void TheRoundingBoundariesConvertBothWays(string text, int side)
        {
            var x = decimal.Parse(text, CultureInfo.InvariantCulture);
            var d = DoubleBound.Of(x);
            (d, DoubleBound.Of(Of(x))).Should().Be((double.Parse(text, CultureInfo.InvariantCulture), d));
            (DoubleBound.Compare(d, x), DoubleBound.Compare(Math.BitDecrement(d), x), DoubleBound.Compare(Math.BitIncrement(d), x)).Should().Be((side, -1, 1));
        }

        /// <summary>The largest double lies past every decimal, and a number past a decimal is held as its own double.</summary>
        [Fact]
        public void TheLargestDoubleLiesPastEveryDecimal()
        {
            (DoubleBound.Compare(double.MaxValue, decimal.MaxValue), DoubleBound.Compare(-double.MaxValue, decimal.MinValue)).Should().Be((1, -1));
            DoubleBound.Of(Of(double.MaxValue)).Should().Be(double.MaxValue);
            DoubleBound.Compare(double.Epsilon, 0m).Should().Be(1);
        }

        /// <summary>
        /// At 9007199254740994.4 the cast rounds to 2^53 + 4 where the text's double is 2^53 + 2, so a <c>DOUBLE</c> equality
        /// with 9007199254740994 is TRUE of it, read either way, and the cell answers so. The plant that converts by the cast
        /// answers FALSE there, which the evaluator refutes.
        /// </summary>
        [Fact]
        public void TheCastIsCaughtAtARoundingBoundary()
        {
            const decimal stored = 9007199254740994.4m, literal = 9007199254740994m;
            ((double)stored).Should().NotBe(DoubleBound.Of(stored));

            var atom = PredicateAtom.Compare(PredicateAtom.TypedReader("DOUBLE"), PredicateAtom.Equal, Of(literal));
            var predicate = new SqlPredicate.Compare(PredicateAtom.Equal, new SqlValue.Typed(JsonDocumentPath.Root.Property("a"), "DOUBLE"), new SqlValue.Literal(Of(literal)));
            var document = new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal) { ["a"] = new J.Num(stored) });
            var truths = new[] { IrEval.TypedAs.Returning, IrEval.TypedAs.Column }.Select(mode => IrEval.Truth(predicate, document, new World(0), mode)).ToList();

            truths.Should().Equal(Truths.True, Truths.True);
            atom.OnCell(new PathCell.Point(Of(stored))).Should().Be(Truths.True);
            ((double)stored == (double)literal ? Truths.True : Truths.False).Should().Be(Truths.False, "the plant answers what the evaluator refutes");
        }

    }

}
