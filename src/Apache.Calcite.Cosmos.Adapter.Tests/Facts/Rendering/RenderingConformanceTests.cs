using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// C6 (spec g15; line 4): every form S chose for a conjunct under the set the endpoint plans under (O1) over the pull
    /// request's run — <c>Emulator</c> on an emulator, <c>Production</c> on an account, which only the probe account's run
    /// asks (O6) — printed as production prints it, run as <c>WHERE</c>, <c>WHERE NOT</c> and projected, over one document
    /// per cell its bundle cut each of its paths into, and kept as <see cref="CosmosEval"/> says of that target.
    /// Service-gated, in a database of its own; <c>conformance.tsv</c> holds each form and what it was run over. A form
    /// over a path the service or the fixture sets itself — <c>id</c>, the partition key, <c>_ts</c> and the other system
    /// properties — cannot be run over the document drawn, and is listed apart; one whose documents the SDK refuses must
    /// hold a lone surrogate, which no service holds, and is listed too.
    /// </summary>
    public sealed class RenderingConformanceTests(RenderingConformanceTests.Fixture service) : IClassFixture<RenderingConformanceTests.Fixture>
    {

        /// <summary>What a document written holds whatever was drawn for it: its id, the fixture's partition key, and the system's properties.</summary>
        static readonly string[] Set = ["$[\"id\"]", "$[\"pk\"]", "$[\"_ts\"]", "$[\"_etag\"]", "$[\"_rid\"]", "$[\"_self\"]", "$[\"_attachments\"]"];

        [Fact]
        public async Task EveryPrintedFormIsKeptAsCosmosEvalSays()
        {
            if (service.Container is null)
                Assert.Skip("This needs a service. " + service.Unavailable);
            var target = CosmosEvalServiceTests.Target;
            var set = target == RenderingTarget.Emulator ? RenderingSet.Emulator : RenderingSet.Production;

            // The forms a conjunct's best was, each with the bundle that proved it: printed, they are what implementation sends.
            var generated = RenderingCheckRun.PerPullRequest.Value.Generated!;
            var forms = generated.Bundles.Where(b => b.Set == set && b.Cells is not null)
                .SelectMany(b => b.Roots.Where(r => r is not SqlPredicate.And && b.Answers[r.Digest] is { Kind: RenderingVerdictKind.Proven, Best: not null })
                    .Select(r => (Form: b.Answers[r.Digest].Best!, Bundle: b)))
                .DistinctBy(f => f.Form.Text).OrderBy(f => f.Form.Text, StringComparer.Ordinal).ToList();
            // Listed in the report, each with why it was not run, so that g16's reader sees every form line 4 leaves out.
            var (wrong, rows, unwritten, documents) = (new List<string>(), new List<object?[]>(), new List<string>(), 0);
            foreach (var (form, _) in forms.Where(f => f.Form.Subforms.Any(s => s.Path is { } p && Set.Contains(p))))
                rows.Add([form.Text, null, null, "not run: a path the service or the fixture sets", null]);
            var apart = rows.Count;
            forms = [.. forms.Where(f => f.Form.Subforms.All(s => s.Path is not { } p || Set.Contains(p) == false))];

            foreach (var ((form, bundle), n) in forms.Select((f, n) => (f, n)))
            {
                var paths = form.Subforms.Select(f => f.Path).OfType<string>().Distinct().ToList();
                var parameters = new CosmosParameterList();
                var text = RenderingPrinter.Print(form, paths.ToDictionary(p => p, CosmosEval.PathOf), "c", parameters);

                // One document per cell of each path, the form's other paths at their first cell's value.
                var cells = paths.ToDictionary(p => p, p => bundle.Cells!.Where(c => c.Path == p).Select(c => c.Witness).Distinct().ToList());
                var drawn = paths.SelectMany(p => cells[p].Select(w => paths.Select(q => q == p ? w : cells[q].FirstOrDefault()).ToList()))
                    .Select(values => RenderingDocumentSampler.Document(paths, [.. values.Select(PathCellTests.Decode)])).DistinctBy(Documents.Json).ToList();

                Dictionary<string, J.Obj> written;
                try
                {
                    written = await service.Write($"c6-{n}", drawn.Select(Documents.Json));
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Microsoft.Azure.Cosmos.CosmosException)
                {
                    // A lone surrogate the SDK refuses to send is nothing the service could hold; anything else is a fault here.
                    rows.Add([form.Text, text, null, "not written: " + e.GetType().Name, null]);
                    if (drawn.Any(LoneSurrogate) == false)
                        wrong.Add($"{text}: not written, and no document drawn holds a lone surrogate: {e.Message.Split('\n')[0]}");
                    unwritten.Add(text);
                    continue;
                }

                documents += written.Count;
                foreach (var (id, observed) in await service.Observe($"c6-{n}", text, parameters.Parameters))
                {
                    var said = CosmosEval.On(form, written[id], target);
                    rows.Add([form.Text, text, Documents.Json(written[id]), observed, said]);
                    if (said != observed)
                        wrong.Add($"{text} over {Documents.Json(written[id])}: the service {observed}, CosmosEval {said?.ToString() ?? "nothing"}");
                }
            }

            RenderingReports.Tsv(RenderingReports.Folder(), "conformance.tsv", ["form", "printed", "document", "service", "cosmos eval"], rows);
            TestContext.Current.TestOutputHelper?.WriteLine($"{forms.Count} forms printed under {set}, over {documents} documents ({unwritten.Count} forms not written, {apart} over a path the service sets listed apart), "
                + $"{service.Charged:F2} RU charged; {wrong.Count} divergences");
            forms.Should().NotBeEmpty();
            wrong.Should().BeEmpty();
        }

        /// <summary>Whether a document holds a string with a lone surrogate anywhere: what the SDK will not send.</summary>
        static bool LoneSurrogate(J value) => value switch
        {
            J.Str s => s.Value.Where((c, i) => char.IsHighSurrogate(c) && (i + 1 == s.Value.Length || char.IsLowSurrogate(s.Value[i + 1]) == false)
                || char.IsLowSurrogate(c) && (i == 0 || char.IsHighSurrogate(s.Value[i - 1]) == false)).Any(),
            J.Obj o => o.Properties.Keys.Any(k => LoneSurrogate(new J.Str(k))) || o.Properties.Values.Any(LoneSurrogate),
            J.Arr a => a.Elements.Any(LoneSurrogate),
            _ => false,
        };

        /// <summary>A database of C6's own.</summary>
        public sealed class Fixture() : CosmosEvalServiceTests.Fixture("conformance");

    }

}
