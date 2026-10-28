using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        /// harness's tables — each statement in a flow that asks S at production's sites beside the translator through
        /// that schema's oracle — admission, and the split's weakenings, which read the closing bundle where one answers
        /// them — and measures what S was asked: each statement's digest, the bundles its plan opened, and whether its
        /// plan holds a filter; and the harness's counts after both. Where the whole-suite rehearsal arms writing, S
        /// writes here too (line 22's rehearsal).
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
        /// <c>COSMOS_PARTITION_GATE=1</c>; every statement planned over one harness schema, its build awaited first.
        /// Under the whole-suite rehearsal, no question S was asked declined.
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
            if (RenderingRehearsal.Armed)
                harness.Counts.Declines.Should().Be(0, "the rehearsal's listing holds no plan a decline changed");
        }

        /// <summary>
        /// The corpus planned twice in one process digests alike: what a planning run leaves behind in the oracle's
        /// memory changes no later plan, or a digest compared across two commits would compare the order they ran in; S
        /// asked beside the translator in both, whose memory is the harness schema's, both passes over one harness.
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
