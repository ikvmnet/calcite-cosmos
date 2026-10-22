using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The probe's checks as class sets, with no graph: text <c>label &lt;&gt; 'bikes'</c> against the guarded form the
    /// translator writes and the bare one, over an undeclared path (T1) and a declared, required string (T2), on
    /// <see cref="ProbeRows"/>; T5's verdicts are the gate's (<see cref="RenderingGateTests"/>).
    /// </summary>
    public sealed class CellOutcomesTests
    {

        const Truths T = Truths.True;
        const Truths F = Truths.False;
        const Truths U = Truths.Unknown;
        const ServiceOutcomes t = ServiceOutcomes.True;
        const ServiceOutcomes f = ServiceOutcomes.False;
        const ServiceOutcomes u = ServiceOutcomes.Undefined;
        const ServiceOutcomes E = ServiceOutcomes.Fails;

        static readonly PredicateAtom Atom = PredicateAtom.Compare(PredicateAtom.TextReader, PredicateAtom.NotEqual, Of("bikes"));

        /// <summary>A form's vector on a partition, evaluated from its leaves up, as S's rules evaluate one.</summary>
        internal static OutcomeVector Evaluate(ServiceForm form, CellPartition cells, ServiceTable table, ValueSet<string> parted) => form.Members.Count == 0
            ? ServiceLeaf.On(form, cells, table, parted)
            : ServiceCompound.On(form, Evaluate(ServiceForm.First(form), cells, table, parted), Evaluate(ServiceForm.Rest(form), cells, table, parted), table);

        static CellOutcomes Outcomes(ServiceForm form, PathValues import)
        {
            var cuts = CutPoints.Of(Atom, form, ReaderEdgesTable.Rows[PredicateAtom.TextReader]);
            return CellOutcomes.Of(Atom, Evaluate(form, PathCell.Split(Label, import, AllBands, cuts), ProbeRows.Table, ValueSet<string>.Empty), AnswerMeanings.Measured);
        }

        static CellClass C(Truths calcite, ServiceOutcomes service) => new(calcite, service);

        static ValueSet<string> CellsOf(CellOutcomes outcomes, CellClass k) => ValueSet.From(outcomes.Cells.Where(c => c.Class == k).Select(c => c.Cell.Text));

        [Fact]
        public void T1()
        {
            var guarded = Outcomes(Guarded, PathValues.Anything);
            guarded.Classes.Should().Equal(ValueSet.Of(C(U, f), C(T, t), C(F, f)));

            var bare = Outcomes(Bare, PathValues.Anything);
            bare.Classes.Should().Equal(ValueSet.Of(C(U, u), C(U, t), C(T, t), C(F, f)));
            CellsOf(bare, C(U, t)).Should().Equal(ValueSet.Of("null", ObjectType, ArrayType));
            CellsOf(bare, C(U, u)).Should().Equal(ValueSet.Of("absent"));

            (guarded.Cells.Count, bare.Cells.Count).Should().Be((11, 11));
            guarded.Rows.Concat(bare.Rows).Should().NotContain(row => row.StartsWith("missing:"), "the probe's rows hold every key the two forms read");
            guarded.Rows.Should().Contain(Atom.Rests);
        }

        [Fact]
        public void T2()
        {
            foreach (var form in new[] { Bare, Guarded })
            {
                var outcomes = Outcomes(form, PathValues.OfKinds(ValueSet.Of(StringType)));
                outcomes.Classes.Should().Equal(ValueSet.Of(C(T, t), C(F, f)), form.Text);
                outcomes.Cells.Count.Should().Be(3);
            }
        }

        /// <summary>
        /// A value that is no boolean reads as the meanings say where its row keeps it not, and what the row keeps, and a
        /// failure, stand: a row that kept a number would refute what a narrowing to undefined would let pass.
        /// </summary>
        [Fact]
        public void AValueThatIsNoBooleanKeepsWhatItsRowKeeps()
        {
            var cells = PathCell.Split(Label, PathValues.OfKinds(ValueSet.Of(IntegerType)), AllBands, ValueSet<CutPoint>.Empty);
            var vector = new OutcomeVector(cells, cells.Cells.Select(_ => new CellOutcome(t | f | E, CosmosValueClass.Number)).ToList(), ValueSet<string>.Empty);
            CellOutcomes.Of(Atom, vector, AnswerMeanings.Measured).Classes.Should().OnlyContain(k => k.Service == (t | u | E));
        }

    }

}
