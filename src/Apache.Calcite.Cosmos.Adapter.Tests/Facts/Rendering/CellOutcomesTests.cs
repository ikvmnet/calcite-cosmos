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
    /// The probe's checks as class sets, with no graph: text <c>label &lt;&gt; 'bikes'</c> against the guarded form the
    /// translator writes and the bare one, over an undeclared path (T1, and T5's verdicts) and a declared, required string
    /// (T2), on <see cref="ProbeRows"/>.
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

        static readonly string Label = SqlValue.Spell(JsonDocumentPath.Root.Property("label"));

        static readonly PredicateAtom Atom = PredicateAtom.Compare(PredicateAtom.TextReader, PredicateAtom.NotEqual, Of("bikes"));

        static readonly ServiceForm Bare = ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Label, Of("bikes"));

        static readonly ServiceForm Guarded = ServiceForm.JunctionOf(CosmosServiceTest.And,
            [ServiceForm.TestOf(CosmosServiceTest.IsPrimitive, Label), ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNull, Label)), Bare]);

        /// <summary>A form's vector on a partition, evaluated from its leaves up, as S's rules evaluate one.</summary>
        internal static OutcomeVector Evaluate(ServiceForm form, CellPartition cells, ServiceTable table, ValueSet<string> parted) => form.Members.Count == 0
            ? ServiceLeaf.On(form, cells, table, parted)
            : ServiceCompound.On(form, Evaluate(ServiceForm.First(form), cells, table, parted), Evaluate(ServiceForm.Rest(form), cells, table, parted), table);

        static CellOutcomes Outcomes(ServiceForm form, PathValues import)
        {
            var cuts = CutPoints.Of(Atom, form, new ReaderEdges(ValueSet<CutKind>.Empty, ValueSet<string>.Empty));
            return CellOutcomes.Of(Atom, Evaluate(form, PathCell.Split(new PathFacts(Label, import, AllBands), cuts), ProbeRows.Table, ValueSet<string>.Empty), AnswerMeanings.Measured);
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

            (guarded.Count, guarded.Answering, bare.Count, bare.Answering).Should().Be((11, 11, 11, 11));
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
                outcomes.Count.Should().Be(3);
            }
        }

        /// <summary>
        /// A value that is no boolean reads as the meanings say where its row keeps it not, and what the row keeps, and a
        /// failure, stand: a row that kept a number would refute what a narrowing to undefined would let pass.
        /// </summary>
        [Fact]
        public void AValueThatIsNoBooleanKeepsWhatItsRowKeeps()
        {
            var cells = PathCell.Split(new PathFacts(Label, PathValues.OfKinds(ValueSet.Of(IntegerType)), AllBands), ValueSet<CutPoint>.Empty);
            var vector = new OutcomeVector(cells, cells.Cells.Select(_ => new CellOutcome(t | f | E, CosmosValueClass.Number)).ToList(), ValueSet<string>.Empty);
            CellOutcomes.Of(Atom, vector, AnswerMeanings.Measured).Classes.Should().OnlyContain(k => k.Service == (t | u | E));
        }

        /// <summary>
        /// The guarded form is exact, and the bare one implied: a <c>WHERE</c> keeps the rows of the null, the object and the
        /// array that Calcite keeps neither way.
        /// </summary>
        [Fact]
        public void T5()
        {
            var guarded = Outcomes(Guarded, PathValues.Anything).Classes;
            guarded.Should().OnlyContain(k => Allowed(k, true));
            guarded.Should().NotContain(k => Refutes(k, true));

            var bare = Outcomes(Bare, PathValues.Anything);
            bare.Classes.Should().OnlyContain(k => Allowed(k, false));
            bare.Classes.Where(k => Refutes(k, true)).Should().Equal(C(U, t));
            bare.Classes.Should().NotContain(k => Refutes(k, false));
            CellsOf(bare, C(U, t)).Should().Equal(ValueSet.Of("null", ObjectType, ArrayType));
        }

        // F2 at Pos under decision 2: K = {T}, N = {F, U}, Ks = {t}, Ns = {f, u}. An allowing box sets a raise aside and
        // admits no failure; a refuting one needs Calcite to answer, never raising.
        static bool Allowed(CellClass k, bool equal)
        {
            var c = k.Calcite & ~Truths.Raises;
            return c == 0 && Within(k.Service, t | f | u)
                || Within(c, equal ? T : Truths.Any) && Within(k.Service, t)
                || Within(c, F | U) && Within(k.Service, equal ? f | u : t | f | u);
        }

        static bool Refutes(CellClass k, bool equal) => k.Calcite != 0 && (k.Calcite & Truths.Raises) == 0
            && (Within(k.Calcite, T) && Within(k.Service, f | u | E) || Within(k.Service, E) || equal && Within(k.Calcite, F | U) && Within(k.Service, t | E));

        static bool Within(Truths x, Truths box) => (x & ~box) == 0;

        static bool Within(ServiceOutcomes x, ServiceOutcomes box) => (x & ~box) == 0;

    }

}
