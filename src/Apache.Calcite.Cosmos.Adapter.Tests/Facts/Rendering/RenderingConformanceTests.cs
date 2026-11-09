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

            var forms = Forms(set, Subset);
            // Listed in the report, each with why it was not run, so that g16's reader sees every form line 4 leaves out.
            var (wrong, rows, unwritten, documents, unread) = (new List<string>(), new List<object?[]>(), new List<string>(), 0, 0);
            foreach (var (form, _) in forms.Where(f => f.Form.Subforms.Any(s => s.Path is { } p && Set.Contains(p))))
                rows.Add([form.Text, null, null, "not run: a path the service or the fixture sets", null]);
            var apart = rows.Count;
            forms = [.. forms.Where(f => f.Form.Subforms.All(s => s.Path is not { } p || Set.Contains(p) == false))];

            foreach (var ((form, bundle), n) in forms.Select((f, n) => (f, n)))
            {
                // A computed place (h10), which the one reading gives S since the switch (h15), is printed as its call over
                // its operands, as implementation prints it, the documents drawn for the paths it reads.
                var computed = Computed(bundle);
                var paths = form.Subforms.SelectMany(f => new[] { f.Path, f.Right }).OfType<string>()
                    .SelectMany(p => computed.TryGetValue(p, out var call) ? call.PathsRead.Select(SqlValue.Spell) : [p]).Distinct().ToList();
                var parameters = new CosmosParameterList();
                var text = RenderingPrinter.Print(form, paths.ToDictionary(p => p, CosmosEval.PathOf), "c", parameters, computed: computed);

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
                    // A computed place's value is the function rows' (CosmosEval.Place), which leave unread a cell no row
                    // keys: listed, and judged nowhere, as source A leaves it unmeasured.
                    var said = CosmosEval.On(form, written[id], target, computed);
                    rows.Add([form.Text, text, Documents.Json(written[id]), observed, said]);
                    if (said is null && form.Subforms.Any(f => f.Path is { } p && computed.ContainsKey(p) || f.Right is { } r && computed.ContainsKey(r)))
                        unread++;
                    else if (said != observed)
                        wrong.Add($"{text} over {Documents.Json(written[id])}: the service {observed}, CosmosEval {said?.ToString() ?? "nothing"}");
                }
            }

            RenderingReports.Tsv(RenderingReports.Folder(), "conformance.tsv", ["form", "printed", "document", "service", "cosmos eval"], rows);
            TestContext.Current.TestOutputHelper?.WriteLine($"{forms.Count} forms printed under {set}{(Subset ? " (the switch's subset)" : "")}, over {documents} documents ({unwritten.Count} forms not written, {apart} over a path the service sets listed apart), "
                + $"{forms.Count(f => Computed(f.Bundle).Count > 0 && f.Form.Subforms.Any(s => s.Path is { } p && Computed(f.Bundle).ContainsKey(p) || s.Right is { } r && Computed(f.Bundle).ContainsKey(r)))} over a computed place, "
                + $"{unread} answers no function row reads; {service.Charged:F2} RU charged; {wrong.Count} divergences");
            forms.Should().NotBeEmpty();
            wrong.Should().BeEmpty();
        }

        /// <summary>The computed places a bundle's roots read, each call by its spelling, which a form names it by.</summary>
        static Dictionary<string, SqlValue.Call> Computed(RenderingGenerateRun.Bundle bundle) =>
            bundle.Roots.SelectMany(r => r.Computed).Where(c => c.Spelled is not null).DistinctBy(c => c.Spelled).ToDictionary(c => c.Spelled!, StringComparer.Ordinal);

        /// <summary>
        /// Whether C6 runs the subset a probe-account run before a switch asks (spec §4.8), named by
        /// <c>COSMOS_CONFORMANCE_SUBSET=switch</c>, in place of every form of the pull request's run.
        /// </summary>
        internal static bool Subset => Environment.GetEnvironmentVariable("COSMOS_CONFORMANCE_SUBSET") == "switch";

        /// <summary>
        /// The forms a conjunct's best was under a set, each with the bundle that proved it: printed, they are what
        /// implementation sends. Every one of the pull request's run, or, for the subset, those of it that hold a string
        /// ordering, an affix, a type test or an element path, which production asks of S since the switch (h15).
        /// </summary>
        internal static List<(ServiceForm Form, RenderingGenerateRun.Bundle Bundle)> Forms(RenderingSet set, bool subset)
        {
            static bool Asked(ServiceForm f) => f.Kind is ServiceFormKind.Affix or ServiceFormKind.Test
                || f.Kind == ServiceFormKind.Compare && f.Test is not (CosmosServiceTest.Equal or CosmosServiceTest.NotEqual) && f.Literal is { } l && Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.IsString(l)
                || f.Path is { } p && p.Contains("][", StringComparison.Ordinal) && char.IsDigit(p[p.LastIndexOf('[') + 1]);

            var generated = RenderingCheckRun.PerPullRequest.Value.Generated!;
            return [.. generated.Bundles.Where(b => b.Set == set && b.Cells is not null)
                .SelectMany(b => b.Roots.Where(r => r is not SqlPredicate.And && b.Answers[r.Digest] is { Kind: RenderingVerdictKind.Proven, Best: not null })
                    .Select(r => (Form: b.Answers[r.Digest].Best!, Bundle: b)))
                .Where(f => subset == false || f.Form.Subforms.Any(Asked))
                .DistinctBy(f => f.Form.Text).OrderBy(f => f.Form.Text, StringComparer.Ordinal)];
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
