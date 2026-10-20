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
    /// The edges a reader names of a numeric literal, by arithmetic: one either side, its rendering, and the double it
    /// rounds to, exactly, which a decimal's own conversion is not.
    /// </summary>
    public sealed class CutPointsTests
    {

        static readonly string P = SqlValue.Spell(JsonDocumentPath.Root.Property("n"));

        static readonly ReaderEdges Every = new(ValueSet.Of(CutKind.Below, CutKind.Above, CutKind.Double, CutKind.Rendering), ValueSet.Of(Of(2147483647m)));

        [Theory]
        [InlineData("9007199254740993", "9007199254740992")]
        [InlineData("-2.5", "-2.5")]
        [InlineData("0", "0")]
        [InlineData("100000000000000000000", "100000000000000000000")]
        [InlineData("0.1", null)]
        [InlineData("79228162514264337593543950330", null)]
        public void EdgesAreTheReadersArithmetic(string literal, string? rounded)
        {
            var c = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);
            var atom = PredicateAtom.Compare(PredicateAtom.TextReader, PredicateAtom.Less, Of(c));
            var cuts = CutPoints.Of(atom, ServiceForm.CompareOf(CosmosServiceTest.Less, P, Of("x")), Every);

            var expected = new[]
            {
                new CutPoint(Of(c), CutKind.Literal), new CutPoint(Of("x"), CutKind.Literal), new CutPoint(Of(2147483647m), CutKind.Boundary),
                new CutPoint(Of(c - 1), CutKind.Below), new CutPoint(Of(c + 1), CutKind.Above), new CutPoint(Of(StringOf(Of(c))), CutKind.Rendering),
            };
            cuts.Should().Equal(ValueSet.From(rounded is null ? expected : expected.Append(new CutPoint(Of(decimal.Parse(rounded, System.Globalization.CultureInfo.InvariantCulture)), CutKind.Double))));
        }

    }

}
