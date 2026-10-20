using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// The probe's cells, as <see cref="PathCell.Split"/> cuts them and Calcite answers on them: text <c>label &lt;&gt; 'bikes'</c>
    /// over an undeclared path (T1, and T5's check) and over a declared, required string (T2).
    /// </summary>
    public sealed class ProbeCellTests
    {

        const Truths T = Truths.True;
        const Truths F = Truths.False;
        const Truths U = Truths.Unknown;

        [Fact]
        public void T1_T2_T5Cells()
        {
            var atom = PredicateAtom.Compare(PredicateAtom.TextReader, PredicateAtom.NotEqual, Of("bikes"));
            var cuts = ValueSet.Of(new CutPoint(Of("bikes"), CutKind.Literal));
            var label = SqlValue.Spell(JsonDocumentPath.Root.Property("label"));
            var none = ValueSet<string>.Empty;

            var undeclared = new (PathCell Cell, Truths Answer)[]
            {
                (new PathCell.Absent(), U), (new PathCell.Null(), U), (new PathCell.Point(Of(true)), T), (new PathCell.Point(Of(false)), T),
                (new PathCell.Strings(null, Of("bikes"), none, AllBands), T), (new PathCell.Point(Of("bikes")), F), (new PathCell.Strings(Of("bikes"), null, none, AllBands), T),
                (new PathCell.Numbers(true, null, null, none), T), (new PathCell.Numbers(false, null, null, none), T),
                (new PathCell.Structure(ObjectType), U), (new PathCell.Structure(ArrayType), U),
            };
            PathCell.Split(new PathFacts(label, PathValues.Anything, AllBands), cuts).Cells.Should().Equal(ValueSet.From(undeclared.Select(c => c.Cell)));
            undeclared.Select(c => atom.OnCell(c.Cell)).Should().Equal(undeclared.Select(c => c.Answer));

            var declared = new (PathCell Cell, Truths Answer)[]
            {
                (new PathCell.Strings(null, Of("bikes"), none, AllBands), T), (new PathCell.Point(Of("bikes")), F), (new PathCell.Strings(Of("bikes"), null, none, AllBands), T),
            };
            PathCell.Split(new PathFacts(label, PathValues.OfKinds(ValueSet.Of(StringType)), AllBands), cuts).Cells.Should().Equal(ValueSet.From(declared.Select(c => c.Cell)));
            declared.Select(c => atom.OnCell(c.Cell)).Should().Equal(declared.Select(c => c.Answer));
        }

    }

}
