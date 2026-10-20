using System;
using System.IO;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;

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
        /// memory changes no later plan, or a digest compared across two commits would compare the order they ran in.
        /// </summary>
        [Fact]
        public void TheDigestsAreDeterministic()
        {
            var first = PlanDigests.Corpus().ToList();
            PlanDigests.Corpus().Should().Equal(first);
        }

    }

}
