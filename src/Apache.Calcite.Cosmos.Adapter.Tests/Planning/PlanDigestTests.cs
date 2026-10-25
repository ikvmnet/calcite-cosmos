using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;

using FluentAssertions;

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
        /// through the oracle of the reasoning the harnesses share, whose build is awaited first, and measures what S
        /// was asked: each statement's digest, and the bundles its plan opened. Where the whole-suite rehearsal arms
        /// writing, S writes here too (line 22's rehearsal).
        /// </summary>
        static readonly Lazy<(string Sql, string Line, int Bundles)[][]> Twice = new(() =>
        {
            TestReasoning.Shared.Rendering.Ready.Wait();
            return [Pass(), Pass()];
        });

        static (string, string, int)[] Pass() => [.. RenderingInputs.Corpus().Select(statement =>
        {
            using var shadow = CosmosRenderingShadow.Arm(null);
            using var cost = CosmosRenderingCost.Measure(out var samples);
            return (statement.Sql, PlanDigests.Lines([statement]).Single(), samples.Sum(s => s.Cost.Bundles));
        })];

        /// <summary>
        /// Writes every digest to <c>TestResults/rendering/plan-digests.tsv</c>, or to the file
        /// <c>COSMOS_PLAN_DIGESTS</c> names: the partition run's first 200 predicates, or its 1,000 where
        /// <c>COSMOS_PARTITION_GATE=1</c>.
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
        /// remembered answer; and a statement with no filter opens none in either.
        /// </summary>
        [Fact]
        public void RepeatsOpenNoBundle()
        {
            var (first, again) = (Twice.Value[0], Twice.Value[1]);
            var unfiltered = first.Where(s => Regex.IsMatch(s.Sql, @"\b(WHERE|HAVING|ON)\b", RegexOptions.IgnoreCase) == false).Select(s => s.Sql).ToHashSet();
            TestContext.Current.TestOutputHelper?.WriteLine($"{first.Length} statements: {first.Sum(s => s.Bundles)} bundles first, {again.Sum(s => s.Bundles)} again; {unfiltered.Count} with no filter");
            again.Where(s => s.Bundles > 0).Select(s => $"{s.Bundles}: {s.Sql}").Should().BeEmpty("a plan again asks S only what it remembers");
            first.Concat(again).Where(s => unfiltered.Contains(s.Sql) && s.Bundles > 0).Select(s => s.Sql).Should().BeEmpty("a statement with no filter asks S nothing");
            first.Sum(s => s.Bundles).Should().BePositive("S was asked");
        }

    }

}
