using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    public sealed partial class RenderingRecallTests
    {

        static readonly JsonDocumentPath IdPath = P("id");

        static readonly SqlPredicate NotBikes = Ne(Text(CategoryPath), "bikes"), IsBikes = Eq(Text(CategoryPath), "bikes");

        static readonly string Spelled = SqlValue.Spell(CategoryPath);

        static ServiceForm Completed(CosmosServiceTest op) => ServiceForm.JunctionOf(CosmosServiceTest.Or,
            [ServiceForm.CompareOf(op, Spelled, JsonValues.Of("bikes")), ServiceForm.TestOf(CosmosServiceTest.IsNull, Spelled), ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsDefined, Spelled))]);

        static ServiceForm Id(CosmosServiceTest op, string id) => ServiceForm.CompareOf(op, SqlValue.Spell(IdPath), JsonValues.Of(id));

        /// <summary>
        /// The <c>CASE</c> family's recall (h13, line 15): the wrong answers 4b65754 sent for P7, P8, P9 and P18
        /// (<c>soundness.md</c> §2, causes C and D). The probe's texts were not kept, so each is reconstructed from its cause
        /// and held to keep on a service the rows that commit's pushdown kept among documents 1 to 7. Each case: its
        /// statement, the IR the loader reads, the text, its values, its meaning as a form, the part S is offered at the
        /// condition's atom at <c>Pos</c> and the least refutation by text it meets, the document source A finds, and the ids the
        /// text and Calcite keep over the differential's products.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, CaseRecall> CaseCases = new Dictionary<string, CaseRecall>
        {
            // C: the negation leaked into the condition, written completed.
            ["P7"] = new(Products + "NOT (CASE WHEN c.\"$.category\" <> 'bikes' THEN c.\"id\" = '1' ELSE c.\"id\" = '6' END)",
                Not(new SqlPredicate.Case(NotBikes, Eq(Text(IdPath), "1"), Eq(Text(IdPath), "6"))),
                "(NOT (((c.category != @p0) OR IS_NULL(c.category) OR NOT IS_DEFINED(c.category)) ? (c.id = @p1) : (c.id = @p2)))", [new("@p0", "bikes"), new("@p1", "1"), new("@p2", "6")],
                ServiceForm.NotOf(ServiceForm.ChosenOf(Completed(CosmosServiceTest.NotEqual), Id(CosmosServiceTest.Equal, "1"), Id(CosmosServiceTest.Equal, "6"))),
                NotBikes, Completed(CosmosServiceTest.NotEqual), "$[\"category\"]=absent (U,t)", "6",
                ["1", "10", "11", "2", "3", "4", "5", "6", "7", "8", "9"], ["1", "10", "11", "2", "3", "4", "5", "7", "8", "9"]),
            // C: COALESCE's operand written bare, a null's != true at the service, so its negation drops 6.
            ["P8"] = new(Products + "NOT COALESCE(c.\"$.category\" <> 'bikes', FALSE)",
                Not(new SqlPredicate.Case(NotBikes, new SqlPredicate.Constant(true), new SqlPredicate.Constant(false))),
                "(NOT ((c.category != @p0) ?? false))", [new("@p0", "bikes")],
                ServiceForm.NotOf(ServiceForm.ChosenOf(ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Spelled, JsonValues.Of("bikes")), ServiceForm.ConstantOf(true), ServiceForm.ConstantOf(false))),
                NotBikes, ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Spelled, JsonValues.Of("bikes")), "$[\"category\"]=array (U,t)", "6",
                ["1", "10", "2", "7", "8"], ["1", "10", "2", "6", "7", "8"]),
            // C: as P7, the condition an equality.
            ["P9"] = new(Products + "NOT (CASE WHEN c.\"$.category\" = 'bikes' THEN c.\"id\" = '1' ELSE c.\"id\" <> '6' END)",
                Not(new SqlPredicate.Case(IsBikes, Eq(Text(IdPath), "1"), Ne(Text(IdPath), "6"))),
                "(NOT (((c.category = @p0) OR IS_NULL(c.category) OR NOT IS_DEFINED(c.category)) ? (c.id = @p1) : (c.id != @p2)))", [new("@p0", "bikes"), new("@p1", "1"), new("@p2", "6")],
                ServiceForm.NotOf(ServiceForm.ChosenOf(Completed(CosmosServiceTest.Equal), Id(CosmosServiceTest.Equal, "1"), Id(CosmosServiceTest.NotEqual, "6"))),
                IsBikes, Completed(CosmosServiceTest.Equal), "$[\"category\"]=absent (U,t)", "7",
                ["10", "2", "6", "7", "8"], ["10", "2", "6", "8"]),
            // D: IS NULL over a comparison the service never leaves unknown.
            ["P18"] = new(Products + "(c.\"$.category\" <> 'bikes') IS NULL",
                new SqlPredicate.Case(NotBikes, new SqlPredicate.Constant(false), new SqlPredicate.Case(Not(NotBikes), new SqlPredicate.Constant(false), new SqlPredicate.Constant(true))),
                "IS_NULL((c.category != @p0))", [new("@p0", "bikes")], ServiceForm.ConstantOf(false),
                NotBikes, ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Spelled, JsonValues.Of("bikes")), "$[\"category\"]=array (U,t)", "7",
                [], ["6", "7"]),
        };

        public static TheoryData<string> CaseNames => [.. CaseCases.Keys];

        /// <summary>
        /// Each <c>CASE</c> recall: the text's meaning, held to Calcite by source A over the differential's products, finds the
        /// document pinned; offered to S as a candidate at the condition's atom at <c>Pos</c>, its wrong part is refuted at the
        /// cell pinned and never chooses the arm in S's best. Over the IR the non-legacy loader reads, which S reads from h15: the
        /// legacy reading S reads until then holds a <c>CASE</c>, a <c>COALESCE</c> and an <c>IS NULL</c> over a comparison
        /// opaque, so no production plan reaches them yet.
        /// </summary>
        [Theory]
        [MemberData(nameof(CaseNames))]
        public void ACaseIsRefutedAndNeverSentInItsPlace(string p)
        {
            var c = CaseCases[p];
            var (tally, world, targets) = (new RenderingSoundnessTests.Tally(), new World(15), Measured.Serves[RenderingSet.Emulator]);
            foreach (var document in CosmosDifferentialTests.Documents.Select(j => (J.Obj)CosmosEvalServiceTests.Fixture.Read(System.Text.Json.JsonDocument.Parse(j).RootElement)!))
                tally.Judge(RenderingSoundnessTests.Tally.Calcite(c.Ir, document, world), c.Ir, c.Meaning, Verdict.Equal, RenderPosition.Pos, document, targets, p);
            TestContext.Current.TestOutputHelper?.WriteLine($"{p}: {tally}; {string.Join(" | ", tally.Witnesses.Take(3))}");
            tally.Found.Select(f => CosmosEval.At(f.Document, SqlValue.Spell(IdPath))).OfType<J.Str>().Select(s => s.Value).Should().Contain(c.Found, "source A finds the document the text gets wrong");

            var theory = RenderingTheory.Define(Measured);
            theory.Graph.Rulesets.Single(r => r.Name == "spell").DefineRule($"planted: 4b65754's {c.Part.Text} at the condition", r =>
            {
                var (e, q, a) = r.Vars<Pred, JPath, PredicateAtom>("e", "q", "a");
                var n = r.Var<string>("n");
                r.When(theory.At.Of(e, RenderPosition.Pos), theory.Ir.Atom.Of(e, q, a), n.Is(theory.Spelling.Of(q)));
                r.Where(n, x => x == Spelled);
                r.Then(theory.Candidate.Of(e, RenderPosition.Pos, c.Part));
            });

            using var bundle = theory.Open(RenderingSet.Emulator);
            var term = bundle.Write(c.Ir);
            bundle.Import(Source, CategoryPath, PathValues.Anything, JsonValues.AllBands);
            bundle.Import(Source, IdPath, PathValues.OfKinds(ValueSet.Of(JsonValues.StringType)), JsonValues.AllBands);
            bundle.Generate(term);
            bundle.RunGenerate(RenderingBundle.WidthOf([c.Ir])).Should().Be(new RenderingRun(RunStopReason.Completed, true, false));

            var condition = bundle.Write(c.Condition);
            var refuted = bundle.Scope.Rows(bundle.Theory.Disagrees)
                .Where(row => Verdict.Equal.Equals(row.Keys[3]) && condition.Id.Equals(row.Keys[0]) && RenderPosition.Pos.Equals(row.Keys[1]) && c.Part.Equals(row.Keys[2]) && row.Output is Refutation)
                .Select(row => RenderingGenerateMutationTests.Show((Refutation)row.Output!)).Distinct().ToList();
            var best = bundle.ReadBest(term);
            TestContext.Current.TestOutputHelper?.WriteLine($"{p}: {c.Part.Text} at the condition, Pos: refuted at {string.Join("; ", refuted)}; S's best {best.Kind} {best.Best?.Text}");

            refuted.Should().Contain(c.Refuted, "the wrong part is refuted at the cell pinned");
            best.Kind.Should().Be(RenderingVerdictKind.Proven);
            best.Best!.Subforms.Where(f => f.Kind == ServiceFormKind.Choose).Select(f => f.Members[0]).Should().NotContain(c.Part, "S's best never chooses its arm by the wrong part");
        }

        /// <summary>On a service, each reconstructed text keeps what 4b65754's pushdown kept, and Calcite what it keeps; both over the eleven products.</summary>
        [Theory]
        [MemberData(nameof(CaseNames))]
        public async Task ACaseOnTheService(string p)
        {
            if (_service.Products is null)
                Assert.Skip("This needs a service. " + _service.Unavailable);

            var c = CaseCases[p];
            (await _service.Service(_service.Products, c.Text, c.Parameters)).Should().Equal(c.Pushed);
            (await _service.Calcite(c.Sql)).Should().Equal(c.Calcite);
        }

        /// <summary>A <c>CASE</c> recall (<see cref="CaseCases"/>).</summary>
        internal sealed record CaseRecall(string Sql, SqlPredicate Ir, string Text, CosmosParameter[] Parameters, ServiceForm Meaning, SqlPredicate Condition, ServiceForm Part,
            string Refuted, string Found, string[] Pushed, string[] Calcite);

    }

}
