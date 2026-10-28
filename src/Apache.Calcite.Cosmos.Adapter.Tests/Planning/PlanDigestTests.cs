using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;

using FluentAssertions;

using org.apache.calcite.rel;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning
{

    /// <summary>
    /// The plan digests (<see cref="PlanDigests"/>), which hold a commit that claims to change no plan to the claim.
    /// </summary>
    public class PlanDigestTests
    {

        /// <summary>
        /// The corpus planned twice over one harness schema — a reasoning of its own, its build awaited first, and one
        /// harness's tables — each statement in a flow S writes through that schema's oracle, as production does since the
        /// switch — admission, and the split's weakenings, which read the closing bundle where one answers them — and
        /// measures what S was asked: each statement's digest, the bundles its plan opened, and whether its plan holds a
        /// filter; and the harness's counts after both (line 22).
        /// </summary>
        static readonly Lazy<((string Sql, string Line, int Bundles, bool Filtered)[][] Passes, (long Hits, long Bundles, long Declines) Counts)> Twice = new(() =>
        {
            var harness = new HarnessReasonings();
            var reasoning = harness.Make();
            using (harness.Planning())
            {
                var corpus = RenderingInputs.Corpus(reasoning).ToList();
                return ([Pass(reasoning, corpus), Pass(reasoning, corpus)], harness.Counts);
            }
        });

        static (string, string, int, bool)[] Pass(CosmosReasoning reasoning, IReadOnlyList<RenderingInputs.Statement> corpus) => [.. corpus.Select(statement =>
        {
            RelNode? planned = null;
            using var shadow = CosmosRenderingShadow.Arm(new RenderingShadowCounter(reasoning.Rendering, []));
            using var cost = CosmosRenderingCost.Measure(out var samples);
            var line = PlanDigests.Lines([statement with { Plan = () => planned = statement.Plan() }]).Single();
            return (statement.Sql, line, samples.Sum(s => s.Cost.Bundles), planned is not null && PlanDigests.Filters(planned));
        })];

        /// <summary>
        /// Writes every digest to <c>TestResults/rendering/plan-digests.tsv</c>, or to the file
        /// <c>COSMOS_PLAN_DIGESTS</c> names: the partition run's first 200 predicates, or its 1,000 where
        /// <c>COSMOS_PARTITION_GATE=1</c>; every statement planned over one harness schema, its build awaited first,
        /// and no question S was asked declined.
        /// </summary>
        /// <remarks>
        /// Run at a commit's parent and at the commit, with <c>-explicit only</c>. The two files must be byte-equal,
        /// and any difference refutes the claim.
        /// </remarks>
        [Fact(Explicit = true)]
        public void WriteDigests()
        {
            var path = Path.GetFullPath(Environment.GetEnvironmentVariable("COSMOS_PLAN_DIGESTS") ?? Path.Combine("TestResults", "rendering", "plan-digests.tsv"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var harness = new HarnessReasonings();
            var reasoning = harness.Make();
            using (harness.Planning())
                File.WriteAllText(path, string.Join("\n", PlanDigests.All(CosmosDifferentialTests.PartitionedPredicates, reasoning)) + "\n");

            // A decline leaves its conjunct residual with no weakening, a digest no shadow row explains. Every statement is
            // planned in the harness's reasoning, which a table that made its own, or was bound to another, would fail.
            TestContext.Current.TestOutputHelper?.WriteLine($"the harness's reasonings: {harness.Counts}");
            harness.Counts.Declines.Should().Be(0, "the switch's listing holds no plan a decline changed");
        }

        /// <summary>
        /// Line 21 at the switch (spec g16): the gate's digests, every statement planned with S writing over one harness
        /// schema, differ from <c>main</c>'s exactly where the Writes-armed rehearsal listed them (<c>Switch/switch-listing.tsv</c>):
        /// each listed line is the one the rehearsal recorded, by its hash; every other line is <c>main</c>'s, by the hash of
        /// them all; and each listed change is of a class the switch admits, the shadow's row for it read at the rehearsal.
        /// </summary>
        /// <remarks>
        /// The unarmed shadow cannot list it: it follows the translator's rounds, and S's own admissions change later
        /// rounds' nodes. So the listing is the rehearsal's digests against <c>main</c>'s, each change classed by the shadow's
        /// rows for its statement and, where the shadow could not read the translator's weakening, by reading the two
        /// weakenings side by side: an element path, <c>LIKE</c> and arithmetic, each O7's (<c>TODO.md</c>). Run at the
        /// gate, with <c>-explicit only</c>.
        /// </remarks>
        [Fact(Explicit = true)]
        public void TheSwitchChangesOnlyWhatTheShadowListed()
        {
            var harness = new HarnessReasonings();
            var reasoning = harness.Make();
            List<string> lines;
            using (harness.Planning())
                lines = [.. PlanDigests.All(CosmosDifferentialTests.GatePartitionedPredicates, reasoning)];

            var listing = Listing();
            var changed = listing.Where(l => Sha16(lines[l.Line - 1]) != l.Switch).Select(l => $"{l.Line} {l.Input} {l.Name}: {Sha16(lines[l.Line - 1])}, listed {l.Switch}").ToList();
            using var unlisted = SHA256.Create();
            var listed = listing.Select(l => l.Line).ToHashSet();
            for (var i = 0; i < lines.Count; i++)
                if (listed.Contains(i + 1) == false)
                    unlisted.TransformBlock(Encoding.UTF8.GetBytes(lines[i] + "\n"), 0, Encoding.UTF8.GetByteCount(lines[i] + "\n"), null, 0);
            unlisted.TransformFinalBlock([], 0, 0);

            TestContext.Current.TestOutputHelper?.WriteLine($"{lines.Count} statements, {listing.Count} listed: " + string.Join(", ", listing.GroupBy(l => l.Why.Split(':')[0]).Select(g => $"{g.Key} {g.Count()}")));
            harness.Counts.Declines.Should().Be(0, "a decline changes a plan for a reason no row lists");
            lines.Should().HaveCount(GateStatements);
            changed.Should().BeEmpty("each listed statement plans as the rehearsal recorded");
            Convert.ToHexString(unlisted.Hash!).ToLowerInvariant().Should().Be(MainUnlisted, "every statement the listing does not name plans as at main");
            listing.Where(l => l.Main == l.Switch || Admitted.Any(a => l.Why.StartsWith(a, StringComparison.Ordinal)) == false).Should().BeEmpty("each listed change is one the switch admits");
        }

        /// <summary>The gate's statements: the corpus, the differential's lists, the partition run's 1,000 predicates' and the probe.</summary>
        const int GateStatements = 3_492;

        /// <summary>The SHA-256 of <c>main</c>'s gate digests the listing names none of, each line ending in a newline, in order.</summary>
        const string MainUnlisted = "9403d752ebb497955a86db369494d9b6e8706d171409f98aa955cb14813ac0d1";

        /// <summary>
        /// The classes the switch admits (spec g16): a gain, a respelling, a regression O2 lists, a weakening tighter or alike,
        /// one looser or dropped on O7's list, and the two the rehearsal found benign, a weakening's Rex typed otherwise and a
        /// conjunct admitted a round earlier, each with its plan's SQL unchanged.
        /// </summary>
        static readonly string[] Admitted = ["gain", "respelled", "regression (O2)", "tighter", "alike", "looser, O7", "dropped, O7", "benign"];

        /// <summary>The first 16 hexadecimal digits of a digest line's SHA-256, as the listing names them.</summary>
        static string Sha16(string line) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)))[..16].ToLowerInvariant();

        /// <summary>The switch's listing, one row a changed statement: its gate line, input and name, class, why, and its line's hash at <c>main</c> and at the switch.</summary>
        static List<(int Line, string Input, string Name, string Class, string Why, string Main, string Switch)> Listing()
        {
            using var reader = new StreamReader(typeof(PlanDigestTests).Assembly.GetManifestResourceStream("Switch/switch-listing.tsv")!);
            return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).Select(l => l.Split('\t'))
                .Select(f => (int.Parse(f[0], CultureInfo.InvariantCulture), f[1], f[2], f[3], f[4], f[5], f[6]))];
        }

        /// <summary>
        /// Line 22: the corpus planned twice in one process digests alike with S writing: what a planning run leaves behind
        /// in the oracle's memory changes no later plan, or a digest compared across two commits would compare the order
        /// they ran in; the memory the harness schema's, both passes over one harness.
        /// </summary>
        [Fact]
        public void TheDigestsAreDeterministic() => Twice.Value.Passes[1].Select(s => s.Line).Should().Equal(Twice.Value.Passes[0].Select(s => s.Line));

        /// <summary>
        /// Lines 26 and 27, riding the same two passes over one harness schema: the second plan of each statement opens no
        /// bundle, every question a remembered answer; a statement whose plan holds no filter opens none in either; and no
        /// question the harness's reasoning was asked declined (line 20).
        /// </summary>
        [Fact]
        public void RepeatsOpenNoBundle()
        {
            var (first, again) = (Twice.Value.Passes[0], Twice.Value.Passes[1]);
            var unfiltered = first.Where(s => s.Filtered == false).Select(s => s.Sql).ToHashSet();
            TestContext.Current.TestOutputHelper?.WriteLine($"{first.Length} statements: {first.Sum(s => s.Bundles)} bundles first, {again.Sum(s => s.Bundles)} again; {unfiltered.Count} with no filter; the harness's reasoning: {Twice.Value.Counts}");
            Twice.Value.Counts.Declines.Should().Be(0, "no question declines in CI");
            again.Where(s => s.Bundles > 0).Select(s => $"{s.Bundles}: {s.Sql}").Should().BeEmpty("a plan again asks S only what it remembers");
            first.Concat(again).Where(s => unfiltered.Contains(s.Sql) && s.Bundles > 0).Select(s => s.Sql).Should().BeEmpty("a statement with no filter asks S nothing");
            unfiltered.Should().NotBeEmpty();
            first.Sum(s => s.Bundles).Should().BePositive("S was asked");
        }

        /// <summary>
        /// While a harness plans, a table over a fixture that made a reasoning of its own fails, and so does one bound to the
        /// suite's reasoning, which no count of the harness's reads; outside one, the suite's is the fixtures'.
        /// </summary>
        [Fact]
        public void AHarnessFailsATableOutsideItsReasonings()
        {
            var unbound = new CosmosContainerMetadata("apart", ["/pk"]);
            var shared = TestReasoning.Bind(unbound);
            using (new HarnessReasonings().Planning())
            {
                FluentActions.Invoking(() => TestReasoning.Checked(new CosmosTable(unbound), unbound)).Should().Throw<InvalidOperationException>("a table over an unbound fixture makes a reasoning of its own");
                FluentActions.Invoking(() => TestReasoning.Checked(new CosmosTable(shared), shared)).Should().Throw<InvalidOperationException>("the harness counts nothing of the suite's reasoning");
            }

            TestReasoning.Checked(new CosmosTable(shared), shared).Container.Reasoning.Should().BeSameAs(TestReasoning.Shared);
        }

    }

}
