using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// Asks the service expressions over witnesses at <c>c.v</c>, written under a partition of their own and deleted
    /// after: what each projects, and whether a <c>WHERE</c> and a <c>WHERE NOT</c> over it keep the witness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A witness is a JSON text, or <c>null</c> for an absent path, and its answer is spelled <c>value/kept</c>: the
    /// projection's JSON or <c>undefined</c> where the object constructor omits it, then <c>T</c> where the <c>WHERE</c>
    /// keeps it, <c>F</c> where the <c>WHERE NOT</c> does, <c>U</c> where neither; <c>!</c> stands for either part where the
    /// statement fails over that witness alone.
    /// Every answer is appended to <c>TestResults/measurements/emulator-runs.tsv</c> with the endpoint,
    /// the row and the expression before any is asserted, which is what an emulator run record is built from.
    /// </para>
    /// <para>
    /// It never asks which endpoint answered: a row's test asserts one answer, and a target that answers otherwise fails it.
    /// </para>
    /// </remarks>
    internal static class CosmosWitnessProbe
    {

        static readonly object Log = new();

        /// <summary>
        /// The answer of each expression over each witness, in the order given; skipped where there is no container.
        /// </summary>
        public static Task<string[][]> Ask(Container? container, string? unavailable, string row, IReadOnlyList<string?> witnesses, params string[] expressions) =>
            Over(container, unavailable, row, witnesses, expressions, async (pk, ids) =>
            {
                var answers = new string[expressions.Length][];
                for (var e = 0; e < expressions.Length; e++)
                {
                    var projected = await Each(container!, pk, ids, $"SELECT c.id, ({expressions[e]}) AS r FROM c", id => $"SELECT c.id, ({expressions[e]}) AS r FROM c WHERE c.id = \"{id}\"");
                    var kept = await Each(container!, pk, ids, $"SELECT c.id FROM c WHERE {expressions[e]}", id => $"SELECT c.id FROM c WHERE c.id = \"{id}\" AND ({expressions[e]})");
                    var negated = await Each(container!, pk, ids, $"SELECT c.id FROM c WHERE NOT ({expressions[e]})", id => $"SELECT c.id FROM c WHERE c.id = \"{id}\" AND NOT ({expressions[e]})");
                    answers[e] = [.. ids.Select(id => Answer(projected[id], kept[id], negated[id]))];
                }

                return answers;
            });

        /// <summary>
        /// What each expression projects over each witness, spelled as <see cref="Ask"/> spells a value, with no
        /// <c>WHERE</c>: one statement for them all, and an expression's own only where that one fails.
        /// </summary>
        public static Task<string[][]> Project(Container? container, string? unavailable, string row, IReadOnlyList<string?> witnesses, params string[] expressions) =>
            Over(container, unavailable, row, witnesses, expressions, async (pk, ids) =>
            {
                var all = await Rows(container!, pk, "SELECT c.id, " + string.Join(", ", expressions.Select((e, i) => $"({e}) AS r{i}")) + " FROM c");
                var answers = new string[expressions.Length][];
                for (var e = 0; e < expressions.Length; e++)
                {
                    var rows = all is null ? await Each(container!, pk, ids, $"SELECT c.id, ({expressions[e]}) AS r{e} FROM c", id => $"SELECT c.id, ({expressions[e]}) AS r{e} FROM c WHERE c.id = \"{id}\"")
                        : ids.ToDictionary(id => id, id => (object?)all.GetValueOrDefault(id));
                    answers[e] = [.. ids.Select(id => rows[id] is "!" ? "!" : rows[id] is JObject one && one.TryGetValue("r" + e, out var r) ? r.ToString(Formatting.None) : "undefined")];
                }

                return answers;
            });

        /// <summary>Writes the witnesses, asks, logs every answer and deletes the witnesses.</summary>
        static async Task<string[][]> Over(Container? container, string? unavailable, string row, IReadOnlyList<string?> witnesses, string[] expressions, Func<string, string[], Task<string[][]>> ask)
        {
            if (container is null)
                Assert.Skip("These need a service. " + unavailable);

            var pk = Guid.NewGuid().ToString("N");
            var ids = Enumerable.Range(0, witnesses.Count).Select(i => "w" + i).ToArray();
            for (var i = 0; i < witnesses.Count; i++)
            {
                var json = $"{{\"id\":\"w{i}\",\"pk\":\"{pk}\"" + (witnesses[i] is { } v ? ",\"v\":" + v : "") + "}";
                using var response = await container!.CreateItemStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), new PartitionKey(pk));
                response.EnsureSuccessStatusCode();
            }

            try
            {
                var answers = await ask(pk, ids);
                lock (Log)
                {
                    var path = Path.GetFullPath(Path.Combine("TestResults", "measurements", "emulator-runs.tsv"));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllLines(path, from e in Enumerable.Range(0, expressions.Length)
                                              from i in Enumerable.Range(0, witnesses.Count)
                                              select string.Join('\t', new Uri(CosmosEmulator.Endpoint!).Authority, row, expressions[e], witnesses[i] ?? "absent", answers[e][i]));
                }

                return answers;
            }
            finally
            {
                foreach (var id in ids)
                    using (await container!.DeleteItemStreamAsync(id, new PartitionKey(pk))) { }
            }
        }

        /// <summary>What one witness answers: its row, <c>null</c> where none came back, or <c>!</c> where its statement failed.</summary>
        static string Answer(object? projected, object? kept, object? negated)
        {
            var value = projected is "!" ? "!" : projected is JObject row && row.TryGetValue("r", out var r) ? r.ToString(Formatting.None) : "undefined";
            var truth = kept is "!" || negated is "!" ? "!" : kept is not null ? negated is not null ? "?" : "T" : negated is not null ? "F" : "U";
            return value + "/" + truth;
        }

        /// <summary>
        /// Each witness's row of a statement asked over them all, or, where that fails, of the statement asked of the
        /// witness alone: a value one witness makes that JSON cannot hold, an infinity, fails the statement for every row.
        /// </summary>
        static async Task<Dictionary<string, object?>> Each(Container container, string pk, string[] ids, string sql, Func<string, string> alone)
        {
            var answers = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (await Rows(container, pk, sql) is { } rows)
            {
                foreach (var id in ids)
                    answers[id] = rows.GetValueOrDefault(id);
            }
            else
            {
                foreach (var id in ids)
                    answers[id] = await Rows(container, pk, alone(id)) is { } one ? one.GetValueOrDefault(id) : "!";
            }

            return answers;
        }

        /// <summary>Each row by its id, or <c>null</c> where the statement fails at the service.</summary>
        static async Task<Dictionary<string, JObject>?> Rows(Container container, string pk, string sql)
        {
            var rows = new Dictionary<string, JObject>(StringComparer.Ordinal);
            try
            {
                using var iterator = container.GetItemQueryIterator<JObject>(new QueryDefinition(sql), requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(pk) });
                while (iterator.HasMoreResults)
                    foreach (var row in await iterator.ReadNextAsync())
                        rows[(string)row["id"]!] = row;
            }
            catch (CosmosException e) when (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError)
            {
                return null;
            }

            return rows;
        }

    }

}
