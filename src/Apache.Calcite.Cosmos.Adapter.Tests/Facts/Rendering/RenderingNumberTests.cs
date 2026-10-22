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
                Pair = (atom, vector, means) =>
                {
                    var real = CellOutcomes.Of(atom, vector, means);
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
        /// <c>RETURNING DECIMAL = 2</c> over a declared string has the definite cell <c>"2"</c>, the literal's rendering: Calcite
        /// parses it and answers TRUE, and the path written for the accessor, as M10 writes it, is refuted there.
        /// </summary>
        [Fact]
        public void TheParseOfAStringPointIsADefiniteCell()
        {
            var (verdict, outcomes) = Check(Cmp(Typed(BodyPath, "DECIMAL"), "=", SqlValue.Of(2m)), ServiceForm.CompareOf(CosmosServiceTest.Equal, Body, Of(2m)), BodyPath,
                PathValues.OfKinds(ValueSet.Of(StringType)));
            outcomes!.Cells.Single(c => c.Cell == new PathCell.Point(Of("2"))).Class.Calcite.Should().Be(Truths.True);
            verdict.Kind.Should().Be(RenderingVerdictKind.Refuted);
            verdict.Refutation!.Cells.Should().Equal(ValueSet.Of(Body + "=" + Of("2")));
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
