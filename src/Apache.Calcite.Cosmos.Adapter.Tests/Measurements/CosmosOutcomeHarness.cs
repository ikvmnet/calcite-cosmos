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

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// Holds a registry row to the service: each outcome's test over the catalogue's witnesses
    /// (<see cref="CosmosOutcomeWitnesses"/>), and each cell's answer one that a target the outcome names declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A truth is read off three statements.</b> A witness <c>WHERE</c> keeps is true, one <c>WHERE NOT</c> keeps is
    /// false, one neither keeps is undefined; the projection, where it is a boolean or nothing, says the same, and a value
    /// that is no boolean is read by the two filters alone. An order is the one comparator that sorts the witnesses as
    /// <c>ORDER BY</c> does, <c>DESC</c> its exact reverse. A literal is a parameter, as the translator sends one, and a
    /// range's two bounds are two. A connective's first operand is held beside the second, at <c>c.u</c>, each of its
    /// witnesses beside each of the other's.
    /// </para>
    /// <para>
    /// <b>It never asks which endpoint answered</b> (<c>TODO.md</c>, the measurements): the answer must equal one a declared
    /// target gave. <c>TestResults/measurements/runs.tsv</c> takes the endpoint, the row, the key, each witnessed cell, its
    /// answer and the targets it matched, owed cells' too, which is what a run record is built from.
    /// </para>
    /// <para>
    /// The witnesses are written into the container given, under a partition of their own that every query is scoped to,
    /// and deleted after.
    /// </para>
    /// </remarks>
    internal static class CosmosOutcomeHarness
    {

        static readonly object Log = new();

        /// <summary>
        /// Holds each outcome of a row over its operand's literal, or over each of <paramref name="edges"/> in its place,
        /// every one with witnesses of its own (<see cref="CosmosOutcomeWitnesses.RoundingEdges"/>).
        /// </summary>
        public static async Task Holds(Container container, CosmosAxiom axiom, IReadOnlyList<string>? edges = null)
        {
            var keys = axiom.Outcomes!.GroupBy(o => o.Formula ?? o.Test + "/" + o.Operand);
            IReadOnlyList<string?> literals = edges is null ? [null] : [.. edges];

            // Keys over one operand share its witnesses, each read at its own cells; an order is measured over its own.
            foreach (var shared in keys.GroupBy(k => k.First() is var o && o.Formula is null ? (o.Test == CosmosServiceTest.Order, o.Operand) : (false, CosmosValueClass.None)))
            {
                var (order, operand) = shared.Key;
                // A connective's operand is its first side's class, held at c.u, and no literal.
                var connective = shared.Any(k => k.First() is { Formula: null, Test: CosmosServiceTest.And or CosmosServiceTest.Or });
                var operands = connective ? CosmosOutcomeWitnesses.Of(CosmosValueClass.None, operand) : [null];
                var witnesses = (from edge in literals
                                 from cell in shared.SelectMany(k => CosmosOutcomeWitnesses.Cells(k.First(), edge)).Distinct()
                                 from json in CosmosOutcomeWitnesses.Of(connective ? CosmosValueClass.None : operand, cell, edge)
                                 from u in operands
                                 select (cell, json, u, Literal: connective ? null : edge ?? CosmosOutcomeWitnesses.Literal(operand)))
                                .Select((w, n) => new Witness("w" + n, w.cell, w.json, w.u, w.Literal)).ToList();

                var pk = Guid.NewGuid().ToString("N");
                foreach (var witness in witnesses)
                    await Write(container, pk, witness);

                try
                {
                    foreach (var key in shared)
                    {
                        var own = witnesses.Where(w => literals.Any(edge => CosmosOutcomeWitnesses.Cells(key.First(), edge).Contains(w.Cell))).ToList();
                        Check(axiom, key, order ? await Order(container, pk, own) : await Truths(container, pk, key.First(), own));
                    }
                }
                finally
                {
                    foreach (var witness in witnesses)
                        using (await container.DeleteItemStreamAsync(witness.Id, new PartitionKey(pk))) { }
                }
            }
        }

        // U is a connective's first operand, at c.u; Literal what a test compares with, a range's two bounds as an array.
        record Witness(string Id, CosmosValueClass Cell, string? Json, string? U, string? Literal);

        static async Task<ILookup<CosmosValueClass, CosmosAnswer>> Truths(Container container, string pk, CosmosOutcome outcome, List<Witness> witnesses)
        {
            var answers = new List<(CosmosValueClass, CosmosAnswer)>();
            foreach (var batch in witnesses.GroupBy(w => (Test: outcome.Formula ?? Formula(outcome, w.Cell), Literal: outcome.Formula is null ? w.Literal : null)))
            {
                var (test, literal) = batch.Key;
                var projected = (await Query(container, pk, $"SELECT c.id, ({test}) AS r FROM c", literal)).ToDictionary(row => (string)row["id"]!, row => ((JObject)row).GetValue("r"));
                var kept = (await Query(container, pk, $"SELECT VALUE c.id FROM c WHERE {test}", literal)).Select(id => (string)id!).ToHashSet();
                var negated = (await Query(container, pk, $"SELECT VALUE c.id FROM c WHERE NOT ({test})", literal)).Select(id => (string)id!).ToHashSet();

                foreach (var witness in batch)
                {
                    (kept.Contains(witness.Id) && negated.Contains(witness.Id)).Should().BeFalse($"{test} and its NOT do not both keep {witness.Json}");
                    var answer = kept.Contains(witness.Id) ? CosmosAnswer.True : negated.Contains(witness.Id) ? CosmosAnswer.False : CosmosAnswer.Undefined;

                    // A range is the one shape whose projection is not what a filter keeps (Service.RangeOverPartedBoundsIsEmpty).
                    var r = projected[witness.Id];
                    if (outcome.Test != CosmosServiceTest.Range && (r is null || r.Type == JTokenType.Boolean))
                        (r is null ? CosmosAnswer.Undefined : (bool)r ? CosmosAnswer.True : CosmosAnswer.False).Should().Be(answer, $"{test} projected over {witness.Json}");

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

            var strings = values.Values.All(v => v.Type == JTokenType.String);
            var comparators = new (CosmosAnswer Answer, bool Applies, Comparison<JToken> Compare)[]
            {
                (CosmosAnswer.CodePoint, strings, (a, b) => ServiceOrderModel.CompareCodePoints((string)a!, (string)b!)),
                (CosmosAnswer.Utf16, strings, (a, b) => string.CompareOrdinal((string)a!, (string)b!)),
                (CosmosAnswer.Numeric, values.Values.All(v => v.Type is JTokenType.Integer or JTokenType.Float), (a, b) => a.Value<decimal>().CompareTo(b.Value<decimal>())),
            };

            var matching = comparators.Where(c => c.Applies && descending.SequenceEqual(Enumerable.Reverse(ascending))
                && values.Keys.OrderBy(id => values[id], Comparer<JToken>.Create(c.Compare)).SequenceEqual(ascending)).Select(c => c.Answer).ToList();
            matching.Should().ContainSingle($"one comparator orders the witnesses as the service does: {string.Join(" ", ascending)}");
            return matching.ToLookup(_ => CosmosValueClass.None);
        }

        /// <summary>Asserts each declared cell, its witnesses answering alike, and records every answer observed.</summary>
        static void Check(CosmosAxiom axiom, IGrouping<string, CosmosOutcome> key, ILookup<CosmosValueClass, CosmosAnswer> observed)
        {
            var declared = (from outcome in key
                            from cell in outcome.Cells
                            from target in new[] { CosmosAxiomTarget.Emulator, CosmosAxiomTarget.Azure }
                            where outcome.On.HasFlag(target)
                            select (cell.Cell, Target: target, cell.Answer)).ToList();

            lock (Log)
            {
                var path = Path.GetFullPath(Path.Combine("TestResults", "measurements", "runs.tsv"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, from cell in observed from answer in cell select string.Join('\t', new Uri(CosmosEmulator.Endpoint!).Authority, axiom.Id, key.Key, cell.Key, answer,
                    string.Join('|', declared.Where(d => d.Cell == cell.Key && d.Answer == answer).Select(d => d.Target)) is { Length: > 0 } targets ? targets : "none"));
            }

            foreach (var cell in declared.Select(d => d.Cell).Distinct())
            {
                observed[cell].Should().ContainSingle($"{axiom.Id}: the catalogue witnesses {key.Key} at {cell}, and they answer alike");
                declared.Where(d => d.Cell == cell).Select(d => d.Answer).Should().Contain(observed[cell].Single(), $"{axiom.Id}: {key.Key} at {cell} answers as a target it names did");
            }
        }

        static string Formula(CosmosOutcome outcome, CosmosValueClass cell) => outcome.Test switch
        {
            CosmosServiceTest.Bare => "c.v",
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
