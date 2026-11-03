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
    /// Every answer is appended to <c>TestResults/measurements/emulator-runs.tsv</c> with the release (CI's
    /// <c>COSMOS_EMULATOR_RELEASE</c>, else the endpoint), the row and the expression before any is asserted, which is what
    /// an emulator run record is built from.
    /// </para>
    /// <para>
    /// A row's test asserts one answer, and a target that answers otherwise fails it. A release a committed record names
    /// must also answer each line recorded for it as recorded, and a release whose own record holds a row must log no line
    /// of that row the record misses: the record is what its pinning tests ask, and a release is named where it parts.
    /// </para>
    /// </remarks>
    internal static class CosmosWitnessProbe
    {

        static readonly object Log = new();

        /// <summary>
        /// The release that answers: <c>COSMOS_EMULATOR_RELEASE</c>, which CI reads off the container's log, else the
        /// endpoint, which no record names.
        /// </summary>
        static string Release => Environment.GetEnvironmentVariable("COSMOS_EMULATOR_RELEASE") is { Length: > 0 } release ? release : new Uri(CosmosEmulator.Endpoint!).Authority;

        /// <summary>Every committed emulator record's answer, by release, row, expression, witness and whether it is a projection alone.</summary>
        static readonly Lazy<Dictionary<(string Release, string Row, string Expression, string Witness, bool Projected), string>> Recorded = new(() =>
            Records().Select(f => f.Split('\t')).GroupBy(f => (f[0], f[1], f[2], f[3], Projected(f[4]))).ToDictionary(g => g.Key, g => g.First()[4]));

        /// <summary>The releases a record is named for, each of which holds every line its rows' tests log.</summary>
        static readonly Lazy<HashSet<string>> Complete = new(() =>
            [.. typeof(CosmosWitnessProbe).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("EmulatorRuns/", StringComparison.Ordinal)).Select(n => Path.GetFileNameWithoutExtension(n).Split('-')[^1])]);

        static IEnumerable<string> Records()
        {
            var assembly = typeof(CosmosWitnessProbe).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("EmulatorRuns/", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                foreach (var line in reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != '#').Skip(1))
                    yield return line;
            }
        }

        /// <summary>Whether an answer is a projection alone, not <c>value/kept</c>.</summary>
        static bool Projected(string answer) => answer.Length < 2 || answer[^2] != '/' || "TFU!?".Contains(answer[^1]) == false;

        /// <summary>
        /// The answer of each expression over each witness, in the order given; skipped where there is no container.
        /// </summary>
        public static Task<string[][]> Ask(Container? container, string? unavailable, string row, IReadOnlyList<string?> witnesses, params string[] expressions) =>
            Ask(container, unavailable, row, witnesses, expressions, members: false);

        /// <summary>
        /// As <see cref="Ask"/>, each witness the members a document holds beside its id, <c>"v":30,"w":"a"</c>, so an
        /// expression may read two paths.
        /// </summary>
        public static Task<string[][]> AskMembers(Container? container, string? unavailable, string row, IReadOnlyList<string> witnesses, string[] expressions) =>
            Ask(container, unavailable, row, witnesses, expressions, members: true);

        static Task<string[][]> Ask(Container? container, string? unavailable, string row, IReadOnlyList<string?> witnesses, string[] expressions, bool members) =>
            Over(container, unavailable, row, witnesses, expressions, members, async (pk, ids) =>
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
            Over(container, unavailable, row, witnesses, expressions, false, async (pk, ids) =>
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
        static async Task<string[][]> Over(Container? container, string? unavailable, string row, IReadOnlyList<string?> witnesses, string[] expressions, bool members, Func<string, string[], Task<string[][]>> ask)
        {
            if (container is null)
                Assert.Skip("These need a service. " + unavailable);

            var pk = Guid.NewGuid().ToString("N");
            var ids = Enumerable.Range(0, witnesses.Count).Select(i => "w" + i).ToArray();
            for (var i = 0; i < witnesses.Count; i++)
            {
                var json = $"{{\"id\":\"w{i}\",\"pk\":\"{pk}\"" + (witnesses[i] is not { Length: > 0 } v ? "" : members ? "," + v : ",\"v\":" + v) + "}";
                using var response = await container!.CreateItemStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), new PartitionKey(pk));
                response.EnsureSuccessStatusCode();
            }

            try
            {
                var answers = await ask(pk, ids);
                var lines = (from e in Enumerable.Range(0, expressions.Length)
                             from i in Enumerable.Range(0, witnesses.Count)
                             select (Expression: expressions[e], Witness: witnesses[i] is { Length: > 0 } w ? w : "absent", Answer: answers[e][i])).ToList();
                lock (Log)
                {
                    var path = Path.GetFullPath(Path.Combine("TestResults", "measurements", "emulator-runs.tsv"));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllLines(path, lines.Select(l => string.Join('\t', Release, row, l.Expression, l.Witness, l.Answer)));
                }

                // A release with a record answers as it did: every line of a release's own record, and any line another holds.
                var (recorded, complete) = (Recorded.Value, Recorded.Value.Keys.Any(k => k.Release == Release && k.Row == row) && Complete.Value.Contains(Release));
                var differs = (from l in lines
                               let key = (Release, row, l.Expression, l.Witness, Projected(l.Answer))
                               let found = recorded.TryGetValue(key, out var was) ? was : null
                               where found is null ? complete : found != l.Answer
                               select $"{l.Expression} over {l.Witness}: {l.Answer}, recorded {found ?? "nothing"}").ToList();
                if (differs.Count > 0)
                    Assert.Fail($"Release {Release} answers {row} otherwise than its record: " + string.Join("; ", differs.Take(20)));

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
