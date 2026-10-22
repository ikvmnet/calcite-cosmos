using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

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
                cases.Add(Refuted("recall " + p, c.Sql, c.Texts.Length == 1 ? c.Texts[0] : "(" + string.Join(" AND ", c.Texts) + ")", c.Parameters, RenderingRecallTests.Replay(c.Sql, c.Texts, c.Parameters)));

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

            var rows = new List<string> { "case\tdocument\tverdict\tcalcite\temulator\tcosmoseval\tjudged" };
            var wrong = new List<string>();
            foreach (var (c, n) in cases.Select((c, n) => (c, n)))
            {
                var partition = "w" + n.ToString(CultureInfo.InvariantCulture);
                var documents = await _service.Write(partition, c.Documents.Select(Saturation.Documents.Json));
                var table = Regex.Match(c.Sql, "FROM (\\w+) AS").Groups[1].Value;
                var kept = (await CosmosDifferentialTests.Run(c.Sql, false, null, [(CosmosDifferentialTests.Containers.Single(m => m.Name == table), _service.Container)], null))
                    .Select(r => Convert.ToString(r is object[] a ? a[0] : r, CultureInfo.InvariantCulture)!).ToHashSet();
                foreach (var (id, observed) in await _service.Observe(partition, c.Text, c.Parameters))
                {
                    var said = CosmosEval.On(c.Entry.What!.Form, documents[id], RenderingTarget.Emulator);
                    var apart = kept.Contains(id) != (observed == ServiceOutcomes.True);
                    var judged = said != observed ? "CosmosEval differs" : apart == c.Refutes ? (c.Refutes ? "reproduced" : "agreed") : c.Refutes ? "not reproduced" : "a witness against a proof";
                    rows.Add(string.Join("\t", c.Name, Saturation.Documents.Json(documents[id]), c.Refutes ? "Refuted" : "Exact", kept.Contains(id), observed, said, judged));
                    if (judged is not ("reproduced" or "agreed"))
                        wrong.Add($"{c.Name}: {c.Text} over {Saturation.Documents.Json(documents[id])}: {judged} (Calcite keeps {kept.Contains(id)}, the emulator {observed}, CosmosEval {said})");
                }
            }

            var folder = Path.GetFullPath(Path.Combine("TestResults", "rendering"));
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "witnesses.tsv"), rows);
            RenderingReports.Write(run, cases.Where(c => c.Name.StartsWith("tip ", StringComparison.Ordinal)).ToDictionary(c => c.Entry.Id, c => rows.First(r => r.StartsWith(c.Name + "\t", StringComparison.Ordinal)).Split('\t')[^1]));
            TestContext.Current.TestOutputHelper?.WriteLine($"{cases.Count} cases, {rows.Count - 1} documents, on {CosmosEmulator.Endpoint}");
            wrong.Should().BeEmpty();
        }

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
            foreach (var (path, import) in claim.Imports)
                bundle.Import(ProbeRows.Source, CosmosEval.PathOf(path), import.Values, import.Chars, import.Assumed);

            bundle.Claim(root, claim.Form);
            bundle.Run();
            bundle.Read(root, claim.Form, Verdict.Equal).Kind.Should().Be(RenderingVerdictKind.Proven);
            var outcomes = (CellOutcomes)bundle.Scope.Rows(ProbeRows.ProbeTheory.Outcomes).Single(r => r.Keys[1].Equals(claim.Form)).Output!;
            return [.. outcomes.Cells.Select(at => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { [outcomes.Path] = at.Cell.Witness() })];
        }

        /// <summary>A document holding each value at its path: a scalar as written, a structure empty, nothing where none.</summary>
        static J.Obj Document(IReadOnlyDictionary<string, string?> cells) => RenderingDocumentSampler.Document([.. cells.Keys], [.. cells.Values.Select(v => v switch
        {
            null => null,
            "object|" => new J.Obj(new(StringComparer.Ordinal)),
            "array|" => new J.Arr([]),
            _ when v == NullValue => new J.Null(),
            _ when TypeOf(v) == BooleanType => new J.Bool(v == Of(true)),
            _ when IsString(v) => new J.Str(StringOf(v)),
            _ => IsExactNumber(v) ? new J.Num(NumberOf(v)) : (J?)new J.Huge(ApproximateOf(v)!.Value),
        })]);

        /// <summary>A witness case: the statement, the text it sent and its values, the check, the documents, and whether they refute it.</summary>
        sealed record Case(string Name, string Sql, string Text, IReadOnlyList<CosmosParameter> Parameters, RenderingMemo.Entry Entry, IReadOnlyList<J.Obj> Documents, bool Refutes);

        /// <summary>A database of the witnesses' own.</summary>
        public sealed class Fixture() : CosmosEvalServiceTests.Fixture("witness");

    }

}
