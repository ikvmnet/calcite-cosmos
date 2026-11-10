using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// service refuses the statement over that witness alone, and the whole answer is <see cref="Unanswered"/> where it
    /// leaves one unanswered twice, or <see cref="Failed"/> where it answered the statements asked beside it.
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
        /// The answer of a witness whose statement, asked of it alone, the service left unanswered twice (503 or 408): the
        /// line measures nothing, where a <c>!</c> is the service's refusal. Probe run 3 left seven so.
        /// </summary>
        internal const string Unanswered = "unanswered";

        /// <summary>
        /// The answer of a witness whose statement the service left unanswered every time it was asked, while it answered the
        /// statements <see cref="ProjectAlone"/> asked just before and just after it: not a busy service, a statement that
        /// fails there, which reads as a <c>!</c> does (<see cref="CosmosFunctionComparison.Judge"/>). Probe run 4's search
        /// U+0000 then <c>1</c>, read from a path, five times on the probe account.
        /// </summary>
        internal const string Failed = "failed";

        /// <summary>
        /// The release that answers: <c>COSMOS_EMULATOR_RELEASE</c>, which CI reads off the container's log, else the
        /// endpoint, which no record names.
        /// </summary>
        internal static string Release => Environment.GetEnvironmentVariable("COSMOS_EMULATOR_RELEASE") is { Length: > 0 } release ? release : new Uri(CosmosEmulator.Endpoint!).Authority;

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
                var (all, _) = await Rows(container!, pk, "SELECT c.id, " + string.Join(", ", expressions.Select((e, i) => $"({e}) AS r{i}")) + " FROM c");
                var answers = new string[expressions.Length][];
                for (var e = 0; e < expressions.Length; e++)
                {
                    var rows = all is null ? await Each(container!, pk, ids, $"SELECT c.id, ({expressions[e]}) AS r{e} FROM c", id => $"SELECT c.id, ({expressions[e]}) AS r{e} FROM c WHERE c.id = \"{id}\"")
                        : ids.ToDictionary(id => id, id => (object?)all.GetValueOrDefault(id));
                    answers[e] = [.. ids.Select(id => rows[id] is string failed ? failed : rows[id] is JObject one && one.TryGetValue("r" + e, out var r) ? r.ToString(Formatting.None) : "undefined")];
                }

                return answers;
            });

        /// <summary>
        /// What each expression projects over a witness, each pair asked alone in a statement of its own in the order
        /// <paramref name="statements"/> gives, spelled as <see cref="Project"/> spells it, and logged once: a pair asked
        /// again is answered by a try the service answered, if any. Where the service leaves a statement unanswered (503 or
        /// 408) it is asked again after a pause that doubles from <paramref name="pause"/>, at most <paramref name="tries"/>
        /// times in all, so that a statement that fails every time is told from a busy service: a pair every statement asking
        /// it left unanswered, each between two the service answered, is <see cref="Failed"/>. Every try is appended to
        /// <c>TestResults/measurements/tries.tsv</c>: when it ended, the release, the row, the statement's place, the
        /// expression, the witness, the try, its status and sub-status, its charge, its milliseconds, and its answer or
        /// the first line of why there is none. A pair no statement asks is <c>null</c>.
        /// </summary>
        public static Task<string[][]> ProjectAlone(Container? container, string? unavailable, string row, IReadOnlyList<string?> witnesses, string[] expressions,
            IReadOnlyList<(int Expression, int Witness)> statements, int tries, TimeSpan pause) =>
            Over(container, unavailable, row, witnesses, expressions, false, async (pk, ids) =>
            {
                var said = new string[statements.Count];
                for (var s = 0; s < statements.Count; s++)
                {
                    var (e, i) = statements[s];
                    var (place, witness) = (s + 1, witnesses[i] is { Length: > 0 } w ? w : "absent");
                    var (rows, failed) = await Rows(container!, pk, $"SELECT c.id, ({expressions[e]}) AS r FROM c WHERE c.id = \"{ids[i]}\"", tries, pause,
                        (attempt, charge, took, error, answered) => Tried(row, place, expressions[e], witness, attempt, charge, took, error, answered is null ? null : Value(answered.GetValueOrDefault(ids[i]))));
                    said[s] = rows is null ? failed : Value(rows.GetValueOrDefault(ids[i]));
                }

                // A pair takes a statement's answer where one was answered; else it failed where each statement asking it
                // stood between two the service answered, and went unanswered where one beside it did too.
                bool Beside(int s) => s > 0 && s + 1 < said.Length && said[s - 1] != Unanswered && said[s + 1] != Unanswered;
                var answers = expressions.Select(_ => new string[ids.Length]).ToArray();
                foreach (var pair in statements.Distinct())
                {
                    var asking = Enumerable.Range(0, statements.Count).Where(s => statements[s] == pair).ToList();
                    answers[pair.Expression][pair.Witness] = asking.Select(s => said[s]).FirstOrDefault(a => a != Unanswered)
                        ?? (asking.All(Beside) ? Failed : Unanswered);
                }

                return answers;
            }, [.. statements.Distinct()]);

        /// <summary>A row's projection <c>r</c> as <see cref="Project"/> spells it: its JSON, or <c>undefined</c> where the object constructor omits it.</summary>
        static string Value(JObject? row) => row is not null && row.TryGetValue("r", out var r) ? r.ToString(Formatting.None) : "undefined";

        /// <summary>Appends one try of a statement <see cref="ProjectAlone"/> asked to <c>TestResults/measurements/tries.tsv</c>.</summary>
        static void Tried(string row, int place, string expression, string witness, int attempt, double charge, TimeSpan took, CosmosException? error, string? answer)
        {
            var status = error is null ? "200" : $"{(int)error.StatusCode}/{error.SubStatusCode}";
            var why = error?.Message.Split('\n')[0].Replace('\t', ' ').Replace("\r", "") ?? "";
            var outcome = answer ?? why[..Math.Min(why.Length, 240)];
            lock (Log)
            {
                var path = Path.GetFullPath(Path.Combine("TestResults", "measurements", "tries.tsv"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, [string.Join('\t', DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture), Release, row,
                    place.ToString(CultureInfo.InvariantCulture), expression, witness, attempt.ToString(CultureInfo.InvariantCulture), status,
                    charge.ToString("F2", CultureInfo.InvariantCulture), ((long)took.TotalMilliseconds).ToString(CultureInfo.InvariantCulture), outcome)]);
            }
        }

        /// <summary>
        /// Writes the witnesses, asks, logs every answer and deletes the witnesses: each expression's over each witness, or
        /// only the pairs <paramref name="pairs"/> names.
        /// </summary>
        static async Task<string[][]> Over(Container? container, string? unavailable, string row, IReadOnlyList<string?> witnesses, string[] expressions, bool members,
            Func<string, string[], Task<string[][]>> ask, IReadOnlyList<(int Expression, int Witness)>? pairs = null)
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
                var lines = (from p in pairs ?? [.. from e in Enumerable.Range(0, expressions.Length) from i in Enumerable.Range(0, witnesses.Count) select (e, i)]
                             select (Expression: expressions[p.Expression], Witness: witnesses[p.Witness] is { Length: > 0 } w ? w : "absent", Answer: answers[p.Expression][p.Witness])).ToList();
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

        /// <summary>
        /// What one witness answers: its row, <c>null</c> where none came back, <c>!</c> where the service refused its
        /// statement, and <see cref="Unanswered"/> whole where it left any of the three unanswered.
        /// </summary>
        static string Answer(object? projected, object? kept, object? negated)
        {
            if (projected is Unanswered || kept is Unanswered || negated is Unanswered)
                return Unanswered;

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
            if (await Rows(container, pk, sql) is ({ } rows, _))
            {
                foreach (var id in ids)
                    answers[id] = rows.GetValueOrDefault(id);
            }
            else
            {
                foreach (var id in ids)
                {
                    var (one, failed) = await Rows(container, pk, alone(id));
                    answers[id] = one is null ? failed : one.GetValueOrDefault(id);
                }
            }

            return answers;
        }

        /// <summary>
        /// Each row by its id; or, where the statement fails at the service, what each witness it asked answers: <c>!</c>
        /// where the service refused it, <see cref="Unanswered"/> where it left it unanswered <paramref name="tries"/> times
        /// (503 or 408), pausing between them where <paramref name="pause"/> is more than nothing, each pause twice the one
        /// before. The caller narrows either, so one failing expression or witness fails alone. Probe run 2's projection of
        /// every string function went unanswered once, after 43 s, and answered nothing; run 3's, and two spellings asked
        /// alone, went unanswered twice, the account refusing none. <paramref name="tried"/> hears each try: its number, its
        /// charge, how long it took, the failure and the rows.
        /// </summary>
        static async Task<(Dictionary<string, JObject>? Rows, string Failed)> Rows(Container container, string pk, string sql, int tries = 2, TimeSpan pause = default,
            Action<int, double, TimeSpan, CosmosException?, Dictionary<string, JObject>?>? tried = null)
        {
            for (var attempt = 1; ; attempt++)
            {
                var rows = new Dictionary<string, JObject>(StringComparer.Ordinal);
                var (started, charge) = (Stopwatch.GetTimestamp(), 0d);
                try
                {
                    using var iterator = container.GetItemQueryIterator<JObject>(new QueryDefinition(sql), requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(pk) });
                    while (iterator.HasMoreResults)
                    {
                        var page = await iterator.ReadNextAsync();
                        charge += page.RequestCharge;
                        foreach (var row in page)
                            rows[(string)row["id"]!] = row;
                    }

                    tried?.Invoke(attempt, charge, Stopwatch.GetElapsedTime(started), null, rows);
                    return (rows, "");
                }
                catch (CosmosException e) when (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError)
                {
                    tried?.Invoke(attempt, charge + e.RequestCharge, Stopwatch.GetElapsedTime(started), e, null);
                    return (null, "!");
                }
                catch (CosmosException e) when (e.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout)
                {
                    tried?.Invoke(attempt, charge + e.RequestCharge, Stopwatch.GetElapsedTime(started), e, null);
                    if (attempt >= tries)
                        return (null, Unanswered);

                    if (pause > TimeSpan.Zero)
                        await Task.Delay(pause * Math.Pow(2, attempt - 1));
                }
            }
        }

    }

}
