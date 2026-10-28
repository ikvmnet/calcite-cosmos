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
    /// The gains line (spec g05, g15, g17; line 19; gates map §5), read off what each statement sent under each set, as
    /// production's sites asked S in a schema of the run's own: P1 sent exactly, as the printer writes it; P2 and P3 exactly
    /// over a mixed-kind twin of <c>typed</c> whose declaration keeps U+0000 out of the label, and under <c>Production</c>;
    /// P4's comparisons weakened tighter than J weakened them at the switch, or as O7 lists; P5's second arm keeping its
    /// kind, in the text its pushed Rex translates to; P6, O7's listed loss, its comparison over a cast no member the Rex
    /// builder writes; P8 counted against the translator's text; P7, already pushed, not S's to regress. On a service each
    /// exact gain keeps Calcite's rows.
    /// </summary>
    public sealed class RenderingGainsTests : IClassFixture<RenderingGainsTests.Fixture>
    {

        const string Typed = "SELECT c.\"id\" FROM typed AS c WHERE JSON_VALUE(c.\"DOC\", '$.label') ",
            View = "SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" ";

        const string P6 = "SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"$.category\" = 'bikes' AND CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) > 50)",
            P7 = "SELECT c.\"id\" FROM strings AS c WHERE JSON_VALUE(c.\"DOC\", '$.s') LIKE 'a%'";

        /// <summary><c>typed</c> declaring only that a label's strings are printable ASCII, whatever its kind: its documents keep it.</summary>
        static readonly CosmosContainerMetadata Twin = new CosmosContainerMetadata("typed", ["/category"])
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
        /// J's weakening of each of P4's statements as the shadow read it at the switch (g16), J's text read back: what S's are
        /// held to once J is no longer asked, and is deleted (g18).
        /// </summary>
        static readonly ServiceForm[] Js =
        [
            J("(IS_DEFINED(c.label) AND ((NOT IS_STRING(c.label)) OR (IS_DEFINED(c.label) AND NOT IS_NULL(c.label) AND (c.label != @p0))))"),
            J("(IS_DEFINED(c.label) AND ((NOT IS_STRING(c.label)) OR (c.label >= @p0)))"),
            J("(IS_DEFINED(c.label) AND ((NOT IS_STRING(c.label)) OR (c.label >= @p0)))"),
        ];

        static ServiceForm J(string text) => CosmosSqlFormReader.Read(text, [new CosmosParameter("@p0", "30")]).Form;

        /// <summary>O7's loss 5 among P4's: J's ordering of a string unproven to S at the weakening's key, which S cannot send, under the sets whose rows read it so at the switch.</summary>
        static bool ListedLoss(string sql, RenderingSet set) => sql.EndsWith(">= '30'", StringComparison.Ordinal) && set != RenderingSet.Production;

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

        static readonly Lazy<RenderingCheckRun> Plain = new(() => Generated("gains", PlainSql, CosmosDifferentialTests.Containers));

        static readonly Lazy<RenderingCheckRun> Twinned = new(() => Generated("twin", TwinSql, [Twin]));

        static readonly Lazy<RenderingCheckRun> Probe = new(() =>
        {
            var generated = new RenderingGenerateRun();
            var kinds = generated.Bind([CosmosDifferentialTests.Containers.Single(c => c.Name == "kinds")]).Single();
            var (name, where) = CosmosPushdownProbeTests.KindProbes.Single(p => p.Name == "each kind spelled per arm");
            var sql = $"SELECT c.\"id\" FROM kinds AS c WHERE {where}";
            return RenderingCheckRun.Over([new RenderingInputs.Statement("probe", name, sql, () => CosmosPushdownProbeTests.Plan(sql, kinds))], generated: generated);
        });

        readonly Fixture _service;

        public RenderingGainsTests(Fixture service) => _service = service;

        static RenderingCheckRun Generated(string input, IReadOnlyList<string> sql, IEnumerable<CosmosContainerMetadata> containers)
        {
            var generated = new RenderingGenerateRun();
            return RenderingCheckRun.Over(RenderingInputs.Differential(input, sql, generated.Bind(containers)), generated: generated);
        }

        /// <summary>A statement's name in a run: its place among the statements planned.</summary>
        static string NameOf(List<string> sql, string statement) => sql.IndexOf(statement).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>What a statement sent of its conjuncts under a set, by its SQL.</summary>
        static List<RenderingGenerateRun.Sent> Sends(RenderingCheckRun run, string sql, RenderingSet set, List<string> inputs) =>
            [.. run.Generated!.Sends.Where(c => c.Set == set && c.Name == NameOf(inputs, sql))];

        /// <summary>The weakenings the split rule pushed for a statement, as its Rex translates: the rendering check's third site, under the statement's own set.</summary>
        static List<string> Pushed(RenderingCheckRun run, string name) => [.. run.Conjuncts.Where(c => c.Site == 3 && c.Name == name).Select(c => c.Text).Distinct()];

        /// <summary>
        /// P1, P2 and P3: each statement's conjunct is sent exactly under each set it is listed for, as the form listed, which
        /// the translator's conventions spell as the text listed. Nothing in the runs failed to plan.
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
                    var sends = twin ? Sends(Twinned.Value, sql, set, TwinSql) : Sends(Plain.Value, sql, set, PlainSql);
                    TestContext.Current.TestOutputHelper?.WriteLine($"{set} {(twin ? "twin" : "typed")} {sql}: {string.Join("; ", sends.Select(c => (c.Owned ? "S " : "translator ") + c.Form?.Text))}");
                    if (sets.Contains(set))
                        sends.Should().Contain(c => form.Equals(c.Form), $"{sql} under {set}{(twin ? " over the twin" : "")}");
                }
            }
        }

        /// <summary>
        /// P4: each comparison that is no <c>LIKE</c> is weakened, in every statement its plan holds, tighter than J weakened it
        /// at the switch, or alike, or looser only where O7 lists the loss: J's weakening unproven to S at the weakening's key,
        /// which S cannot send. Each is compared on the documents source A draws from its weakening bundle's import, on every
        /// target its set serves, and reported.
        /// </summary>
        [Fact]
        public void P4IsTighterOrAsO7Lists()
        {
            var tighter = 0;
            foreach (var (sql, j) in Weakened.Zip(Js))
                foreach (var set in RenderingGenerateRun.Sets)
                {
                    var rows = Plain.Value.Generated!.Weakenings.Where(w => w.Set == set && w.Name == NameOf(PlainSql, sql) && w.Residual && w.Mine is not null).ToList();
                    var classes = rows.Select(w => Against(w, j)).ToList();
                    TestContext.Current.TestOutputHelper?.WriteLine($"{set} {sql}: J {j.Text}; " + string.Join("; ", rows.Select((w, i) => $"S {w.Mine!.Text}: {classes[i]}")));
                    rows.Should().NotBeEmpty(sql);
                    classes.Should().NotContain(c => c == "parting" || c == "looser" && ListedLoss(sql, set) == false, $"{sql} under {set}");
                    tighter += classes.Count(c => c == "tighter");
                }

            tighter.Should().BePositive("P4's inequality is tighter under every set");
        }

        /// <summary>
        /// S's weakening against J's: the same form; tighter, keeping no document J's does not and dropping one it keeps;
        /// looser; parting both ways; or alike, on the documents drawn from its bundle's import, on every target its set serves.
        /// </summary>
        static string Against(RenderingGenerateRun.Weakened w, ServiceForm j)
        {
            if (w.Mine!.Equals(j))
                return "same";

            var (fewer, more) = (false, false);
            foreach (var document in w.Claim is null ? [] : RenderingDocumentSampler.Sample(w.Claim))
                foreach (var target in ProbeRows.Measured.Serves[w.Set])
                {
                    var (mine, theirs) = (CosmosEval.On(w.Mine, document, target), CosmosEval.On(j, document, target));
                    if (mine is null || theirs is null)
                        continue;

                    fewer |= theirs == ServiceOutcomes.True && mine != ServiceOutcomes.True;
                    more |= mine == ServiceOutcomes.True && theirs != ServiceOutcomes.True;
                }

            return (fewer, more) switch { (true, false) => "tighter", (false, true) => "looser", (true, true) => "parting", _ => "alike on the documents" };
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
                var t4 = Probe.Value.Generated!.Weakenings.Where(w => w.Set == set && w.Mine is { Kind: ServiceFormKind.Or }).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set} P5: " + string.Join("; ", t4.Select(w => w.Mine!.Text)));
                t4.Should().NotBeEmpty().And.OnlyContain(w => w.Mine!.Members.Any(arm => arm.Subforms.Contains(kindC)), $"P5 under {set}");

                var p6 = Plain.Value.Generated!.Weakenings.Where(w => w.Set == set && w.Name == NameOf(PlainSql, P6)).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set} P6: " + string.Join("; ", p6.Select(w => w.Mine?.Text)));
                p6.Should().NotBeEmpty().And.OnlyContain(w => w.Mine == null || w.Mine.Subforms.All(f => f.Kind != ServiceFormKind.Compare || f.Path != price), $"P6 under {set}");
            }

            // What the split rule pushed for each, under the statement's own set: the Rex S's weakening builds, as the
            // translator writes it.
            var (p5, p6Pushed) = (Pushed(Probe.Value, Probe.Value.Conjuncts.First().Name), Pushed(Plain.Value, NameOf(PlainSql, P6)));
            TestContext.Current.TestOutputHelper?.WriteLine($"P5 pushed: {string.Join("; ", p5)}; P6 pushed: {string.Join("; ", p6Pushed)}");
            p5.Where(t => t.Contains(" OR ", StringComparison.Ordinal)).Should().NotBeEmpty().And.OnlyContain(t => t.Contains("(c.kind = @p", StringComparison.Ordinal), "P5's pushed text");
            p6Pushed.Should().OnlyContain(t => t.Contains("c.price >", StringComparison.Ordinal) == false, "P6's pushed text compares no price");
        }

        /// <summary>
        /// P8: a conjunct S sends exactly as a form of fewer nodes than the text the translator writes of it from the same
        /// stratum, read back, under each set; and P7, <c>LIKE 'a%'</c> over a declared string, the translator's, pushed and
        /// not S's.
        /// </summary>
        [Fact]
        public void P8IsCountedAndP7IsTheTranslators()
        {
            var generated = Plain.Value.Generated!;
            foreach (var set in RenderingGenerateRun.Sets)
            {
                var shorter = generated.Sends.Where(c => c.Set == set && c.Form is not null).Select(c => (Sent: c, Theirs: Translated(c)))
                    .Where(c => c.Theirs is { } theirs && c.Sent.Form!.Nodes < theirs.Nodes).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}: P8, {shorter.Count} sent shorter: " + string.Join("; ", shorter.Take(5).Select(c => $"{c.Theirs!.Text} as {c.Sent.Form!.Text}")));
                shorter.Should().NotBeEmpty($"at least one P8 under {set}");
            }

            var p7 = generated.Sends.Where(c => c.Name == NameOf(PlainSql, P7)).ToList();
            p7.Should().NotBeEmpty().And.OnlyContain(c => c.Owned == false && c.Round > 0, "P7 is pushed, and stays the translator's");
        }

        /// <summary>The form the translator writes a conjunct as from the stratum its statement renders it from, read back; none where it writes none, or one the reader does not read as written.</summary>
        static ServiceForm? Translated(RenderingGenerateRun.Sent c)
        {
            var parameters = new CosmosParameterList();
            return c.Source.TranslatorFor(c.Conjunct, parameters).TryTranslateConjunct(c.Source.Conjuncts[c.Conjunct], out var text)
                && CosmosSqlFormReader.Read(text!, parameters.Parameters) is var read && read.Written == text && RenderingCheckRun.Opaque(read.Form) == false ? read.Form : null;
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
