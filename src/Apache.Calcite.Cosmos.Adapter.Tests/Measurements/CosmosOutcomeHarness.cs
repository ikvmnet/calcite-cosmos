using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json.Linq;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// Holds a registry row to the service: each outcome's test over the catalogue's witnesses
    /// (<see cref="CosmosOutcomeWitnesses"/>), and each cell's answer within what a target the outcome names declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A truth is read off three statements.</b> A witness <c>WHERE</c> keeps is true, one <c>WHERE NOT</c> keeps is
    /// false, one neither keeps is undefined; the projection, where it is a boolean or nothing, says the same, except at a
    /// cell some target declared off a <c>WHERE</c> alone or the targets answer apart. An order is the one comparator that
    /// sorts the witnesses as <c>ORDER BY</c> does and places them as <c>&gt;</c> beside each does. A literal is a
    /// parameter, as the translator sends one, and a range's two bounds are two.
    /// </para>
    /// <para>
    /// <b>It never asks which endpoint answered</b> (<c>TODO.md</c>, the measurements): an answer must mean nothing a
    /// declared target's answer does not, read through <see cref="AnswerMeanings"/>. Every cell is measured and appended to
    /// <c>TestResults/measurements/runs.tsv</c> — the endpoint, the row, the key, the cell, its answer and the targets it
    /// matched, owed cells' too — before any is asserted, which is what a run record is built from.
    /// </para>
    /// <para>
    /// The witnesses are written into the container given, under a partition of their own that every query is scoped to,
    /// and deleted after: other tests read these containers across partitions.
    /// </para>
    /// </remarks>
    internal static class CosmosOutcomeHarness
    {

        static readonly object Log = new();

        /// <summary>
        /// Holds each outcome of a row over its operand's literal, or over each of <paramref name="edges"/> in its place,
        /// every one with witnesses of its own (<see cref="CosmosOutcomeWitnesses.RoundingEdges"/>); skipped where there is
        /// no container.
        /// </summary>
        public static async Task Holds(Container? container, string id, string? unavailable = null, IReadOnlyList<string>? edges = null)
        {
            if (container is null)
                Assert.Skip("These need a service. " + unavailable);

            var axiom = CosmosAxioms.Get(id);
            var keys = axiom.Outcomes!.GroupBy(o => o.Formula ?? o.Test + "/" + o.Operand);
            IReadOnlyList<string?> literals = edges is null ? [null] : [.. edges];
            var failures = new List<string>();

            // Keys over one operand share its witnesses, each read at its own cells; an order is measured over its own.
            foreach (var shared in keys.GroupBy(k => k.First() is var o && o.Formula is null ? (o.Test == CosmosServiceTest.Order, o.Operand) : (false, CosmosValueClass.None)))
            {
                var (order, operand) = shared.Key;
                // A connective's operand is its first side's class, held at c.u, every witness of it beside every one of the second.
                var connective = shared.Any(k => k.First() is { Formula: null, Test: CosmosServiceTest.And or CosmosServiceTest.Or });
                var compares = shared.Any(k => k.First() is { Formula: null, Test: >= CosmosServiceTest.Equal and <= CosmosServiceTest.GreaterOrEqual or CosmosServiceTest.Range });
                var operands = connective ? CosmosOutcomeWitnesses.Of(CosmosValueClass.None, operand) : [null];
                var witnesses = (from edge in literals
                                 from cell in shared.SelectMany(k => CosmosOutcomeWitnesses.Cells(k.First(), edge)).Distinct()
                                 from json in CosmosOutcomeWitnesses.Of(connective ? CosmosValueClass.None : operand, cell, edge)
                                 from u in operands
                                 select (cell, json, u, Literal: compares ? edge ?? CosmosOutcomeWitnesses.Literal(operand) : null))
                                .Select((w, n) => new Witness("w" + n, w.cell, w.json, w.u, w.Literal)).ToList();

                var pk = Guid.NewGuid().ToString("N");
                foreach (var witness in witnesses)
                    await Write(container!, pk, witness);

                try
                {
                    foreach (var key in shared)
                    {
                        var own = witnesses.Where(w => literals.Any(edge => CosmosOutcomeWitnesses.Cells(key.First(), edge).Contains(w.Cell))).ToList();
                        var declared = Declared(axiom, key);
                        failures.AddRange(Check(axiom, key, declared, order ? await Order(container!, pk, own) : await Truths(container!, pk, key.First(), own, declared, failures)));
                    }
                }
                finally
                {
                    foreach (var witness in witnesses)
                        using (await container!.DeleteItemStreamAsync(witness.Id, new PartitionKey(pk))) { }
                }
            }

            failures.Should().BeEmpty($"{id} holds on the service");
        }

        // U is a connective's first operand, at c.u; Literal what a test compares with, a range's two bounds as an array.
        record Witness(string Id, CosmosValueClass Cell, string? Json, string? U, string? Literal);

        /// <summary>What each target answered at each cell of a key: an atom's as the reader writes S's rows, a composite's as written.</summary>
        static List<(CosmosValueClass Cell, RenderingTarget Target, CosmosAnswer Answer)> Declared(CosmosAxiom axiom, IGrouping<string, CosmosOutcome> key) =>
            key.First().Formula is null
                ? [.. from row in CosmosAxiomRows.Read([axiom with { Outcomes = [.. key] }]) where row.Target != RenderingTarget.Calcite select (row.Key.Cell, row.Target, row.Answer)]
                : [.. from outcome in key
                      from target in new[] { (CosmosAxiomTarget.Emulator, RenderingTarget.Emulator), (CosmosAxiomTarget.Azure, RenderingTarget.Azure) }
                      where outcome.On.HasFlag(target.Item1)
                      from cell in outcome.Cells
                      select (cell.Cell, target.Item2, cell.Answer)];

        static async Task<ILookup<CosmosValueClass, CosmosAnswer>> Truths(Container container, string pk, CosmosOutcome outcome, List<Witness> witnesses,
            List<(CosmosValueClass Cell, RenderingTarget Target, CosmosAnswer Answer)> declared, List<string> failures)
        {
            var answers = new List<(CosmosValueClass, CosmosAnswer)>();
            foreach (var batch in witnesses.GroupBy(w => (Test: outcome.Formula ?? Formula(outcome, w.Cell), w.Literal)))
            {
                var (test, literal) = batch.Key;
                var projected = (await Query(container, pk, $"SELECT c.id, ({test}) AS r FROM c", literal)).ToDictionary(row => (string)row["id"]!, row => ((JObject)row).GetValue("r"));
                var kept = (await Query(container, pk, $"SELECT VALUE c.id FROM c WHERE {test}", literal)).Select(id => (string)id!).ToHashSet();
                var negated = (await Query(container, pk, $"SELECT VALUE c.id FROM c WHERE NOT ({test})", literal)).Select(id => (string)id!).ToHashSet();

                foreach (var witness in batch)
                {
                    if (kept.Contains(witness.Id) && negated.Contains(witness.Id))
                        failures.Add($"{test} and its NOT both keep {witness.Json}");

                    var answer = kept.Contains(witness.Id) ? CosmosAnswer.True : negated.Contains(witness.Id) ? CosmosAnswer.False : CosmosAnswer.Undefined;

                    // A projection says what its filter keeps, except where a target was measured to keep otherwise than it
                    // projects (Service.RangeOverPartedBoundsIsEmpty): a cell declared off a WHERE alone may be one, and so may
                    // a cell the targets answer apart, as Azure's parted range projects true where its filter keeps nothing.
                    var r = projected[witness.Id];
                    var at = declared.Where(d => d.Cell == witness.Cell).Select(d => d.Answer).Distinct().ToList();
                    if ((r is null || r.Type == JTokenType.Boolean) && at.Contains(CosmosAnswer.NotKept) == false && at.Count < 2
                        && (r is null ? CosmosAnswer.Undefined : (bool)r ? CosmosAnswer.True : CosmosAnswer.False) is var shown && shown != answer)
                        failures.Add($"{test} projected over {witness.Json} is {shown}, where its filters read {answer}");

                    answers.Add((witness.Cell, answer));
                }
            }

            return answers.Distinct().ToLookup(a => a.Item1, a => a.Item2);
        }

        static async Task<ILookup<CosmosValueClass, CosmosAnswer>> Order(Container container, string pk, List<Witness> witnesses)
        {
            var ascending = (await Query(container, pk, "SELECT VALUE c.id FROM c ORDER BY c.v", null)).Select(id => (string)id!).ToList();
            var descending = (await Query(container, pk, "SELECT VALUE c.id FROM c ORDER BY c.v DESC", null)).Select(id => (string)id!).ToList();
            var values = witnesses.ToDictionary(w => w.Id, w => JToken.Parse(w.Json!));

            // What > keeps beside each witness sent as a literal; one holding U+0000 no comparator places.
            var above = new Dictionary<string, HashSet<string>>();
            foreach (var w in witnesses.Where(w => w.Json!.Contains("\\u0000") == false))
                above[w.Id] = (await Query(container, pk, "SELECT VALUE c.id FROM c WHERE c.v > @l", w.Json)).Select(id => (string)id!).ToHashSet();

            var strings = values.Values.All(v => v.Type == JTokenType.String);
            var comparators = new (CosmosAnswer Answer, bool Applies, Comparison<JToken> Compare)[]
            {
                (CosmosAnswer.CodePoint, strings, (a, b) => ServiceLeaf.Compare(CosmosAnswer.CodePoint, (string)a!, (string)b!)),
                (CosmosAnswer.StoredCodePoint, strings, (a, b) => ServiceLeaf.Compare(CosmosAnswer.StoredCodePoint, (string)a!, (string)b!)),
                (CosmosAnswer.Utf16, strings, (a, b) => ServiceLeaf.Compare(CosmosAnswer.Utf16, (string)a!, (string)b!)),
                (CosmosAnswer.Numeric, values.Values.All(v => v.Type is JTokenType.Integer or JTokenType.Float), (a, b) => a.Value<decimal>().CompareTo(b.Value<decimal>())),
            };

            var matching = comparators.Where(c => c.Applies && descending.SequenceEqual(Enumerable.Reverse(ascending))
                && values.Keys.OrderBy(id => values[id], Comparer<JToken>.Create(c.Compare)).SequenceEqual(ascending)
                && above.All(a => a.Value.SetEquals(values.Keys.Where(id => c.Compare(values[id], values[a.Key]) > 0)))).Select(c => c.Answer).ToList();
            return matching.ToLookup(_ => CosmosValueClass.None);
        }

        /// <summary>
        /// Records every answer observed, and answers what fails: a declared cell whose witnesses answer apart, or whose answer
        /// means something no declared target's does — a comparator, unless it is one.
        /// </summary>
        static IEnumerable<string> Check(CosmosAxiom axiom, IGrouping<string, CosmosOutcome> key, List<(CosmosValueClass Cell, RenderingTarget Target, CosmosAnswer Answer)> declared,
            ILookup<CosmosValueClass, CosmosAnswer> observed)
        {
            bool Within(CosmosAnswer seen, CosmosAnswer answer) => seen == answer
                || AnswerMeanings.Measured.Of(seen) is var meant and not ServiceOutcomes.None && (meant & ~AnswerMeanings.Measured.Of(answer)) == ServiceOutcomes.None;

            lock (Log)
            {
                var path = Path.GetFullPath(Path.Combine("TestResults", "measurements", "runs.tsv"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, from cell in observed from answer in cell select string.Join('\t', new Uri(CosmosEmulator.Endpoint!).Authority, axiom.Id, key.Key, cell.Key, answer,
                    string.Join('|', declared.Where(d => d.Cell == cell.Key && Within(answer, d.Answer)).Select(d => d.Target)) is { Length: > 0 } targets ? targets : "none"));
            }

            foreach (var cell in declared.Select(d => d.Cell).Distinct())
            {
                if (observed[cell].Count() != 1)
                    yield return $"{key.Key} at {cell} answers {string.Join(", ", observed[cell])}, not one answer";
                else if (declared.Any(d => d.Cell == cell && Within(observed[cell].Single(), d.Answer)) == false)
                    yield return $"{key.Key} at {cell} answers {observed[cell].Single()}, which no target it names did";
            }
        }

        static string Formula(CosmosOutcome outcome, CosmosValueClass cell) => outcome.Test switch
        {
            CosmosServiceTest.Bare => "c.v",
            CosmosServiceTest.Constant => outcome.Operand == CosmosValueClass.True ? "true" : "false",
            CosmosServiceTest.Not => "NOT " + CosmosOutcomeWitnesses.Side(cell, "c.v"),
            CosmosServiceTest.And or CosmosServiceTest.Or => $"({CosmosOutcomeWitnesses.Side(outcome.Operand, "c.u")} {outcome.Test.ToString().ToUpperInvariant()} {CosmosOutcomeWitnesses.Side(cell, "c.v")})",
            CosmosServiceTest.Range => "c.v >= @lo AND c.v <= @hi",
            CosmosServiceTest.Equal => "c.v = @l",
            CosmosServiceTest.NotEqual => "c.v != @l",
            CosmosServiceTest.Less => "c.v < @l",
            CosmosServiceTest.LessOrEqual => "c.v <= @l",
            CosmosServiceTest.Greater => "c.v > @l",
            CosmosServiceTest.GreaterOrEqual => "c.v >= @l",
            var test => ServiceForm.TestOf(test, "c.v").Text,
        };

        static async Task Write(Container container, string pk, Witness witness)
        {
            var json = $"{{\"id\":\"{witness.Id}\",\"pk\":\"{pk}\"" + (witness.Json is null ? "" : ",\"v\":" + witness.Json) + (witness.U is null ? "" : ",\"u\":" + witness.U) + "}";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            using var response = await container.CreateItemStreamAsync(stream, new PartitionKey(pk));
            response.EnsureSuccessStatusCode();
        }

        static async Task<List<JToken>> Query(Container container, string pk, string sql, string? literal)
        {
            var query = new QueryDefinition(sql);
            if (literal is not null)
                query = JToken.Parse(literal) is JArray bounds ? query.WithParameter("@lo", ((JValue)bounds[0]).Value).WithParameter("@hi", ((JValue)bounds[1]).Value)
                    : query.WithParameter("@l", ((JValue)JToken.Parse(literal)).Value);

            var rows = new List<JToken>();
            using var iterator = container.GetItemQueryIterator<JToken>(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(pk) });
            while (iterator.HasMoreResults)
                rows.AddRange(await iterator.ReadNextAsync());

            return rows;
        }

    }

}
