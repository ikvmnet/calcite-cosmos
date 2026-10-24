using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The declared-fact rewrite against S (spec g05; line 33): the rewrite lowers UUID and instant comparisons to string
    /// comparisons from declared stored forms before any statement is built, so S proves forms for the lowered conjunct;
    /// the form S would send must keep the rows the original keeps, as Calcite evaluates the original itself, in process,
    /// so an original the loader reads as opaque, a UUID cast among them, is judged too.
    /// </summary>
    public sealed class RenderingRewriteTests
    {

        /// <summary>
        /// For each conjunct the rewrite changed in the pull request's run, under each set, S's best for what it became is
        /// held to the original on source A's documents, drawn from its bundle's import beside the original's literals: at
        /// <c>Pos</c>, the service's TRUE exactly where Calcite's is, and never a failure. Each lowering S proves is judged.
        /// </summary>
        [Fact]
        public void ARewrittenConjunctsBestKeepsWhatItsOriginalKeeps()
        {
            var rewrites = RenderingCheckRun.PerPullRequest.Value.Shadow!.Rewrites.ToList();
            var (judged, witnesses) = (0, new List<string>());
            foreach (var set in RenderingGenerateRun.Sets)
            {
                var rows = rewrites.Where(r => r.Set == set).ToList();
                var proven = rows.Where(r => r.Answer?.Best is not null).Select(r => (Row: r, Judged: Judge(r, r.Answer!.Best!))).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{set}: {rows.Count} conjuncts rewritten, {rows.Count(r => r.Made is null)} unpaired, {proven.Count} proven, "
                    + $"{proven.Count(p => p.Judged.Judged > 0)} judged over {proven.Sum(p => p.Judged.Judged)} documents");
                foreach (var (row, (count, found)) in proven)
                    TestContext.Current.TestOutputHelper?.WriteLine($"  {row.Input} {row.Name}/{row.Statement}: {row.Original} became {row.Made}, {row.Answer!.Best!.Text}: {count} documents");

                (judged, witnesses) = (judged + proven.Sum(p => p.Judged.Judged), [.. witnesses, .. proven.SelectMany(p => p.Judged.Witnesses)]);
            }

            rewrites.Should().NotBeEmpty("the inputs hold lowered comparisons");
            judged.Should().BePositive("a lowering S proves is judged against its original");
            witnesses.Should().BeEmpty();
        }

        /// <summary>
        /// The line fails: a lowering to the stored form in the wrong case, here S's best with its string upper-cased, keeps
        /// none of the documents holding the stored form, which Calcite's original keeps.
        /// </summary>
        [Fact]
        public void AWrongLoweringIsFound()
        {
            var row = RenderingCheckRun.PerPullRequest.Value.Shadow!.Rewrites.First(r => r.Answer?.Best is { Kind: ServiceFormKind.Compare, Literal: { } l }
                && JsonValues.IsString(l) && JsonValues.StringOf(l) != JsonValues.StringOf(l).ToUpperInvariant());
            var best = row.Answer!.Best!;
            var wrong = ServiceForm.CompareOf(best.Test, best.Path!, JsonValues.Of(JsonValues.StringOf(best.Literal!).ToUpperInvariant()));
            Judge(row, wrong).Witnesses.Should().NotBeEmpty($"{wrong.Text} for {row.Original}");
        }

        /// <summary>
        /// A form held to a rewritten conjunct's original, compiled by Calcite through its binding: the documents judged, and
        /// each where the service's TRUE parts from Calcite's or the service fails. A document Calcite raises on, or that no
        /// row is, is not judged; an original Calcite cannot compile alone judges none.
        /// </summary>
        internal static (int Judged, List<string> Witnesses) Judge(RenderingGenerateRun.Rewrite rewrite, ServiceForm form)
        {
            var (node, (fields, readings)) = rewrite.Source;
            var (judged, witnesses) = (0, new List<string>());
            if (rewrite.Claim is null || CalciteInProcess.Compile(node, fields, readings) is not { } calcite)
                return (judged, witnesses);

            var beside = CalciteInProcess.Literals(node).Distinct().SelectMany(l => form.Paths.Select(p => ServiceForm.CompareOf(CosmosServiceTest.Equal, p, l))).ToList();
            var claim = rewrite.Claim with { Form = beside.Count == 0 ? form : ServiceForm.JunctionOf(CosmosServiceTest.And, [form, .. beside]) };
            foreach (var document in RenderingDocumentSampler.Sample(claim))
            {
                if (calcite(document) is not { } truth || truth == Truths.Raises)
                    continue;

                judged++;
                foreach (var target in ProbeRows.Measured.Serves[rewrite.Set])
                    if (CosmosEval.On(form, document, target) is { } service && (service == ServiceOutcomes.Fails || (service == ServiceOutcomes.True) != (truth == Truths.True)))
                        witnesses.Add($"{rewrite.Set} {rewrite.Input} {rewrite.Name}/{rewrite.Statement}: {form.Text} for {rewrite.Original} on {Documents.Json(document)}: Calcite {truth}, {target} {service}");
            }

            return (judged, witnesses);
        }

    }

}
