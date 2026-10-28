using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The pairwise check (spec g05; line 3; gates map §3.2): two forms the gate agreed exact for one node at one position
    /// keep alike on every document, so two rows that contradict each other across keys show as two forms parting on a
    /// cell both agree. Judged by <see cref="CosmosEval"/> alone, which needs no <see cref="IrEval"/> and so judges where
    /// it tosses a coin too.
    /// </summary>
    public sealed class RenderingPairwiseTests
    {

        /// <summary>
        /// Every two <c>Equal</c> agreements of a node at a position in each generate bundle of the pull request's run keep
        /// alike on every target the bundle's set serves, over source A's documents where the conjuncts its stratum assumes
        /// hold. A document Calcite raises on, where the Equal box tolerates any answer but a failure, is reported apart.
        /// </summary>
        [Fact]
        public void EveryTwoExactCandidatesAgree()
        {
            var compared = Compare(RenderingCheckRun.PerPullRequest.Value.Generated!);
            RenderingReports.WritePairwise(compared);
            TestContext.Current.TestOutputHelper?.WriteLine($"{compared.Pairs} pairs, {compared.Seen} documents and targets compared, "
                + $"{compared.Raised} where Calcite raises reported apart; {compared.Disagreements.Count} disagreements");
            compared.Pairs.Should().BePositive();
            compared.Disagreements.Should().BeEmpty();
        }

        /// <summary>
        /// The pairs of a run's generate bundles compared on source A's documents: each pair, documents and targets compared
        /// and those where Calcite raises, and each disagreement.
        /// </summary>
        internal static Compared Compare(RenderingGenerateRun generated)
        {
            var (rows, disagreements) = (new List<object?[]>(), new List<string>());
            var (pairs, compared, raised) = (0, 0, 0);
            foreach (var bundle in generated.Bundles.Where(b => b.Claim is not null))
            {
                var groups = bundle.Forms.Where(f => f.What == RenderingGenerateRun.Judged.Agreement && f.Claim == Verdict.Equal)
                    .GroupBy(f => (f.Node.Digest, f.Position)).Where(g => g.Count() > 1)
                    .SelectMany(g => g.SelectMany((a, i) => g.Skip(i + 1).Select(b => (a.Node, a.Position, A: a.Form, B: b.Form, Of: "candidates"))))
                    .ToList();
                if (groups.Count == 0)
                    continue;

                // Line 1's documents and stratum, read by the one definition both lines judge by.
                var (claim, world, targets) = (bundle.Claim!, new World(bundle.Id), ProbeRows.Measured.Serves[bundle.Set]);
                var documents = RenderingSoundnessTests.Draw(claim).Where(d => RenderingSoundnessTests.RuledOut(claim, d) == false).ToList();
                foreach (var (node, pos, a, b, of) in groups)
                {
                    pairs++;
                    var assumes = RenderingSoundnessTests.Assumes(claim, node);
                    var keeps = pos == RenderPosition.Pos ? ServiceOutcomes.True : ServiceOutcomes.False;
                    var (seen, apart, parted) = (0, 0, 0);
                    foreach (var document in documents.Where(d => RenderingSoundnessTests.Holds(claim, d, world, assumes)))
                    {
                        var raises = RenderingSoundnessTests.Tally.Calcite(node, document, world) is { Count: 0 };
                        foreach (var target in targets)
                        {
                            if (CosmosEval.On(a, document, target) is not { } x || CosmosEval.On(b, document, target) is not { } y)
                                continue;

                            if (raises)
                                apart++;
                            else
                                seen++;

                            if ((x == keeps) != (y == keeps) && raises == false)
                            {
                                parted++;
                                disagreements.Add($"{bundle.Set} bundle {bundle.Id}, {node} at {pos} ({of}): {a.Text} {x} and {b.Text} {y} on {target}, {Documents.Json(document)}");
                            }
                        }
                    }

                    (compared, raised) = (compared + seen, raised + apart);
                    rows.Add([bundle.Set, bundle.Id, node.Digest, pos, of, a.Text, b.Text, seen, apart, parted]);
                }
            }

            return new(pairs, compared, raised, disagreements, rows);
        }

        /// <summary>What <see cref="Compare"/> found: pairs, documents and targets compared, those where Calcite raises, each disagreement, a row a pair.</summary>
        internal sealed record Compared(int Pairs, int Seen, int Raised, List<string> Disagreements, List<object?[]> Rows);

    }

}
