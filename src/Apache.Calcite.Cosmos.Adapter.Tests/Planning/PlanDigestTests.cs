using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Facts;

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
        /// Every digest is the same with statements rooted at their declaration's <c>D0</c> as without: the switch off, then
        /// on, each in generations of its own, so that neither arm reads what the other compiled or remembered.
        /// </summary>
        /// <remarks>
        /// Run before a pull request that touches <c>D0</c>, with <c>-explicit only</c>, and at the gate with
        /// <c>COSMOS_PARTITION_GATE=1</c>, which plans the partition run's 1,000 predicates rather than 200.
        /// </remarks>
        [Fact(Explicit = true)]
        public void TheDeclarationsPossibleRowsChangeNoPlan()
        {
            var off = PlanDigests.All(CosmosDifferentialTests.PartitionedPredicates, new JsonFactGenerations()).ToList();
            var armed = new JsonFactGenerations();
            List<string> on;
            using (StatementTerms.ArmDeclaredPossible())
                on = PlanDigests.All(CosmosDifferentialTests.PartitionedPredicates, armed).ToList();

            // The switch reached the planner: the statements it remembers over the declared container lie beneath D0.
            var declared = armed.Bind(CosmosDifferentialTests.Containers.Single(c => c.Name == "declared").Facts).Declaration()!;
            declared.DeclaredPossible()!.Children.Should().NotBeEmpty();

            var differing = off.Zip(on).Where(p => p.First != p.Second).Select(p => "off: " + p.First + "\non:  " + p.Second).ToList();
            TestContext.Current.TestOutputHelper?.WriteLine($"{off.Count} statements, {differing.Count} differing; sha256 {Hash(off)} off, {Hash(on)} on");
            on.Count.Should().Be(off.Count);
            differing.Should().BeEmpty();
        }

        static string Hash(IEnumerable<string> lines) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"))).ToLowerInvariant();

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
