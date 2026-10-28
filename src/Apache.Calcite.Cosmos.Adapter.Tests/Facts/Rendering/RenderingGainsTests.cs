using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

using Op = Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The gains line (spec g05, g15; line 19; gates map §5), read off the generate shadow of each statement under each
    /// set, as production's sites asked S: P1 sent exactly, as the printer writes it; P2 and P3 exactly over a mixed-kind
    /// twin of <c>typed</c> whose declaration keeps U+0000 out of the label, and under <c>Production</c>; P4's comparisons
    /// weakened tighter than J, or as O7 lists; P5's second arm keeping its kind, in the text its pushed Rex translates
    /// to; P6, O7's listed loss, its comparison over a cast no member the Rex builder writes; P8 counted; P7, already
    /// pushed, not S's to regress. On a service each exact gain keeps Calcite's rows.
    /// </summary>
    public sealed class RenderingGainsTests : IClassFixture<RenderingGainsTests.Fixture>
    {

        const string Typed = "SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') ",
            View = "SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" ";

        const string P6 = "SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" = 'bikes' AND CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50)",
            P7 = "SELECT c.\"id\" FROM strings AS c WHERE JSON_VALUE(c.\"DOC\", '$.s') LIKE 'a%'";

        /// <summary><c>typed</c> declaring only that a label's strings are printable ASCII, whatever its kind: its documents keep it.</summary>
        static readonly CosmosContainerMetadata Twin = TestReasoning.Bind(new CosmosContainerMetadata("typed", ["/category"]))
            .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse("""{ "type": "object", "properties": { "label": { "pattern": "^[ -~]*$" } } }""")!));

        static readonly string Label = ProbeRows.Label;

        static ServiceForm Compare(Op op, string literal) => ServiceForm.CompareOf(op, Label, Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.Of(literal));

        static ServiceForm Is(Op test) => ServiceForm.TestOf(test, Label);

        /// <summary>Each exact gain, the statement, the containers it is planned over, the sets it must be Exact under, its form and that form as the translator would write it.</summary>
        static readonly (string Sql, bool Twin, RenderingSet[] Sets, ServiceForm Form, string Text)[] Exact =
        [
            (Typed + "<> 'bikes'", false, [.. RenderingGenerateRun.Sets], ProbeRows.All(Is(Op.IsDefined), ProbeRows.Negated(Is(Op.IsNull)), Is(Op.IsPrimitive), Compare(Op.NotEqual, "bikes")),
                "(IS_DEFINED(c.label) AND NOT IS_NULL(c.label) AND IS_PRIMITIVE(c.label) AND (c.label != @p0))"),
            (View + "<> 'bikes'", false, [.. RenderingGenerateRun.Sets], ProbeRows.All(Is(Op.IsDefined), ProbeRows.Negated(Is(Op.IsNull)), Is(Op.IsPrimitive), Compare(Op.NotEqual, "bikes")),
                "(IS_DEFINED(c.label) AND NOT IS_NULL(c.label) AND IS_PRIMITIVE(c.label) AND (c.label != @p0))"),
            (Typed + "> 'bikes'", true, [.. RenderingGenerateRun.Sets], ProbeRows.Any(Compare(Op.Greater, "bikes"), Is(Op.IsBool)), "((c.label > @p0) OR IS_BOOL(c.label))"),
            (Typed + "< 'bikes'", true, [.. RenderingGenerateRun.Sets], ProbeRows.Any(Compare(Op.Less, "bikes"), Is(Op.IsNumber)), "((c.label < @p0) OR IS_NUMBER(c.label))"),
            (View + "> 'bikes'", true, [.. RenderingGenerateRun.Sets], ProbeRows.Any(Compare(Op.Greater, "bikes"), Is(Op.IsBool)), "((c.label > @p0) OR IS_BOOL(c.label))"),
            (Typed + "> 'bikes'", false, [RenderingSet.Production], ProbeRows.Any(Compare(Op.Greater, "bikes"), Is(Op.IsBool)), "((c.label > @p0) OR IS_BOOL(c.label))"),
            (Typed + "< 'bikes'", false, [RenderingSet.Production], ProbeRows.Any(Compare(Op.Less, "bikes"), Is(Op.IsNumber)), "((c.label < @p0) OR IS_NUMBER(c.label))"),
        ];

        /// <summary>The gains exact under every set over <c>typed</c> itself, each statement with its text: what the witnesses run.</summary>
        internal static IEnumerable<(string Sql, string Text)> ExactEverywhere => Exact.Where(e => e.Twin == false && e.Sets.Length == RenderingGenerateRun.Sets.Count()).Select(e => (e.Sql, e.Text));

        /// <summary>P4's statements that are no <c>LIKE</c>.</summary>
        static readonly string[] Weakened = [Typed + "<> '30'", Typed + ">= '30'", View + ">= '30'"];

        /// <summary>
        /// P8's statements, which the translator writes longer than S: a double negation, and an inequality whose guards a
        /// sibling's exact form makes vacuous.
        /// </summary>
        static readonly string[] Shorter =
        [
            "SELECT c.\"id\" FROM products AS c WHERE NOT (NOT (c.\"$.category\" = 'bikes'))",
            "SELECT c.\"id\" FROM kinds AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') <> 'b' AND JSON_VALUE(c.\"DOC\", '$.kind') <> 'c'",
        ];

        /// <summary>The statements planned over the differential's containers, and over the twin, each named by its place.</summary>
        static readonly List<string> PlainSql = [.. Exact.Where(e => e.Twin == false).Select(e => e.Sql).Distinct(), .. Weakened, P6, P7, .. Shorter],
            TwinSql = [.. Exact.Where(e => e.Twin).Select(e => e.Sql).Distinct()];

        static readonly Lazy<RenderingCheckRun> Plain = new(() => Shadowed("gains", PlainSql, [.. CosmosDifferentialTests.Containers]));

        static readonly Lazy<RenderingCheckRun> Twinned = new(() => Shadowed("twin", TwinSql, [Twin]));

        static readonly Lazy<RenderingCheckRun> Probe = new(() =>
        {
            var kinds = CosmosDifferentialTests.Containers.Single(c => c.Name == "kinds");
            var (name, where) = CosmosPushdownProbeTests.KindProbes.Single(p => p.Name == "each kind spelled per arm");
            var sql = $"SELECT c.\"id\" FROM kinds AS c WHERE {where}";
            return RenderingCheckRun.Over([new RenderingInputs.Statement("probe", name, sql, () => CosmosPushdownProbeTests.Plan(sql, kinds))], shadow: new RenderingGenerateRun(), writes: false);
        });

        readonly Fixture _service;

        public RenderingGainsTests(Fixture service) => _service = service;

        static RenderingCheckRun Shadowed(string input, IReadOnlyList<string> sql, CosmosContainerMetadata[] containers) =>
            RenderingCheckRun.Over(RenderingInputs.Differential(input, sql, containers), shadow: new RenderingGenerateRun(), writes: false);

        /// <summary>A statement's name in a run: its place among the statements planned.</summary>
        static string NameOf(List<string> sql, string statement) => sql.IndexOf(statement).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The shadow's classes of a statement's conjuncts under a set, by its SQL.</summary>
        static List<RenderingGenerateRun.Classed> Classes(RenderingCheckRun run, string sql, RenderingSet set, List<string> inputs) =>
            [.. run.Shadow!.Classes.Where(c => c.Set == set && c.Name == NameOf(inputs, sql))];

        /// <summary>
        /// P1, P2 and P3: each statement's conjunct is a gain under each set it is listed for, sent as the form listed, which
        /// the translator's conventions spell as the text listed. Nothing in the runs failed to plan or to shadow.
        /// </summary>
        [Fact]
        public void P1P2P3AreSentExactly()
        {
            Plain.Value.Failures.Concat(Twinned.Value.Failures).Should().BeEmpty();
            foreach (var (sql, twin, sets, form, text) in Exact)
            {
                CosmosSqlFormReader.Read(text, [RenderingRecallTests.Bikes]).Form.Should().Be(form, text);
                RenderingPrinter.Print(form, new Dictionary<string, Apache.Calcite.Cosmos.Facts.JsonDocumentPath> { [Label] = ProbeRows.LabelPath }, "c", new CosmosParameterList())
                    .Should().Be(text, "the printer writes S's form as listed");
                foreach (var set in RenderingGenerateRun.Sets)
                {
                    var classes = twin ? Classes(Twinned.Value, sql, set, TwinSql) : Classes(Plain.Value, sql, set, PlainSql);
                    TestContext.Current.TestOutputHelper?.WriteLine($"{set} {(twin ? "twin" : "typed")} {sql}: {string.Join("; ", classes.Select(c => c.Class + " " + c.Form?.Text))}");
                    if (sets.Contains(set))
                        classes.Should().Contain(c => c.Class == RenderingGenerateRun.Gain && form.Equals(c.Form), $"{sql} under {set}{(twin ? " over the twin" : "")}");
                }
            }
        }

        /// <summary>
        /// P4: each comparison that is no <c>LIKE</c> is weakened tighter than J weakens it, or no looser but where O7 lists the
        /// loss: J's weakening unproven to S at the weakening's key, which S cannot send. Each class is reported.
        /// </summary>
        [Fact]
        public void P4IsTighterOrAsO7Lists()
        {
            var tighter = 0;
            foreach (var sql in Weakened)
                foreach (var set in RenderingGenerateRun.Sets)
                {
                    var rows = Plain.Value.Shadow!.Weakenings.Where(w => w.Set == set && w.Name == NameOf(PlainSql, sql) && w.Text is not null).ToList();
                    var classes = rows.Select(RenderingGenerateRun.ClassOf).ToList();
                    TestContext.Current.TestOutputHelper?.WriteLine($"{set} {sql}: " + string.Join("; ", rows.Select((w, i) => $"J {w.Text} [{w.TranslatorVerdict}], S {w.Mine?.Text}: {classes[i]}")));
                    rows.Should().NotBeEmpty(sql);
                    classes.Should().NotContain(c => c == "looser" || c == "parting", $"{sql} under {set}");
                    tighter += classes.Count(c => c == "tighter");
                }

            tighter.Should().BePositive("P4's inequality is tighter under every set");
        }

        /// <summary>
        /// P5 as restated (§4.3): probe T4's disjunction, each kind spelled per arm, has no exact form, and its weakening
        /// keeps the second arm's <c>c.kind = 'c'</c>, in the text its Rex translates to as the split rule would push it. P6 is O7's listed loss until Stage 3: its weakening beneath the
        /// <c>NOT</c> holds no comparison of the price with a number, since the cast parses a string Calcite then compares
        /// where the service compares no string, so no accessor of the conjunct writes C4's
        /// <c>IS_NUMBER(c.price) AND c.price &gt; 50</c> as it means. Stage 3's guarded spelling, once the loader reads
        /// <c>IS_NUMBER</c> and <c>CASE</c>, delivers it. Under each set.
        /// </summary>
        [Fact]
        public void P5IsWeakenedAndP6IsAListedLoss()
        {
            var kindC = ServiceForm.CompareOf(Op.Equal, Apache.Calcite.Cosmos.Facts.SqlValue.Spell(ProbeRows.KindPath), Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.Of("c"));
            var price = Apache.Calcite.Cosmos.Facts.SqlValue.Spell(OracleFixture.P("price"));
            Probe.Value.Failures.Should().BeEmpty();
            foreach (var set in RenderingGenerateRun.Sets)
            {
                var t4 = Probe.Value.Shadow!.Weakenings.Where(w => w.Set == set && w.Mine is { Kind: ServiceFormKind.Or }).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set} P5: " + string.Join("; ", t4.Select(w => w.Mine!.Text)));
                t4.Should().NotBeEmpty().And.OnlyContain(w => w.Mine!.Members.Any(arm => arm.Subforms.Contains(kindC)), $"P5 under {set}");

                // What the split rule pushes for it (site W): the Rex S's weakening builds, as the translator writes it.
                var pushed = Probe.Value.Shadow!.Pushes.Where(w => w.Set == set && w.Form.Kind == ServiceFormKind.Or).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set} P5 pushed: " + string.Join("; ", pushed.Select(w => w.Text)));
                pushed.Should().NotBeEmpty().And.OnlyContain(w => w.Text != null && w.Text.Contains("(c.kind = @p", StringComparison.Ordinal), $"P5's pushed text under {set}");

                var p6 = Plain.Value.Shadow!.Weakenings.Where(w => w.Set == set && w.Name == NameOf(PlainSql, P6)).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set} P6: " + string.Join("; ", p6.Select(w => w.Mine?.Text)));
                p6.Should().NotBeEmpty().And.OnlyContain(w => w.Mine == null || w.Mine.Subforms.All(f => f.Kind != ServiceFormKind.Compare || f.Path != price), $"P6 under {set}");
                Plain.Value.Shadow!.Pushes.Where(w => w.Set == set && w.Name == NameOf(PlainSql, P6)).Should().OnlyContain(w => w.Text == null || w.Text.Contains("c.price >", StringComparison.Ordinal) == false,
                    $"P6's pushed text under {set} compares no price");
            }
        }

        /// <summary>
        /// P8: a conjunct the translator pushes that S sends exactly as a form of fewer nodes, under each set, read beside the
        /// translator, since the pull request's run plans with S writing; and P7, <c>LIKE 'a%'</c> over a declared string,
        /// the translator's, pushed and not S's.
        /// </summary>
        [Fact]
        public void P8IsCountedAndP7IsTheTranslators()
        {
            var shadow = Plain.Value.Shadow!;
            foreach (var set in RenderingGenerateRun.Sets)
            {
                var shorter = shadow.Classes.Where(c => c.Set == set && c.Class == RenderingGenerateRun.Respelled && c.Form!.Nodes < c.TextForm!.Nodes).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}: P8, {shorter.Count} sent shorter: " + string.Join("; ", shorter.Take(5).Select(c => $"{c.Text} as {c.Form!.Text}")));
                shorter.Should().NotBeEmpty($"at least one P8 under {set}");
            }

            var p7 = Plain.Value.Shadow!.Classes.Where(c => c.Name == NameOf(PlainSql, P7)).ToList();
            p7.Should().NotBeEmpty().And.OnlyContain(c => c.Class == RenderingGenerateRun.Written && c.TranslatorRound > 0, "P7 is pushed, and stays the translator's");
        }

        /// <summary>On a service each exact gain's text keeps the ids Calcite keeps for its statement.</summary>
        [Fact]
        public async Task OnTheService()
        {
            if (_service.Typed is null)
                Assert.Skip("This needs a service. " + _service.Unavailable);

            foreach (var (sql, _, _, _, text) in Exact.DistinctBy(e => (e.Sql, e.Text)))
                (await _service.Service(_service.Typed, text, [RenderingRecallTests.Bikes])).Should().Equal(await _service.Calcite(sql), text);
        }

        /// <summary>A database of the gains' own.</summary>
        public sealed class Fixture() : RenderingRecallTests.Fixture("gains");

    }

}
