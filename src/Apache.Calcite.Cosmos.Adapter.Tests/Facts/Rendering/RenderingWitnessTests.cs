using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosTranslatorFault;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The emulator witnesses (spec §5.5): one document per cell, for every refuting cell of the tip's refutations, the
    /// recall's, the mutations' and the gate faults G2 and G3's, and every cell the gains preview's agreements were proved
    /// over, written to a database of their own. The captured text runs as a <c>WHERE</c>, under <c>NOT</c> and projected,
    /// and Calcite runs the statement in process over the same documents: a refuting cell's document must keep apart on the
    /// two, an agreed one alike, and the service's outcome must be what <see cref="CosmosEval"/> says. The tip's
    /// refutations are triaged by theirs.
    /// </summary>
    public sealed class RenderingWitnessTests : IClassFixture<RenderingWitnessTests.Fixture>
    {

        readonly Fixture _service;

        public RenderingWitnessTests(Fixture service) => _service = service;

        [Fact]
        public async Task EveryWitnessKeepsAsItsVerdictSays()
        {
            if (_service.Container is null)
                Assert.Skip("This needs a service. " + _service.Unavailable);

            var run = RenderingCheckRun.PerPullRequest.Value;
            var cases = new List<Case>();
            foreach (var (check, refutation, _) in RenderingReports.Refutations(run))
                cases.Add(new($"tip {check.Input} {check.Name}#{check.Unit}", RenderingInputs.All(0).First(i => i.Input == check.Input && i.Name == check.Name).Sql,
                    check.Claimed!, check.Bound!, check.Entry!, [Document(check.Entry!.What!.Witness!)], true));

            foreach (var (p, c) in RenderingRecallTests.Cases)
                cases.Add(Refuted("recall " + p, c.Sql, RenderingRecallTests.Where(c.Texts), c.Parameters, RenderingRecallTests.Replay(c.Sql, c.Texts, c.Parameters)));

            foreach (var fault in Enumerable.Range(1, 10).Select(i => Enum.Parse<CosmosTranslatorFault>("M" + i)))
                foreach (var check in RenderingMutationTests.Checked(fault, [.. RenderingMutationTests.Triggers[fault].Select(t => t.Sql)]).Conjuncts
                    .Where(c => c.Entry?.Asserted.Kind == RenderingVerdictKind.Refuted).DistinctBy(c => (c.Name, c.Unit)).OrderBy(c => int.Parse(c.Name, CultureInfo.InvariantCulture)).ThenBy(c => c.Unit))
                    cases.Add(Refuted($"{fault} {check.Name}#{check.Unit}", RenderingMutationTests.Triggers[fault][int.Parse(check.Name, CultureInfo.InvariantCulture)].Sql, check.Claimed!, check.Bound!, check.Entry!));

            // G2's form, which G3's mutated leaf agrees with: the null its refutation names keeps apart on the service.
            cases.Add(Refuted("G2 and G3", RenderingGateFaultTests.NotBikes, RenderingGateFaultTests.Unguarded, [RenderingRecallTests.Bikes],
                RenderingRecallTests.Replay(RenderingGateFaultTests.NotBikes, [RenderingGateFaultTests.Unguarded], [RenderingRecallTests.Bikes], M8 | M9)));

            foreach (var (sql, gain) in RenderingGainsPreviewTests.Cases.Where(g => g.Reads == "Exact").Select(g => (g.Sql, g.Gain)))
            {
                var entry = RenderingRecallTests.Replay(sql, [gain], [RenderingRecallTests.Bikes], M8 | M9);
                cases.Add(new("gain " + gain, sql, gain, [RenderingRecallTests.Bikes], entry, [.. Agreed(entry).Select(Document)], false));
            }

            var (rows, wrong, witnessed) = (new List<object?[]>(), new List<string>(), new Dictionary<int, string>());
            foreach (var (c, n) in cases.Select((c, n) => (c, n)))
            {
                var partition = "w" + n.ToString(CultureInfo.InvariantCulture);
                var documents = await _service.Write(partition, c.Documents.Select(Saturation.Documents.Json));
                var table = Regex.Match(c.Sql, "FROM (\\w+) AS").Groups[1].Value;
                var kept = (await RenderingRecallTests.Fixture.Calcite(c.Sql, (CosmosDifferentialTests.Containers.Single(m => m.Name == table), _service.Container))).ToHashSet();
                foreach (var (id, observed) in await _service.Observe(partition, c.Text, c.Parameters))
                {
                    var said = CosmosEval.On(c.Entry.What!.Form, documents[id], RenderingTarget.Emulator);
                    var apart = kept.Contains(id) != (observed == ServiceOutcomes.True);
                    var judged = said != observed ? "CosmosEval differs" : Predicted(c, kept.Contains(id), observed) == false ? "outside S's prediction"
                        : apart == c.Refutes ? (c.Refutes ? "reproduced" : "agreed") : c.Refutes ? "not reproduced" : "a witness against a proof";
                    rows.Add([c.Name, Saturation.Documents.Json(documents[id]), c.Refutes ? "Refuted" : "Exact", kept.Contains(id), observed, said, judged]);
                    if (c.Name.StartsWith("tip ", StringComparison.Ordinal))
                        witnessed[c.Entry.Id] = judged;
                    if (judged is not ("reproduced" or "agreed"))
                        wrong.Add($"{c.Name}: {c.Text} over {Saturation.Documents.Json(documents[id])}: {judged} (Calcite keeps {kept.Contains(id)}, the emulator {observed}, CosmosEval {said})");
                }
            }

            RenderingReports.Tsv(RenderingReports.Write(run, witnessed), "witnesses.tsv", ["case", "document", "verdict", "calcite", "emulator", "cosmoseval", "judged"], rows);
            TestContext.Current.TestOutputHelper?.WriteLine($"{cases.Count} cases, {rows.Count} documents, on {CosmosEmulator.Endpoint}");
            wrong.Should().BeEmpty();
        }

        /// <summary>
        /// Whether what the service and Calcite did with a refuting document lies in the class the refutation carries, the
        /// form's at the root on that document: the service's outcome among its outcomes, and Calcite's keeping among its
        /// truths. An agreed document's prediction is its verdict's, that the two keep alike.
        /// </summary>
        static bool Predicted(Case c, bool kept, ServiceOutcomes observed) => c.Refutes == false || c.Entry.Asserted.Refutation?.Class is { } k
            && (k.Service & observed) != 0 && (k.Calcite & (kept ? Truths.True : ~Truths.True)) != 0;

        /// <summary>A refuted check's case: the document its refuting cells put at their paths.</summary>
        static Case Refuted(string name, string sql, string text, IReadOnlyList<CosmosParameter> parameters, RenderingMemo.Entry entry)
        {
            entry.Asserted.Kind.Should().Be(RenderingVerdictKind.Refuted, name);
            return new(name, sql, text, parameters, entry, [Document(entry.What!.Witness!)], true);
        }

        /// <summary>The cells a one-atom claim was proved over, each the value it puts at the atom's path.</summary>
        static IEnumerable<IReadOnlyDictionary<string, string?>> Agreed(RenderingMemo.Entry entry)
        {
            var claim = entry.What!;
            using var bundle = ProbeRows.ProbeTheory.Open(RenderingCheckRun.AssertedSet);
            var root = bundle.Write(claim.Conjuncts.Single());
            RenderingCheckRun.Restate(bundle, claim);

            bundle.Claim(root, claim.Form);
            bundle.Run();
            bundle.Read(root, claim.Form, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Proven);
            var outcomes = (CellOutcomes)bundle.Scope.Rows(ProbeRows.ProbeTheory.Outcomes).Single(r => r.Keys[1].Equals(claim.Form)).Output!;
            return [.. outcomes.Cells.Select(at => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { [outcomes.Path] = at.Cell.Witness() })];
        }

        /// <summary>A document holding each value at its path: a scalar as written, a structure empty, nothing where none.</summary>
        static J.Obj Document(IReadOnlyDictionary<string, string?> cells) => RenderingDocumentSampler.Document([.. cells.Keys], [.. cells.Values.Select(PathCellTests.Decode)]);

        /// <summary>A witness case: the statement, the text it sent and its values, the check, the documents, and whether they refute it.</summary>
        sealed record Case(string Name, string Sql, string Text, IReadOnlyList<CosmosParameter> Parameters, RenderingMemo.Entry Entry, IReadOnlyList<J.Obj> Documents, bool Refutes);

        /// <summary>A database of the witnesses' own.</summary>
        public sealed class Fixture() : CosmosEvalServiceTests.Fixture("witness");

    }

}
