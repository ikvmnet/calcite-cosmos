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

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The emulator witnesses (spec §5.5, g06): one document per cell, for every refuting cell of the tip's refutations,
    /// the recall's and the gate faults G2 and G3's, each document showing S's plants of M1–M10 wrong, and every cell the
    /// gains' agreements were proved over, written to a database of their own. The text runs as a <c>WHERE</c>, under
    /// <c>NOT</c> and projected, and Calcite runs the statement in process over the same documents: a refuting document
    /// must keep apart on the two, an agreed one alike, and the service's outcome must be what <see cref="CosmosEval"/>
    /// says. The tip's refutations are triaged by theirs.
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
                    check.Claimed!, check.Bound!, check.Entry!.What!.Form, check.Entry.Asserted.Refutation?.Class, check.Entry.Id, [Document(check.Entry.What.Witness!)], true));

            foreach (var (p, c) in RenderingRecallTests.Cases)
                cases.Add(Refuted("recall " + p, c.Sql, RenderingRecallTests.Where(c.Texts), c.Parameters, RenderingRecallTests.Replay(c.Sql, c.Texts, c.Parameters)));

            foreach (var fault in Enumerable.Range(1, 10).Select(i => "M" + i))
                cases.AddRange(Planted(RenderingGenerateMutationTests.Run(fault)));

            // G2's form, which G3's mutated leaf agrees with: the null its refutation names keeps apart on the service.
            cases.Add(Refuted("G2 and G3", RenderingGateFaultTests.NotBikes, RenderingGateFaultTests.Unguarded, [RenderingRecallTests.Bikes],
                RenderingRecallTests.Replay(RenderingGateFaultTests.NotBikes, [RenderingGateFaultTests.Unguarded], [RenderingRecallTests.Bikes])));

            foreach (var (sql, gain) in RenderingGainsTests.ExactEverywhere)
            {
                var entry = RenderingRecallTests.Replay(sql, [gain], [RenderingRecallTests.Bikes]);
                cases.Add(new("gain " + gain, sql, gain, [RenderingRecallTests.Bikes], entry.What!.Form, null, null, [.. Agreed(entry).Select(Document)], false));
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
                    var said = CosmosEval.On(c.Form, documents[id], RenderingTarget.Emulator);
                    var apart = kept.Contains(id) != (observed == ServiceOutcomes.True);
                    var judged = said != observed ? "CosmosEval differs" : Predicted(c, kept.Contains(id), observed) == false ? "outside S's prediction"
                        : apart == c.Refutes ? (c.Refutes ? "reproduced" : "agreed") : c.Refutes ? "not reproduced" : "a witness against a proof";
                    rows.Add([c.Name, Saturation.Documents.Json(documents[id]), c.Refutes ? "Refuted" : "Exact", kept.Contains(id), observed, said, judged]);
                    if (c.Tip is int tip)
                        witnessed[tip] = judged;
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
        static bool Predicted(Case c, bool kept, ServiceOutcomes observed) => c.Refutes == false || c.Class is { } k
            && (k.Service & observed) != 0 && (k.Calcite & (kept ? Truths.True : ~Truths.True)) != 0;

        /// <summary>A refuted check's case: the document its refuting cells put at their paths.</summary>
        static Case Refuted(string name, string sql, string text, IReadOnlyList<CosmosParameter> parameters, RenderingMemo.Entry entry)
        {
            entry.Asserted.Kind.Should().Be(RenderingVerdictKind.Refuted, name);
            return new(name, sql, text, parameters, entry.What!.Form, entry.Asserted.Refutation!.Class, null, [Document(entry.What!.Witness!)], true);
        }

        /// <summary>
        /// A plant's cases, over each pinned trigger that is one conjunct of a statement selecting ids: an untrusted plant's
        /// form refuted at an atom beneath nothing but <c>NOT</c>s, read at the conjunct, with the document its refuting cells
        /// make; a trusted one's wrong form at the conjunct, with the least document source A found it on where the
        /// emulator's model parts from Calcite.
        /// </summary>
        static IEnumerable<Case> Planted(RenderingGenerateMutationTests.Planted planted)
        {
            var (plant, world) = (planted.Plant, new World(6));
            foreach (var (t, i) in planted.Triggers.Select((t, i) => (t, i)).Where(x => x.t.Caught is not null && Regex.IsMatch(x.t.Sql, "^SELECT [cp]\\.\"id\" FROM")))
            {
                // The statement planning read first is the WHERE's; the filters a split builds beside it come after.
                var roots = t.Bundles.SelectMany(b => b.Roots).Where(r => t.Classes.Any(c => c.Statement == 0 && c.Ir == r.Digest)).DistinctBy(r => r.Digest).ToList();
                if (roots.Count != 1)
                    continue;

                var name = $"{plant.Name} {i}";
                if (plant.Trusted == false)
                {
                    foreach (var r in t.Bundles.SelectMany(b => b.Refutations ?? []).Where(r => plant.Proposed.ContainsKey(r.Form)).OrderBy(r => r.Form.Text, StringComparer.Ordinal))
                        if (Negations(roots[0], r.Node) is int nots)
                        {
                            var (form, refutation) = (r.Form, r.Refutation);
                            for (var n = 0; n < nots; n++)
                                (form, refutation) = (ServiceForm.Strip(form), Refutation.Negated(refutation));

                            yield return new(name, t.Sql, Text(form), [], form, refutation.Class, null, [Document(r.Witness)], true);
                            break;
                        }

                    continue;
                }

                var found = t.Judged.Values.SelectMany(j => j.Found).Where(f => f.Position == RenderPosition.Pos && f.Ir.Digest == roots[0].Digest)
                    .Select(f => (f.Form, f.Document, Calcite: RenderingSoundnessTests.Tally.Calcite(f.Ir, f.Document, world), Service: CosmosEval.On(f.Form, f.Document, RenderingTarget.Emulator)))
                    .Where(f => f.Calcite is { Count: > 0 } && f.Service is { } service && f.Calcite.Contains(Truths.True) != (service == ServiceOutcomes.True))
                    .OrderBy(f => Documents.Json(f.Document), StringComparer.Ordinal).ThenBy(f => f.Form.Text, StringComparer.Ordinal).FirstOrDefault();
                if (found.Form is not null)
                    yield return new(name, t.Sql, Text(found.Form), [], found.Form, new CellClass(found.Calcite!.Aggregate(Truths.None, (a, b) => a | b), found.Service!.Value), null, [found.Document], true);
            }
        }

        /// <summary>How many <c>NOT</c>s a root holds above an atom with nothing else between; <c>null</c> where something else is.</summary>
        static int? Negations(SqlPredicate root, SqlPredicate atom) => root.Digest == atom.Digest ? 0 : root is SqlPredicate.Not n ? Negations(n.Operand, atom) + 1 : null;

        /// <summary>A form as Cosmos SQL over the alias <c>c</c>, its literals inlined.</summary>
        internal static string Text(ServiceForm form) => form.Kind switch
        {
            ServiceFormKind.Test => form.Text.Replace("$[", "c[", StringComparison.Ordinal),
            ServiceFormKind.Compare => $"({form.Path!.Replace("$[", "c[", StringComparison.Ordinal)} {Op(form.Test)} {Literal(form.Literal!)})",
            ServiceFormKind.Not => $"(NOT {Text(form.Members[0])})",
            ServiceFormKind.And or ServiceFormKind.Or => "(" + string.Join(form.Kind == ServiceFormKind.And ? " AND " : " OR ", form.Members.Select(Text)) + ")",
            ServiceFormKind.Constant => form.Literal == JsonValues.Of(true) ? "true" : "false",
            _ => throw new NotSupportedException(form.Text),
        };

        static string Op(CosmosServiceTest op) => op switch
        {
            CosmosServiceTest.Equal => "=",
            CosmosServiceTest.NotEqual => "!=",
            CosmosServiceTest.Less => "<",
            CosmosServiceTest.LessOrEqual => "<=",
            CosmosServiceTest.Greater => ">",
            _ => ">=",
        };

        static string Literal(string value) => JsonValues.TypeOf(value) switch
        {
            JsonValues.StringType => System.Text.Json.JsonSerializer.Serialize(JsonValues.StringOf(value)),
            JsonValues.BooleanType => value == JsonValues.Of(true) ? "true" : "false",
            JsonValues.NullType => "null",
            _ => JsonValues.NumberOf(value).ToString(CultureInfo.InvariantCulture),
        };

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

        /// <summary>
        /// A witness case: the statement, the text it sent and its values, the form that text is, the class a refuting
        /// document must fall in, the tip's entry it triages, the documents, and whether they refute it.
        /// </summary>
        sealed record Case(string Name, string Sql, string Text, IReadOnlyList<CosmosParameter> Parameters, ServiceForm Form, CellClass? Class, int? Tip, IReadOnlyList<J.Obj> Documents, bool Refutes);

        /// <summary>A database of the witnesses' own.</summary>
        public sealed class Fixture() : CosmosEvalServiceTests.Fixture("witness");

    }

}
