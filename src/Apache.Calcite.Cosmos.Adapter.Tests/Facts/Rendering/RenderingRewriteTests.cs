using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The declared-fact rewrite against S (spec g05; line 33): the rewrite lowers UUID and instant comparisons to string
    /// comparisons from declared stored forms before any statement is built, so S proves forms for the lowered conjunct;
    /// that form must never be refuted for the conjunct it was lowered from.
    /// </summary>
    public sealed class RenderingRewriteTests
    {

        /// <summary>
        /// For each conjunct the rewrite changed in the pull request's run, the original beside the rewritten in one shadow
        /// bundle under each set: the rewritten conjunct's best, claimed for the original, is never refuted. An original the
        /// loader reads as opaque is never refuted, and is counted.
        /// </summary>
        [Fact]
        public void ARewrittenConjunctIsNeverProvenWhereItsOriginalIsRefuted()
        {
            var rewrites = RenderingCheckRun.PerPullRequest.Value.Shadow!.Rewrites.ToList();
            foreach (var set in RenderingGenerateRun.Sets)
            {
                var rows = rewrites.Where(r => r.Set == set).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}: {rows.Count} conjuncts rewritten, {rows.Count(r => r.Made is null)} unpaired, "
                    + $"{rows.Count(r => r.Original.Subterms.Any(t => t is Apache.Calcite.Cosmos.Facts.SqlPredicate.Opaque) == false)} originals the loader reads, "
                    + $"{rows.Count(r => r.Answer?.Best is not null)} rewritten proven; the original claimed: "
                    + string.Join(", ", rows.Where(r => r.Claimed is not null).GroupBy(r => r.Claimed!.Kind).Select(g => $"{g.Key} {g.Count()}")));
            }

            foreach (var r in rewrites.Where(r => r.Set == RenderingGenerateRun.AssertedSets[0]).Take(20))
                TestContext.Current.TestOutputHelper?.WriteLine($"  {r.Input} {r.Name}/{r.Statement}: {r.Original} became {r.Made}, {r.Answer?.Best?.Text ?? r.Answer?.Kind.ToString() ?? "unpaired"}, {r.Claimed?.Kind}");

            rewrites.Should().NotBeEmpty("the inputs hold lowered comparisons");
            rewrites.Where(r => r.Claimed?.Kind == RenderingVerdictKind.Refuted)
                .Select(r => $"{r.Set} {r.Input} {r.Name}/{r.Statement}: {r.Answer!.Best!.Text} for {r.Made}, refuted for {r.Original} at {r.Claimed!.Refutation}").Should().BeEmpty();
        }

    }

}
