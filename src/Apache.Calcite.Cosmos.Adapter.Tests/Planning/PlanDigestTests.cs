using System;
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
        /// The corpus planned twice, each statement in a flow that asks S at production's sites beside the translator
        /// through the oracle of the reasoning the harnesses share, whose build is awaited first — admission, and the
        /// split's weakenings, which read the closing bundle where one answers them — and measures what S was asked:
        /// each statement's digest, the bundles its plan opened, and whether its plan holds a filter. Where the
        /// whole-suite rehearsal arms writing, S writes here too (line 22's rehearsal).
        /// </summary>
        static readonly Lazy<(string Sql, string Line, int Bundles, bool Filtered)[][]> Twice = new(() =>
        {
            TestReasoning.Shared.Rendering.Ready.Wait();
            return [Pass(), Pass()];
        });

        static (string, string, int, bool)[] Pass() => [.. RenderingInputs.Corpus().Select(statement =>
        {
            RelNode? planned = null;
            using var shadow = CosmosRenderingShadow.Arm(new RenderingShadowCounter(TestReasoning.Shared.Rendering, []));
            using var cost = CosmosRenderingCost.Measure(out var samples);
            var line = PlanDigests.Lines([statement with { Plan = () => planned = statement.Plan() }]).Single();
            return (statement.Sql, line, samples.Sum(s => s.Cost.Bundles), planned is not null && PlanDigests.Filters(planned));
        })];

        /// <summary>
        /// Writes every digest to <c>TestResults/rendering/plan-digests.tsv</c>, or to the file
        /// <c>COSMOS_PLAN_DIGESTS</c> names: the partition run's first 200 predicates, or its 1,000 where
        /// <c>COSMOS_PARTITION_GATE=1</c>. Under the whole-suite rehearsal, no question S was asked declined.
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
            File.WriteAllText(path, string.Join("\n", PlanDigests.All(CosmosDifferentialTests.PartitionedPredicates)) + "\n");

            // A decline leaves its conjunct residual with no weakening, a digest no shadow row explains. Every statement is
            // planned in the shared reasoning, which a harness's table that made its own would fail (TestReasoning.Checked).
            TestContext.Current.TestOutputHelper?.WriteLine($"the shared reasoning's oracle: {TestReasoning.Shared.Rendering.Counts}");
            if (RenderingRehearsal.Armed)
                TestReasoning.Shared.Rendering.Counts.Declines.Should().Be(0, "the rehearsal's listing holds no plan a decline changed");
        }

        /// <summary>
        /// The corpus planned twice in one process digests alike: what a planning run leaves behind in the oracle's
        /// memory changes no later plan, or a digest compared across two commits would compare the order they ran in; S
        /// asked beside the translator in both, whose memory is the shared reasoning's.
        /// </summary>
        [Fact]
        public void TheDigestsAreDeterministic() => Twice.Value[1].Select(s => s.Line).Should().Equal(Twice.Value[0].Select(s => s.Line));

        /// <summary>
        /// Lines 26 and 27, riding the same two passes: the second plan of each statement opens no bundle, every question a
        /// remembered answer; and a statement whose plan holds no filter opens none in either.
        /// </summary>
        [Fact]
        public void RepeatsOpenNoBundle()
        {
            var (first, again) = (Twice.Value[0], Twice.Value[1]);
            var unfiltered = first.Where(s => s.Filtered == false).Select(s => s.Sql).ToHashSet();
            TestContext.Current.TestOutputHelper?.WriteLine($"{first.Length} statements: {first.Sum(s => s.Bundles)} bundles first, {again.Sum(s => s.Bundles)} again; {unfiltered.Count} with no filter");
            again.Where(s => s.Bundles > 0).Select(s => $"{s.Bundles}: {s.Sql}").Should().BeEmpty("a plan again asks S only what it remembers");
            first.Concat(again).Where(s => unfiltered.Contains(s.Sql) && s.Bundles > 0).Select(s => s.Sql).Should().BeEmpty("a statement with no filter asks S nothing");
            unfiltered.Should().NotBeEmpty();
            first.Sum(s => s.Bundles).Should().BePositive("S was asked");
        }

    }

}
