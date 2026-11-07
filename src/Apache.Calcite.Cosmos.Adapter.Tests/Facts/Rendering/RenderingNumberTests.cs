using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Numbers in S, under the asserted set: a typed ordering exact under decision 2, its value beside the raise; a wrong
    /// bound not proven, as it would be were the raise alone; a double's ordering exact on its double slots; and the parse
    /// of a string point a definite cell.
    /// </summary>
    public sealed class RenderingNumberTests
    {

        static readonly JsonDocumentPath TsPath = P("_ts"), NumberPath = P("p"), BodyPath = P("body");

        static readonly string Ts = SqlValue.Spell(TsPath), Number = SqlValue.Spell(NumberPath), Body = SqlValue.Spell(BodyPath);

        const decimal Stamp = 1700000000m;

        static readonly SqlPredicate Later = Cmp(Typed(TsPath, "BIGINT"), ">", SqlValue.Of(Stamp));

        static readonly PathValues NonNegative = PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType)), PathValues.Above(Bound.AtLeast(0m)));

        /// <summary>
        /// Typed <c>_ts &gt; 1700000000</c> over the integers from 0 is proven Equal: on the cells below, at and above the
        /// literal Calcite answers FALSE, FALSE and TRUE, each beside the raise <c>RETURNING</c> makes, and the service
        /// false, false and true.
        /// </summary>
        [Fact]
        public void ATypedOrderingIsExactUnderDecisionTwo()
        {
            var (verdict, outcomes) = Check(Later, ServiceForm.CompareOf(CosmosServiceTest.Greater, Ts, Of(Stamp)), TsPath, NonNegative);
            verdict.Kind.Should().Be(RenderingVerdictKind.Proven);
            new[] { 1m, Stamp, Stamp + 1 }.Select(x => outcomes!.Cells.Single(c => c.Cell.Contains(Of(x))).Class).Should().Equal(
                new CellClass(Truths.False | Truths.Raises, ServiceOutcomes.False), new CellClass(Truths.False | Truths.Raises, ServiceOutcomes.False),
                new CellClass(Truths.True | Truths.Raises, ServiceOutcomes.True));
        }

        /// <summary>
        /// <c>_ts &gt; 1700000001</c>, written for <c>_ts &gt; 1700000000</c>, is not proven: at 1700000001 the column reads
        /// TRUE and the service answers false. <c>RETURNING</c> raises there, so the cell is no definite refutation (the
        /// column's own reading is decision D12's); the plant that answers the raise alone on an int-sized <c>BIGINT</c>
        /// proves the wrong bound.
        /// </summary>
        [Fact]
        public void AnOffByOneIsNotProvenWhereTheRaiseAloneWouldProveIt()
        {
            var wrong = ServiceForm.CompareOf(CosmosServiceTest.Greater, Ts, Of(Stamp + 1));
            var (verdict, outcomes) = Check(Later, wrong, TsPath, NonNegative);
            verdict.Kind.Should().Be(RenderingVerdictKind.Unproven);
            outcomes!.Cells.Single(c => c.Cell.Contains(Of(Stamp + 1))).Class.Should().Be(new CellClass(Truths.True | Truths.Raises, ServiceOutcomes.False));
            IrEval.Truth(Later, new J.Obj(new SortedDictionary<string, J>(System.StringComparer.Ordinal) { ["_ts"] = new J.Num(Stamp + 1) }), new World(0), IrEval.TypedAs.Column)
                .Should().Be(Truths.True);

            var planted = RenderingTheory.Define(ProbeRows.Measured with
            {
                Pair = (atom, columns, vector) =>
                {
                    var real = CellOutcomes.Of(atom, columns, vector);
                    var cells = ValueSet.From(real.Cells.Select(c => atom.Reader == "typed:BIGINT" && IntSized(c.Cell) ? c with { Class = c.Class with { Calcite = Truths.Raises } } : c));
                    return real with { Cells = cells, Classes = ValueSet.From(cells.Select(c => c.Class)) };
                },
            });
            Check(Later, wrong, TsPath, NonNegative, planted).Verdict.Kind.Should().Be(RenderingVerdictKind.Proven, "the plant answers the raise alone where the column reads the value");

            static bool IntSized(PathCell cell) => cell is PathCell.Numbers { Integral: true } n && n.Side(int.MaxValue + 1m) == -1
                || cell is PathCell.Point { Value: var v } && TryNumberOf(v, out var x) && x <= int.MaxValue;
        }

        /// <summary><c>RETURNING DOUBLE &lt; 100.0</c> over an undeclared path is proven Equal: each double slot is one way.</summary>
        [Fact]
        public void ADoubleOrderingIsExact() =>
            Check(Cmp(Typed(NumberPath, "DOUBLE"), "<", SqlValue.Of(100.0m)), ServiceForm.CompareOf(CosmosServiceTest.Less, Number, Of(100m)), NumberPath, PathValues.Anything)
                .Verdict.Kind.Should().Be(RenderingVerdictKind.Proven);

        /// <summary>
        /// A number Calcite wrote cast is the constant a reading compares with where that reading's row takes it (G8):
        /// <c>RETURNING DOUBLE &lt; CAST(100.0 AS DOUBLE)</c> is proven Equal as the literal is. The 2.5 a cast to
        /// <c>DECIMAL(19, 0)</c> truncates is a constant of <c>RETURNING DECIMAL</c> alone, whose parse rescales the literal
        /// itself, and of no reading that compares the double exactly; and a cast to a <c>DECIMAL</c> its whole digits do not
        /// fit, which raises, denotes nothing, so its comparison is no atom.
        /// </summary>
        [Fact]
        public void ANumberCalciteWroteCastIsTheConstantItsReadingTakes()
        {
            var below = Cmp(Typed(NumberPath, "DOUBLE"), "<", Cast("DOUBLE", SqlValue.Of(100.0m)));
            Check(below, ServiceForm.CompareOf(CosmosServiceTest.Less, Number, Of(100m)), NumberPath, PathValues.Anything).Verdict.Kind.Should().Be(RenderingVerdictKind.Proven);
            Atoms(below).Should().Equal(PredicateAtom.Compare(PredicateAtom.TypedReader("DOUBLE"), "<", Of(100m)));
            Atoms(Cmp(Cast("DOUBLE", Text(BodyPath)), "<=", Cast("DOUBLE", SqlValue.Of(30.5m)))).Should().Equal(
                PredicateAtom.Compare(PredicateAtom.CastReader("DOUBLE", PredicateAtom.TextReader), "<=", Of(30.5m)));

            var moved = new SqlValue.DecimalCast(19, 0, SqlValue.Of(2.5m));
            Atoms(Cmp(Typed(NumberPath, "DOUBLE"), "=", Cast("DOUBLE", moved))).Should().BeEmpty("RETURNING DOUBLE compares the 2 Calcite truncated to, which is not 2.5");
            Atoms(Cmp(Cast("DOUBLE", Text(BodyPath)), "=", Cast("DOUBLE", moved))).Should().BeEmpty("and so does a cast of the accessor");
            Atoms(Cmp(Typed(BodyPath, "DECIMAL"), "=", moved)).Should().Equal([PredicateAtom.Compare(PredicateAtom.TypedReader("DECIMAL"), "=", Of(2.5m))],
                "RETURNING DECIMAL's parse rescales the literal itself");

            Atoms(Cmp(Typed(BodyPath, "DECIMAL"), "=", new SqlValue.DecimalCast(19, 18, SqlValue.Of(50m)))).Should().BeEmpty("50 has two whole digits, DECIMAL(19, 18) one");
            Atoms(Cmp(Typed(BodyPath, "DECIMAL"), "=", new SqlValue.DecimalCast(19, 18, SqlValue.Of(5m)))).Should().Equal(PredicateAtom.Compare(PredicateAtom.TypedReader("DECIMAL"), "=", Of(5m)));
            Atoms(Cmp(Typed(BodyPath, "DECIMAL"), "=", new SqlValue.DecimalCast(1, 1, SqlValue.Of(0.05m)))).Should().Equal([PredicateAtom.Compare(PredicateAtom.TypedReader("DECIMAL"), "=", Of(0.05m))],
                "0.05 has no whole digit, and DECIMAL(1, 1) holds it, moved to 0.0");
        }

        /// <summary>
        /// A reading's row takes a number a cast that drops scale moved only where its atom reads the move: over a stored 2,
        /// <c>= 2.5</c> may be TRUE, as Calcite's 2.5 cast to scale zero is 2
        /// (<c>CalciteJsonValueMeasurementTests.ReturningDecimalReadsAStoredFractionExactlyAndTruncatesItsLiteral</c>).
        /// </summary>
        [Fact]
        public void AReadingTakesAMovedNumberOnlyWhereItsAtomReadsTheMove()
        {
            var moved = ProbeRows.Measured.Comparands.Where(r => r.Moved).ToList();
            moved.Should().NotBeEmpty();
            foreach (var (reading, _, row) in moved)
                PredicateAtom.Compare(reading, "=", Of(2.5m)).OnCell(new PathCell.Point(Of(2m)), ProbeRows.Columns).Should().HaveFlag(Truths.True, $"{row} says {reading} takes a moved number");

            PredicateAtom.Compare(PredicateAtom.TypedReader("DOUBLE"), "=", Of(2.5m)).OnCell(new PathCell.Point(Of(2m)), ProbeRows.Columns).Should().NotHaveFlag(Truths.True,
                "RETURNING DOUBLE reads no move, and no row says it takes one");
        }

        /// <summary>Each atom S's own analysis reads of a conjunct.</summary>
        static IReadOnlyList<PredicateAtom> Atoms(SqlPredicate conjunct) => [.. RenderingTemplatesTests.AtomsOf([conjunct]).Select(a => a.Atom)];

        /// <summary>
        /// <c>typed:DECIMAL = 2</c> is <c>RETURNING DECIMAL</c>'s parse and a <c>DECIMAL</c> column at once (decision D12): over
        /// a declared string the cell <c>"2"</c>, the literal's rendering, is TRUE to the clause, which parses it, beside the
        /// column's raise, so the path written for the accessor, as M10 writes it, is not refuted there; and a fraction whose
        /// double is 2, which the clause reads as 2 and the column as itself, is TRUE or FALSE beside the service's true.
        /// </summary>
        [Fact]
        public void ADecimalReadsTheClausesParseBesideTheColumns()
        {
            var form = ServiceForm.CompareOf(CosmosServiceTest.Equal, Body, Of(2m));
            var (verdict, outcomes) = Check(Cmp(Typed(BodyPath, "DECIMAL"), "=", SqlValue.Of(2m)), form, BodyPath, PathValues.OfKinds(ValueSet.Of(StringType)));
            outcomes!.Cells.Single(c => c.Cell == new PathCell.Point(Of("2"))).Class.Calcite.Should().Be(Truths.True | Truths.Raises);
            verdict.Kind.Should().Be(RenderingVerdictKind.Unproven);

            (verdict, outcomes) = Check(Cmp(Typed(BodyPath, "DECIMAL"), "=", SqlValue.Of(2m)), form, BodyPath, PathValues.OfKinds(ValueSet.Of(FractionType)));
            outcomes!.Cells.Single(c => c.Cell.Contains(Of(2.0000000000000001m))).Class.Should().Be(new CellClass(Truths.True | Truths.False, ServiceOutcomes.True));
            verdict.Kind.Should().Be(RenderingVerdictKind.Unproven);
        }

        /// <summary>
        /// A cast's parse trims as Java does, every character up to U+0020: <c>CAST(… AS INTEGER) = 12</c> is TRUE of the string
        /// U+0001 then 12, so its path compared with <c>"12"</c> is refuted there; and <c>BIGINT</c> reads text shaped as a
        /// timestamp as one, which no cell says.
        /// </summary>
        [Fact]
        public void ACastsParseTrimsAsJavaDoes()
        {
            var padded = Of("\u000112");
            var (verdict, outcomes) = Check(Cmp(Cast("INTEGER", Text(BodyPath)), "=", SqlValue.Of(12m)), ServiceForm.CompareOf(CosmosServiceTest.Equal, Body, Of("12")), BodyPath,
                PathValues.OneOf(ValueSet.Of(padded, Of("5"))));
            outcomes!.Cells.Single(c => c.Cell == new PathCell.Point(padded)).Class.Should().Be(new CellClass(Truths.True, ServiceOutcomes.False));
            verdict.Kind.Should().Be(RenderingVerdictKind.Refuted);

            var stamp = Of("1990-01-01 00:00:05");
            Check(Cmp(Cast("BIGINT", Text(BodyPath)), "=", SqlValue.Of(5m)), ServiceForm.CompareOf(CosmosServiceTest.Equal, Body, Of("5")), BodyPath, PathValues.OneOf(ValueSet.Of(stamp)))
                .Outcomes!.Cells.Single(c => c.Cell == new PathCell.Point(stamp)).Class.Calcite.Should().Be(Truths.True | Truths.False | Truths.Raises);
        }

        /// <summary>Checks a claim of a form for a conjunct over one path, imported whole, under the asserted set.</summary>
        static (RenderingVerdict Verdict, CellOutcomes? Outcomes) Check(SqlPredicate conjunct, ServiceForm form, JsonDocumentPath path, PathValues values, RenderingTheory? theory = null)
        {
            theory ??= ProbeRows.ProbeTheory;
            using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(conjunct);
            bundle.Import(ProbeRows.Source, path, values, AllBands);
            bundle.Claim(root, form);
            bundle.Run().Should().Be(new RenderingRun(RunStopReason.Completed, true, false));
            return (bundle.Read(root, form, Verdict.Equal), bundle.Scope.TryGet(theory.Outcomes.Of(root, form), out var outcomes) ? outcomes : null);
        }

    }

}
