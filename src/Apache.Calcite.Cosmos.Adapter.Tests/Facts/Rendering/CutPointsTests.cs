using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The edges a reader names of a numeric literal, by arithmetic: one either side, its rendering, and the double it rounds
    /// to, spelled as the literal, which the split rounds; and where a reader's double edge parts its cells.
    /// </summary>
    public sealed class CutPointsTests
    {

        static readonly string P = SqlValue.Spell(JsonDocumentPath.Root.Property("n"));

        static readonly ReaderEdges Every = new(ValueSet.Of(CutKind.Below, CutKind.Above, CutKind.Double, CutKind.Rendering), ValueSet.Of(Of(2147483647m)));

        [Theory]
        [InlineData("9007199254740993")]
        [InlineData("-2.5")]
        [InlineData("0")]
        [InlineData("0.1")]
        [InlineData("79228162514264337593543950330")]
        public void EdgesAreTheReadersArithmetic(string literal)
        {
            var c = decimal.Parse(literal, CultureInfo.InvariantCulture);
            var atom = PredicateAtom.Compare(PredicateAtom.TextReader, PredicateAtom.Less, Of(c));
            CutPoints.Of(atom, ServiceForm.CompareOf(CosmosServiceTest.Less, P, Of("x")), Every).Should().Equal(ValueSet.Of(
                new CutPoint(Of(c), CutKind.Literal), new CutPoint(Of("x"), CutKind.Literal), new CutPoint(Of(2147483647m), CutKind.Boundary),
                new CutPoint(Of(c - 1), CutKind.Below), new CutPoint(Of(c + 1), CutKind.Above), new CutPoint(Of(StringOf(Of(c))), CutKind.Rendering),
                new CutPoint(Of(c), CutKind.Double)));
        }

        /// <summary>
        /// <c>BIGINT</c>'s double edge at 2^53 + 1 parts the integers into the five slots: below its double; at it and below
        /// the literal, where 2^53 lies; the literal; at it and above, where no integer lies, so no cell; and above.
        /// </summary>
        [Fact]
        public void ADoubleEdgePartsTheIntegersIntoItsSlots()
        {
            const decimal c = 9007199254740993m;
            var atom = PredicateAtom.Compare(PredicateAtom.TypedReader("BIGINT"), PredicateAtom.Equal, Of(c));
            var cells = PathCell.Split(P, PathValues.OfKinds(ValueSet.Of(IntegerType)), AllBands,
                CutPoints.Of(atom, ServiceForm.BareOf(P), ReaderEdgesTable.Rows[atom.Reader])).Cells;

            var d = DoubleBound.Of(c);
            cells.Should().ContainSingle(cell => cell.Contains(Of(c - 2))).Which.Should().Match<PathCell.Numbers>(n => n.DoubleHigh == System.Math.BitDecrement(d));
            cells.Should().ContainSingle(cell => cell.Contains(Of(c - 1))).Which.Should().Match<PathCell.Numbers>(n => n.DoubleLow == d && n.DoubleHigh == d);
            cells.Should().ContainSingle(cell => cell.Contains(Of(c))).Which.Should().Be(new PathCell.Point(Of(c)));
            cells.Should().ContainSingle(cell => cell.Contains(Of(c + 1))).Which.Should().Match<PathCell.Numbers>(n => n.DoubleLow == System.Math.BitIncrement(d));
            cells.OfType<PathCell.Numbers>().Should().NotContain(n => n.DoubleHigh == d && n.Side(c) == 1);
        }

    }

}
