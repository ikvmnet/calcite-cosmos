using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    public partial class CosmosDifferentialTests
    {

        // ── The planted differential: strings holding U+0000 and U+FDD0 ─────────────────────────────────────────────────

        /// <summary>
        /// The emulator stores U+0000 as U+FDD0 <c>0</c> and U+FDD0 as U+FDD0 <c>1</c> (<c>EmulatorStoredText</c>), so a
        /// statement over strings holding either answers as Calcite does only where the adapter sends what the emulator
        /// compares and declines what was not measured. Every statement here is planted over documents holding both, at
        /// <c>s</c>, <c>t</c> and <c>id</c>, across two logical partitions, and judged against the oracle, nothing pushed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rows of the fix it holds</b> (<c>reports/emu-nul/spec.md</c>, §2), each named in its case's label:
        /// D1, <c>=</c>, <c>IN</c>, <c>&lt;&gt;</c> and <c>NOT IN</c> with a value holding either, which the emulator compared
        /// raw with what it stores; D2, a lookup join and a grouped join on such keys; D3, a point read and a
        /// <c>ReadMany</c> of an id holding U+0000, a 500 where the read was sent; D14, the comparisons with a literal the
        /// rendering graph owns, which it models the emulator comparing in the stored spelling; R, a constant the
        /// service returns (§4.6), a <c>CASE</c> arm or a <c>COALESCE</c> operand, sent stored and returned as written;
        /// D4 and D5, a text sort either way and <c>MIN</c> and <c>MAX</c> over text that may hold U+0000, which the
        /// emulator orders stored, U+0000 above every unit from U+0001 to U+FDCF; D6, such text against a parameter; and
        /// D7, against another path. Those are refused under the emulator, and <c>planted_free</c>, whose text holds no
        /// U+0000, is held to keep its pushes: a value holding one is no anchor there, and is read weakened and rechecked.
        /// </para>
        /// <para>
        /// <b>Not judged, and why.</b> No U+FDD0 literal can be written, Calcite casting it to ISO-8859-1, so U+FDD0 reaches
        /// the service as a parameter or as stored data alone. Calcite's own <c>LIKE</c> with U+0000 in the pattern answers
        /// nothing or raises "Invalid escape sequence", so no such pattern is planted. <c>POSITION(? IN x)</c> fails
        /// validation on both sides. The string functions and <c>LIKE</c> over such text are the later rows'.
        /// </para>
        /// <para>
        /// Run where the account is an emulator, and skipped on Azure, which stores every string as written and whose own
        /// order over these strings the probe account's run answers (<c>TODO.md</c>). Its containers are its own, so the
        /// class's other statements read none of its documents.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task EveryPlantedStatementAnswersAsCalcite()
        {
            if (_client is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));
            if (CosmosEmulator.ServiceTarget != CosmosServiceTarget.Emulator)
                Assert.Skip("The plant holds what the emulator stores escaped; Azure stores it as written.");

            var containers = await PlantedContainersSeeded();
            var wrong = new List<string>();
            var unpushed = new List<string>();

            foreach (var c in PlantedCases())
            {
                var sent = new List<string>();
                List<string> oracle, pushed;

                try
                {
                    if (c.Order is PlantedOrder order)
                    {
                        // Judged by code point, every planted string lying below U+D800 or in the BMP, where code point and
                        // code unit agree: the oracle's own sort of text is the CLR runtime's culture order.
                        var values = (await Run(order.Input, false, null, containers, c.Parameters)).Select(Single).ToList();
                        oracle = order.Expected(values);
                        pushed = (await Run(c.Sql, true, sent, containers, c.Parameters)).Select(Single).ToList();
                    }
                    else
                    {
                        oracle = (await Run(c.Sql, false, null, containers, c.Parameters)).Select(Canonical).OrderBy(r => r, StringComparer.Ordinal).ToList();
                        pushed = (await Run(c.Sql, true, sent, containers, c.Parameters)).Select(Canonical).OrderBy(r => r, StringComparer.Ordinal).ToList();
                    }
                }
                catch (Exception e)
                {
                    wrong.Add($"{c.Label}: raised {e.GetType().Name}: {Shown(e.Message.Split('\n')[0])}");
                    continue;
                }

                if (pushed.SequenceEqual(oracle) == false)
                    wrong.Add($"{c.Label}: [{Shown(string.Join("; ", pushed))}] where Calcite gives [{Shown(string.Join("; ", oracle))}], sent {Shown(string.Join(" || ", sent))}");

                // A fix that answered right by keeping everything in process would hold nothing: the statements the stored
                // spelling makes exact still reach the service.
                if (c.Pushes is string expected && sent.Any(q => q.Contains(expected, StringComparison.Ordinal)) == false)
                    unpushed.Add($"{c.Label}: sent {Shown(string.Join(" || ", sent))}, none holding {expected}");
            }

            // Every line said, where an assertion would list ten.
            if (wrong.Count > 0 || unpushed.Count > 0)
                Assert.Fail($"{wrong.Count} WRONG, {unpushed.Count} not pushed:\n" + string.Join("\n", wrong.Concat(unpushed)));
        }

        /// <summary>
        /// A planted statement: what it says, its parameters, a text one statement sent must hold where it pushes, and for a
        /// sort or an extreme, how its answer is judged; any other is judged as a set of rows.
        /// </summary>
        sealed record PlantedCase(string Label, string Sql, object?[]? Parameters = null, string? Pushes = null, PlantedOrder? Order = null);

        enum PlantedOrderKind
        {
            Ascending,
            Descending,
            Min,
            Max,
        }

        /// <summary>
        /// How a planted sort or extreme is judged: its input, one column, and what Calcite answers over it by code point, a
        /// single key leaving no tie to be free in.
        /// </summary>
        sealed record PlantedOrder(string Input, PlantedOrderKind Kind, int? Fetch)
        {
            public List<string> Expected(IReadOnlyList<string> values)
            {
                var sorted = values.Where(v => v != "null").OrderBy(v => v, StringComparer.Ordinal).ToList();
                return Kind switch
                {
                    PlantedOrderKind.Ascending => sorted.Concat(values.Where(v => v == "null")).Take(Fetch ?? int.MaxValue).ToList(),
                    PlantedOrderKind.Descending => values.Where(v => v == "null").Concat(Enumerable.Reverse(sorted)).Take(Fetch ?? int.MaxValue).ToList(),
                    PlantedOrderKind.Min => [sorted.Count == 0 ? "null" : sorted[0]],
                    _ => [sorted.Count == 0 ? "null" : sorted[^1]],
                };
            }
        }

        /// <summary>The one value of a row of one column, as text.</summary>
        static string Single(object? row) => (row is object?[] cells ? cells[0] : row) switch { null => "null", string s => s, var v => Canonical(v) };

        const string Nul = "\u0000";

        const string Fdd0 = "﷐";

        static IEnumerable<PlantedCase> PlantedCases()
        {
            static string J(string p) => $"JSON_VALUE(c.\"DOC\", '$.{p}')";

            foreach (var (table, column, x) in new[]
            {
                ("planted_declared", "s", J("s")), ("planted_declared", "t", J("t")), ("planted_declared", "id", "c.\"id\""),
                ("planted_plain", "s", J("s")), ("planted_plain", "id", "c.\"id\""),
            })
            {
                var from = $"FROM {table} AS c";
                PlantedCase Case(string row, string label, string where, string? pushes, params object?[] parameters) =>
                    new($"{row} {table}.{column} {label}", $"SELECT c.\"id\" {from} WHERE {where}", parameters.Length == 0 ? null : parameters, pushes);

                // D1: each value, its stored spelling's alias U+FDD0 0 among them, which the emulator spells as U+FDD0 1 0.
                foreach (var (name, value) in new[] { ("nul", Nul), ("fdd0", Fdd0), ("a<nul>b", "a" + Nul + "b"), ("<fdd0>0", Fdd0 + "0"), ("<nul><fdd0>", Nul + Fdd0), ("a", "a") })
                {
                    yield return Case("D1", $"= ? {name}", $"{x} = ?", " WHERE ", value);
                    yield return Case("D1", $"<> ? {name}", $"{x} <> ?", " WHERE ", value);
                }

                yield return Case("D1", "IN (?, ?) nul, fdd0", $"{x} IN (?, ?)", " WHERE ", Nul, Fdd0);
                yield return Case("D1", "NOT IN (?, ?) nul, fdd0", $"{x} NOT IN (?, ?)", " WHERE ", Nul, Fdd0);
                yield return Case("D1", "= ? nul OR = ? a", $"{x} = ? OR {x} = ?", " WHERE ", Nul, "a");

                // D14: the literal comparisons S owns, U+0000 the one character a literal can hold.
                yield return Case("D14", "= nul literal", $"{x} = '{Nul}'", null);
                yield return Case("D14", "= a<nul>b literal", $"{x} = 'a{Nul}b'", null);
                yield return Case("D14", "<> nul literal", $"{x} <> '{Nul}'", null);
                yield return Case("D14", "IN literals nul, a", $"{x} IN ('{Nul}', 'a')", null);
                yield return Case("D14", "> 'a' literal", $"{x} > 'a'", null);
                yield return Case("D14", "< 'b' literal", $"{x} < 'b'", null);
            }

            // D4 and D5: a text sort either way, paged and not, and MIN and MAX, over paths that may hold U+0000, across two
            // partitions; refused under the emulator and sorted in process. planted_free's s holds no U+0000, U+FDD0 among
            // its strings, and keeps its pushes: E keeps the order of what holds no U+0000.
            foreach (var (table, column, x, nullable) in new[]
            {
                ("planted_declared", "s", J("s"), false), ("planted_declared", "t", J("t"), true), ("planted_declared", "id", "c.\"id\"", false),
                ("planted_plain", "s", J("s"), false), ("planted_plain", "id", "c.\"id\"", false), ("planted_free", "s", J("s"), false),
            })
            {
                var free = table == "planted_free";
                var input = $"SELECT {x} FROM {table} AS c" + (nullable ? $" WHERE {x} IS NOT NULL" : "");
                foreach (var descending in new[] { false, true })
                    foreach (var fetch in new int?[] { null, 3 })
                        yield return new($"{(descending ? "D5" : "D4")} {table}.{column} ORDER BY{(descending ? " DESC" : "")}{(fetch is null ? "" : " FETCH 3")}",
                            $"{input} ORDER BY {x}{(descending ? " DESC" : "")}{(fetch is int f ? $" FETCH NEXT {f} ROWS ONLY" : "")}", null, free ? " ORDER BY " : null,
                            new PlantedOrder(input, descending ? PlantedOrderKind.Descending : PlantedOrderKind.Ascending, fetch));

                yield return new($"D4 {table}.{column} MIN", $"SELECT MIN({x}) FROM {table} AS c", null, free ? "MIN(" : null, new PlantedOrder(input, PlantedOrderKind.Min, null));
                yield return new($"D5 {table}.{column} MAX", $"SELECT MAX({x}) FROM {table} AS c", null, free ? "MAX(" : null, new PlantedOrder(input, PlantedOrderKind.Max, null));
            }

            // D6: a path against a parameter. Over planted_declared's s, which may hold U+0000, refused whatever the value;
            // over planted_free's, anchored on a value holding no U+0000, one that holds one read weakened and rechecked.
            foreach (var table in new[] { "planted_declared", "planted_free" })
            {
                var x = J("s");
                var pushes = table == "planted_free" ? " WHERE " : null;
                foreach (var (name, value) in new[] { ("nul", Nul), ("a", "a"), ("b", "b"), ("b<nul>", "b" + Nul), ("a<nul>b", "a" + Nul + "b"), ("fdd0", Fdd0), ("b<fdd0>", "b" + Fdd0) })
                {
                    foreach (var op in new[] { ">", ">=", "<", "<=" })
                        yield return new($"D6 {table}.s {op} ? {name}", $"SELECT c.\"id\" FROM {table} AS c WHERE {x} {op} ?", [value], pushes);

                    yield return new($"D6 {table}.s BETWEEN ? a AND ? {name}", $"SELECT c.\"id\" FROM {table} AS c WHERE {x} BETWEEN ? AND ?", ["a", value], pushes);
                }
            }

            // D7: a path against a path. Refused where either may hold U+0000; pushed where neither may.
            foreach (var table in new[] { "planted_declared", "planted_free" })
                foreach (var op in new[] { "<", ">", "<=", ">=" })
                    yield return new($"D7 {table} s {op} t", $"SELECT c.\"id\" FROM {table} AS c WHERE {J("s")} {op} {J("t")}", null, table == "planted_free" ? " WHERE " : null);

            // D2: a lookup join on such keys, inner, left and grouped.
            foreach (var table in new[] { "planted_declared", "planted_plain" })
            {
                yield return new($"D2 {table} JOIN id = keys.ref", $"SELECT o.\"id\", k.\"id\" FROM planted_keys AS k JOIN {table} AS o ON o.\"id\" = JSON_VALUE(k.\"DOC\", '$.ref')");
                yield return new($"D2 {table} LEFT JOIN id = keys.ref", $"SELECT k.\"id\", o.\"id\" FROM planted_keys AS k LEFT JOIN {table} AS o ON o.\"id\" = JSON_VALUE(k.\"DOC\", '$.ref')");
                yield return new($"D2 {table} JOIN s = keys.s", $"SELECT o.\"id\", k.\"id\" FROM planted_keys AS k JOIN {table} AS o ON JSON_VALUE(o.\"DOC\", '$.s') = JSON_VALUE(k.\"DOC\", '$.s')");
                yield return new($"D2 {table} JOIN on s, grouped", $"SELECT JSON_VALUE(k.\"DOC\", '$.s'), COUNT(*) FROM planted_keys AS k JOIN {table} AS o ON JSON_VALUE(o.\"DOC\", '$.s') = JSON_VALUE(k.\"DOC\", '$.s') GROUP BY JSON_VALUE(k.\"DOC\", '$.s')");

                // D3: a point read and a ReadMany of ids holding U+0000, by literal, the one way a read is offered; and by
                // parameter, which is a query.
                yield return new($"D3 {table} id = a<nul> and category", $"SELECT c.\"id\" FROM {table} AS c WHERE c.\"id\" = 'a{Nul}' AND {J("category")} = 'p1'", null, " WHERE ");
                yield return new($"D3 {table} id = nul and category", $"SELECT c.\"id\" FROM {table} AS c WHERE c.\"id\" = '{Nul}' AND {J("category")} = 'p0'", null, " WHERE ");
                yield return new($"D3 {table} id IN (nul, a<nul>) and category", $"SELECT c.\"id\" FROM {table} AS c WHERE c.\"id\" IN ('{Nul}', 'a{Nul}', 'a') AND {J("category")} = 'p0'", null, " WHERE ");
                yield return new($"D3 {table} id = 'a' and category", $"SELECT c.\"id\" FROM {table} AS c WHERE c.\"id\" = 'a' AND {J("category")} = 'p0'", null, "Read(a)");
                yield return new($"D3 {table} id = ? and category", $"SELECT c.\"id\" FROM {table} AS c WHERE c.\"id\" = ? AND {J("category")} = 'p1'", ["a" + Nul], " WHERE ");
                yield return new($"D3 {table} id IN (?, ?) and category", $"SELECT c.\"id\" FROM {table} AS c WHERE c.\"id\" IN (?, ?) AND {J("category")} = 'p0'", [Nul, Fdd0], " WHERE ");
            }

            // §4.6: a constant the service returns, sent stored and returned as written; a parameter alike, U+FDD0 0 among
            // them, which sent raw would come back as the U+0000 it spells. Over planted_plain the CASE and COALESCE stay in
            // process whatever the value, its paths being undeclared, so it is judged and not held to push.
            foreach (var table in new[] { "planted_declared", "planted_plain" })
            {
                var declared = table == "planted_declared";
                var arm = $"CASE WHEN {J("category")} = 'p0' THEN";
                yield return new($"R {table} CASE arm literal a<nul>b", $"SELECT c.\"id\", {arm} 'a{Nul}b' ELSE {J("s")} END FROM {table} AS c", null, declared ? " ? @p" : null);
                foreach (var (name, value) in new[] { ("nul", Nul), ("fdd0", Fdd0), ("<fdd0>0", Fdd0 + "0"), ("<nul><fdd0>", Nul + Fdd0) })
                {
                    yield return new($"R {table} CASE arm ? {name}", $"SELECT c.\"id\", {arm} ? ELSE {J("s")} END FROM {table} AS c", [value], declared ? " ? @p" : null);
                    yield return new($"R {table} COALESCE(t, ?) {name}", $"SELECT c.\"id\", COALESCE({J("t")}, ?) FROM {table} AS c", [value], declared ? " : @p" : null);
                }
            }
        }

        /// <summary>A document of the plant, keeping what <c>orders_declared</c> declares: <c>s</c>, <c>b</c>, <c>e</c>, <c>w</c> and <c>d</c> present.</summary>
        static string PlantedDocument(string id, string category, string s, string? t) =>
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id, ["category"] = category, ["s"] = s, ["t"] = t, ["k"] = 1, ["b"] = true, ["w"] = 3, ["d"] = 0.25, ["e"] = "a",
            });

        static readonly string[] PlantedDocuments =
        [
            PlantedDocument("nul", "p0", Nul, "a" + Nul),
            PlantedDocument("a" + Nul, "p1", "a" + Nul + "b", Nul),
            PlantedDocument(Fdd0, "p0", Fdd0, "a" + Fdd0),
            PlantedDocument(Fdd0 + "0", "p1", Fdd0 + "0", Fdd0),
            PlantedDocument("nulfdd0", "p0", Nul + Fdd0, Fdd0 + "a"),
            PlantedDocument("a", "p0", "a", "a"),
            PlantedDocument("b", "p1", "b", "b"),
            PlantedDocument("empty", "p0", "", ""),
            PlantedDocument("\u0001", "p1", "\u0001", "\u0001"),
            PlantedDocument("nul2", "p1", Nul, "x"),
            PlantedDocument(Nul, "p0", "c", null),
        ];

        static readonly string[] PlantedKeyDocuments =
        [
            """{"id":"k1","category":"q","s":"\u0000","ref":"nul"}""",
            """{"id":"k2","category":"q","s":"﷐","ref":"﷐"}""",
            """{"id":"k3","category":"q","s":"a","ref":"a\u0000"}""",
            """{"id":"k4","category":"q","s":"a\u0000b","ref":"\u0000"}""",
            """{"id":"k5","category":"q","s":"﷐0","ref":"﷐0"}""",
            """{"id":"k6","category":"q","s":"b","ref":"b"}""",
        ];

        /// <summary>
        /// The documents of <c>planted_free</c>, whose <c>s</c> and <c>t</c> hold no U+0000 and are declared so: strings
        /// either side of a U+0001, U+FDD0 among them, which a value holding U+0000 parts from under the emulator.
        /// </summary>
        static readonly string[] PlantedFreeDocuments =
        [
            PlantedDocument("f1", "p0", "a", "b"),
            PlantedDocument("f2", "p1", "b", "a"),
            PlantedDocument("f3", "p0", "b\u0001", "b"),
            PlantedDocument("f4", "p1", "c", "b\u0001"),
            PlantedDocument("f5", "p0", "\u0001", ""),
            PlantedDocument("f6", "p1", "", "\u0001"),
            PlantedDocument("f7", "p0", "a" + Fdd0, Fdd0),
            PlantedDocument("f8", "p1", Fdd0, "a" + Fdd0),
            PlantedDocument("f9", "p0", "b" + Fdd0 + "x", "c"),
        ];

        /// <summary>What <c>planted_free</c> declares: <c>s</c> and <c>t</c> strings of no U+0000 and nothing from U+D800 but U+FDD0.</summary>
        const string PlantedFreeSchema = """
            { "type": "object", "required": ["s", "t"], "properties": {
                "s": { "type": "string", "pattern": "^[\\u0001-\\uD7FF\\uFDD0]*$" },
                "t": { "type": "string", "pattern": "^[\\u0001-\\uD7FF\\uFDD0]*$" } } }
            """;

        static readonly CosmosContainerMetadata PlantedFree = OrdersContainer("planted_free", PlantedFreeSchema);

        static readonly CosmosContainerMetadata PlantedDeclared = OrdersContainer("planted_declared", OrdersDeclaredSchema);

        static readonly CosmosContainerMetadata PlantedPlain = OrdersContainer("planted_plain", null);

        static readonly CosmosContainerMetadata PlantedKeys = new("planted_keys", ["/category"]);

        static IReadOnlyList<(CosmosContainerMetadata Metadata, Container Container)>? _plantedContainers;

        static readonly SemaphoreSlim _plantedSeeding = new(1, 1);

        /// <summary>Seeds the plant's four containers once, the first time it runs, in the class's database, which its cleanup deletes.</summary>
        static async Task<IReadOnlyList<(CosmosContainerMetadata Metadata, Container Container)>> PlantedContainersSeeded()
        {
            await _plantedSeeding.WaitAsync();
            try
            {
                if (_plantedContainers is null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var database = _client!.GetDatabase(DatabaseName);
                    _plantedContainers =
                    [
                        (PlantedDeclared, await Seed(database, PlantedDeclared.Name, PlantedDocuments, cts.Token, policy: OrderIndexingPolicy(composites: true))),
                        (PlantedPlain, await Seed(database, PlantedPlain.Name, PlantedDocuments, cts.Token, policy: OrderIndexingPolicy(composites: false))),
                        (PlantedKeys, await Seed(database, PlantedKeys.Name, PlantedKeyDocuments, cts.Token)),
                        (PlantedFree, await Seed(database, PlantedFree.Name, PlantedFreeDocuments, cts.Token, policy: OrderIndexingPolicy(composites: true))),
                    ];
                }

                return _plantedContainers;
            }
            finally
            {
                _plantedSeeding.Release();
            }
        }

        /// <summary>A string with every unit outside printable ASCII escaped, for a message.</summary>
        static string Shown(string text)
        {
            var shown = new StringBuilder(text.Length);
            foreach (var unit in text)
                shown.Append(unit < 0x20 || unit >= 0x7F || unit == '\\' ? $"\\u{(int)unit:X4}" : unit.ToString());

            return shown.ToString();
        }

    }

}
