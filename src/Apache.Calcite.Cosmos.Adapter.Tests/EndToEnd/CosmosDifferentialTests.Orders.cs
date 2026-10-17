using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// The <c>ORDER BY</c> yardstick: every generated statement whose answer depends on an order is
    /// judged by whether Calcite's semantics allow the answer, not by whether it equals Calcite's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule it holds the adapter to is the owner's:</b> an order goes to the service where the
    /// adapter can prove the answer allowable — ties free, no <c>ORDER BY</c> free, a <c>FETCH</c> free
    /// among tied rows — and nowhere else. The written corpus compares exact sequences, which is
    /// stricter than that on ties and is computed by a runtime that orders multi-key text by culture;
    /// here each answer is checked against (A0)–(A3) of <see cref="OrderAllowability"/>, with a
    /// comparator written from Calcite's Java semantics in the test and borrowed from nothing it
    /// judges.
    /// </para>
    /// <para>
    /// <b>The input is read in process.</b> R, the rows a sort is over, is the same statement with
    /// its <c>ORDER BY</c>, <c>OFFSET</c> and <c>FETCH</c> taken away, its keys selected beside its
    /// columns, and every pushdown rule excluded, as the corpus's oracle is. A pushdown that raises
    /// where Calcite answers fails. A statement whose R Calcite raises over is not judged, with one
    /// exception: an answer that shows its keys is held to its own order, since no input makes rows
    /// allowable whose keys, as the answer itself shows them, run against the <c>ORDER BY</c>.
    /// </para>
    /// <para>
    /// <b>What the service accepts is judged too, as Azure accepts it.</b> The emulator orders an
    /// excluded path and serves a multi-key <c>ORDER BY</c> with no composite index; Azure answers
    /// both with a 400, measured on the probe account. So every <c>ORDER BY</c> a plan sends is held
    /// to the indexing policy the container was created with: every key an indexed path, and every
    /// multi-key order served by a composite index in its directions or their exact reverse.
    /// </para>
    /// <para>
    /// <b>Three containers hold the same shapes.</b> <c>orders_plain</c> declares nothing;
    /// <c>orders_declared</c> declares types, bounds, <c>required</c> and an <c>enum</c>, and its
    /// metadata the composite indexes; <c>orders_ascii</c> declares the same and the characters of its
    /// strings, by JSON Schema <c>pattern</c>: a UUID, an identifier, and nothing at or above U+D800.
    /// Each declaration is kept by its documents, since a declaration is the caller's word and a
    /// container that broke it would test the word rather than the reasoning.
    /// </para>
    /// <para>
    /// <b>A failure names its smallest form and its family.</b> It is narrowed by dropping the page,
    /// then each key, then each conjunct, as the ternary logic gate narrows a predicate, and its family
    /// is read from the two rows it holds out of order: text either side of U+D800, the two nulls of
    /// a leading key, a double the service printed as another integer, a null placed where Calcite
    /// does not place it. An answer the CLR runtime itself gets wrong, where no order was pushed, is
    /// reported apart: that is <c>calcite-dotnet</c>'s, not the adapter's.
    /// </para>
    /// </remarks>
    public partial class CosmosDifferentialTests
    {

        /// <summary>
        /// How many items <see cref="EveryOrderIsOneCalciteAllows"/> generates, rotating over the
        /// three containers: <see cref="GateOrderedItems"/> where <c>COSMOS_ORDER_GATE=1</c>, and
        /// <see cref="DefaultOrderedItems"/> otherwise. A keyset item is three statements.
        /// </summary>
        static int OrderedItems => Environment.GetEnvironmentVariable("COSMOS_ORDER_GATE") == "1"
            ? GateOrderedItems
            : DefaultOrderedItems;

        const int GateOrderedItems = 1_000;

        /// <summary>
        /// The run a pull request pays for: the gate's first items, the generator drawing them in order
        /// from one seed, so that the default is a prefix of the gate as the ternary logic gate's is.
        /// </summary>
        const int DefaultOrderedItems = 200;

        const int OrderSeed = 20_261_016;

        // ── The containers ────────────────────────────────────────────────────────────────────

        /// <summary>The paths every pair of which has a composite index, in both direction patterns.</summary>
        static readonly string[] OrderIndexedPairs = ["/s", "/t", "/k", "/b", "/w", "/id", "/e"];

        static IEnumerable<(string First, string Second, bool SecondDescending)> OrderCompositePairs() =>
            from first in OrderIndexedPairs
            from second in OrderIndexedPairs
            where first != second
            from down in new[] { false, true }
            select (first, second, down);

        static CosmosCompositeIndex[] OrderCompositeIndexes() => OrderCompositePairs()
            .Select(p => new CosmosCompositeIndex([new CosmosCompositeIndexPath(p.First, false), new CosmosCompositeIndexPath(p.Second, p.SecondDescending)]))
            .ToArray();

        /// <summary>
        /// The indexing policy a container is created with: the service's default, which indexes every
        /// path but <c>_etag</c>, and the composite indexes where the metadata declares them.
        /// </summary>
        static IndexingPolicy OrderIndexingPolicy(bool composites)
        {
            var policy = new IndexingPolicy();
            policy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
            policy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"_etag\"/?" });

            if (composites)
                foreach (var (first, second, descending) in OrderCompositePairs())
                    policy.CompositeIndexes.Add(new Collection<CompositePath>
                    {
                        new() { Path = first, Order = CompositePathSortOrder.Ascending },
                        new() { Path = second, Order = descending ? CompositePathSortOrder.Descending : CompositePathSortOrder.Ascending },
                    });

            return policy;
        }

        /// <summary>
        /// What <c>orders_declared</c> declares: <c>s</c> and <c>b</c> present and never null,
        /// <c>t</c> and <c>k</c> nullable, <c>w</c> an integer with no bound, <c>d</c> a number within
        /// ±10^15, and <c>e</c> one of four strings either side of U+D800.
        /// </summary>
        const string OrdersDeclaredSchema = "{ \"type\": \"object\", \"required\": [\"s\", \"b\", \"e\", \"w\", \"d\"], \"properties\": { " +
            "\"s\": { \"type\": \"string\" }, \"t\": { \"type\": [\"string\", \"null\"] }, \"k\": { \"type\": [\"integer\", \"null\"] }, " +
            "\"b\": { \"type\": \"boolean\" }, \"w\": { \"type\": \"integer\" }, " +
            "\"d\": { \"type\": \"number\", \"minimum\": -1000000000000000, \"maximum\": 1000000000000000 }, " +
            "\"e\": { \"enum\": [\"a\", \"B\", \"\\uE000\", \"\\uD83D\\uDE00\"] }, \"g\": { \"type\": \"string\" }, \"at\": { \"type\": \"string\" } } }";

        /// <summary>The identifier the ASCII container's ids are declared to be.</summary>
        const string IdentifierPattern = "^[A-Za-z0-9_-]+$";

        /// <summary>A UUID in either case, which is how the ASCII container's <c>g</c> is declared.</summary>
        const string UuidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

        /// <summary>Every character below U+D800, which is how its text is declared.</summary>
        const string BelowSurrogatesPattern = "^[\\\\u0000-\\\\uD7FF]*$";

        /// <summary>
        /// What <c>orders_ascii</c> declares: what <c>orders_declared</c> does, its <c>enum</c> below
        /// U+D800, and the characters of <c>id</c>, <c>s</c>, <c>t</c> and <c>g</c> by <c>pattern</c>.
        /// </summary>
        const string OrdersAsciiSchema = "{ \"type\": \"object\", \"required\": [\"s\", \"b\", \"e\", \"w\", \"d\"], \"properties\": { " +
            "\"id\": { \"type\": \"string\", \"pattern\": \"" + IdentifierPattern + "\" }, " +
            "\"s\": { \"type\": \"string\", \"pattern\": \"" + BelowSurrogatesPattern + "\" }, " +
            "\"t\": { \"type\": [\"string\", \"null\"], \"pattern\": \"" + BelowSurrogatesPattern + "\" }, \"k\": { \"type\": [\"integer\", \"null\"] }, " +
            "\"b\": { \"type\": \"boolean\" }, \"w\": { \"type\": \"integer\" }, " +
            "\"d\": { \"type\": \"number\", \"minimum\": -1000000000000000, \"maximum\": 1000000000000000 }, " +
            "\"e\": { \"enum\": [\"a\", \"B\", \"\\u00E9\", \"\\u4E00\"] }, \"g\": { \"type\": \"string\", \"pattern\": \"" + UuidPattern + "\" }, \"at\": { \"type\": \"string\" } } }";

        static CosmosContainerMetadata OrdersContainer(string name, string? schema) => schema is null
            ? new CosmosContainerMetadata(name, ["/category"])
            : new CosmosContainerMetadata(name, ["/category"], OrderCompositeIndexes())
                .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(schema)!));

        static readonly CosmosContainerMetadata OrdersPlain = OrdersContainer("orders_plain", null);

        static readonly CosmosContainerMetadata OrdersDeclared = OrdersContainer("orders_declared", OrdersDeclaredSchema);

        static readonly CosmosContainerMetadata OrdersAscii = OrdersContainer("orders_ascii", OrdersAsciiSchema);

        // ── The documents ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Strings either side of the line where code point order and UTF-16 order part, and the ones
        /// culture comparison mistakes: below U+D800 the two orders are one, U+E000 to U+FFFF sort below
        /// a supplementary character by code point and above it by code unit, and a soft hyphen or a
        /// combining accent is a character to both.
        /// </summary>
        static readonly string[] OrderStrings =
        [
            "", "a", "A", "B", "_", "1", "\u00E9", "e\u0301", "a\u00ADb", "ab", "a b",
            "a\uE000", "a\U0001F600", "\uD7FF", "\uE000", "\uFF21", "\uFFFD", "\uFFFF",
            "\U00010000", "\U0001F600", "\U0001F600a", "\U0010FFFF",
        ];

        /// <summary>The strings an id can be: every one of <see cref="OrderStrings"/> but the empty one.</summary>
        static readonly string[] OrderIds = OrderStrings.Where(s => s.Length > 0).ToArray();

        /// <summary>Strings below U+D800, as many as <see cref="OrderStrings"/>, for the ASCII container.</summary>
        static readonly string[] OrderLowStrings =
        [
            "", "a", "A", "B", "_", "1", "\u00E9", "e\u0301", "a\u00ADb", "ab", "a b",
            "a\uD7FF", "\uD7FF", "\u00FF", "\u0100", "\u4E00", "\u3042", "~", "\u00C5", "A\u030A", "\u212B", "Z",
        ];

        /// <summary>Identifiers, as many as <see cref="OrderIds"/>.</summary>
        static readonly string[] OrderIdentifiers = ["a", "A", "B", "_", "1", "a-b", "Z9", "x_1", "-", "ab", "AB", "b", "c", "z", "Z", "0", "9", "a_", "_a", "aa", "zz"];

        /// <summary>UUIDs, one value in two cases and one in mixed case among them.</summary>
        static readonly string[] OrderUuids =
        [
            "123e4567-e89b-12d3-a456-426614174000",
            "123E4567-E89B-12D3-A456-426614174000",
            "fffffff0-0000-0000-0000-000000000000",
            "00000000-0000-0000-8000-000000000000",
            "Abcdef01-2345-6789-abcd-ef0123456789",
            "7fffffff-ffff-ffff-ffff-ffffffffffff",
        ];

        /// <summary>Instants, at a second, a millisecond and below it, which Calcite reads to the millisecond.</summary>
        static readonly string[] OrderInstants =
        [
            "2024-01-01 00:00:00", "2024-01-01 00:00:00.001", "2024-01-01 00:00:00.0005",
            "2023-06-15 12:30:45.123", "2023-06-15 12:30:45.1234", "2025-12-31 23:59:59.999",
        ];

        /// <summary>
        /// The values of <c>v</c>, which nothing declares, written as raw JSON: every JSON type, and
        /// the numbers where an int64, a double and the service's printing of one part — 2^53 + 1, 2^63 - 1
        /// and the double 2^63, which is printed back as another integer.
        /// </summary>
        static readonly string[] OrderAnything =
        [
            "0", "-0.0", "1", "1.0", "1.5", "2147483647", "2147483648", "9007199254740992", "9007199254740993",
            "1152921504606846976", "1152921504606846977", "9223372036854775807", "9223372036854775808",
            "100000000000000001", "1e20", "\"30\"", "true", "false", "null", null!, "[]", "[1]", "{}", "{\"a\":1}",
        ];

        /// <summary>
        /// The integers of <c>w</c>, which is declared an integer and nothing more: 2^63 written whole is
        /// one. None fits an <c>INTEGER</c>, since <c>RETURNING BIGINT</c> raises over a stored integer
        /// that does — it asserts <c>java.lang.Long</c> and is handed an <c>Integer</c>
        /// (calcite-dotnet#120) — and the statement would not be judged.
        /// </summary>
        static readonly string[] OrderIntegers =
        [
            "2147483648", "-2147483649", "9007199254740992", "9007199254740993", "-9007199254740993", "1152921504606846976",
            "1152921504606846977", "9223372036854775807", "9223372036854775808", "100000000000000001",
        ];

        /// <summary>The fractions of <c>d</c>, none whole: <c>RETURNING DOUBLE</c> raises over a whole number.</summary>
        static readonly string[] OrderFractions = ["-2.5", "-0.5", "0.25", "1.5", "2.5", "3.75", "999999999999999.5", "-999999999999999.5"];

        /// <summary>The integers of <c>k</c>, a JSON null and nothing.</summary>
        static readonly string?[] OrderSmallIntegers = ["-1", "0", "1", "3", "10", "100", "null", null];

        const int OrderDocumentCount = 48;

        /// <summary>
        /// The documents of one container, by index: four logical partitions, each value of each path
        /// repeated across them so that ties exist, and ids unique within a partition and repeated
        /// across partitions, since that is all the service promises of an id.
        /// </summary>
        static string[] OrderDocuments(bool ascii)
        {
            var strings = ascii ? OrderLowStrings : OrderStrings;
            var ids = ascii ? OrderIdentifiers : OrderIds;
            var options = new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            var documents = new string[OrderDocumentCount];
            for (var i = 0; i < OrderDocumentCount; i++)
            {
                using var buffer = new System.IO.MemoryStream();
                using (var json = new Utf8JsonWriter(buffer, options))
                {
                    json.WriteStartObject();
                    json.WriteString("id", ids[(5 * (i / 4) + 3 * (i % 4)) % ids.Length]);
                    json.WriteString("category", "o" + (i % 4));
                    json.WriteString("s", strings[(i * 5 + 1) % strings.Length]);

                    // t is a string, a JSON null, or nothing.
                    switch ((i * 7 + 3) % (strings.Length + 2))
                    {
                        case var n when n < strings.Length: json.WriteString("t", strings[n]); break;
                        case var n when n == strings.Length: json.WriteNull("t"); break;
                    }

                    Raw(json, "k", OrderSmallIntegers[(i * 3 + 1) % OrderSmallIntegers.Length]);
                    json.WriteBoolean("b", i % 3 == 1);
                    Raw(json, "v", OrderAnything[(i * 5 + 2) % OrderAnything.Length]);
                    Raw(json, "w", OrderIntegers[(i * 7 + 4) % OrderIntegers.Length]);
                    Raw(json, "d", OrderFractions[(i * 3 + 2) % OrderFractions.Length]);
                    json.WriteString("e", (ascii ? new[] { "a", "B", "\u00E9", "\u4E00" } : new[] { "a", "B", "\uE000", "\U0001F600" })[(i / 4 + i) % 4]);
                    json.WriteString("g", OrderUuids[(i * 5 + 3) % OrderUuids.Length]);
                    json.WriteString("at", OrderInstants[(i * 7 + 1) % OrderInstants.Length]);
                    json.WriteEndObject();
                }

                documents[i] = Encoding.UTF8.GetString(buffer.ToArray());
            }

            // Written in a permutation, so that no tie comes back in the order it was written by chance.
            return Enumerable.Range(0, OrderDocumentCount).Select(i => documents[i * 29 % OrderDocumentCount]).ToArray();

            static void Raw(Utf8JsonWriter json, string name, string? value)
            {
                if (value is null)
                    return;

                json.WritePropertyName(name);
                json.WriteRawValue(value);
            }
        }

        /// <summary>
        /// One container of the yardstick: its metadata, the policy Azure would hold it to, and the
        /// documents it is seeded with.
        /// </summary>
        sealed record OrderContainer(CosmosContainerMetadata Metadata, IndexingPolicy Policy, string[] Documents)
        {
            public string Name => Metadata.Name;

            /// <summary>Every string the documents hold, which a comparison with a parameter is made over.</summary>
            public IReadOnlyList<string> Strings { get; } = Documents
                .SelectMany(d => JsonDocument.Parse(d).RootElement.EnumerateObject())
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p => p.Value.GetString()!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            /// <summary>Each document by its partition key and id, which is what identifies a row.</summary>
            public IReadOnlyDictionary<(string, string), JsonElement> ById { get; } = Documents
                .Select(d => JsonDocument.Parse(d).RootElement)
                .ToDictionary(d => (d.GetProperty("category").GetString()!, d.GetProperty("id").GetString()!));
        }

        static readonly OrderContainer[] OrderContainers =
        [
            new(OrdersPlain, OrderIndexingPolicy(composites: false), OrderDocuments(ascii: false)),
            new(OrdersDeclared, OrderIndexingPolicy(composites: true), OrderDocuments(ascii: false)),
            new(OrdersAscii, OrderIndexingPolicy(composites: true), OrderDocuments(ascii: true)),
        ];

        static IReadOnlyList<(CosmosContainerMetadata Metadata, Container Container)>? _orderContainers;

        static readonly SemaphoreSlim _orderSeeding = new(1, 1);

        /// <summary>
        /// Seeds the three containers once, the first time the yardstick runs: the class's other tests
        /// never read them, and a run that does not ask for the yardstick does not pay for them.
        /// </summary>
        static async Task<IReadOnlyList<(CosmosContainerMetadata Metadata, Container Container)>> OrderContainersSeeded()
        {
            await _orderSeeding.WaitAsync();
            try
            {
                if (_orderContainers is null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var database = _client!.GetDatabase(DatabaseName);

                    var seeded = new List<(CosmosContainerMetadata, Container)>();
                    foreach (var container in OrderContainers)
                        seeded.Add((container.Metadata, await Seed(database, container.Name, container.Documents, cts.Token, policy: container.Policy)));

                    _orderContainers = seeded;
                }

                return _orderContainers;
            }
            finally
            {
                _orderSeeding.Release();
            }
        }

        // ── The statements ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// An expression a statement orders by, the path it reads where it reads one as it is, and
        /// what kind of reading it is.
        /// </summary>
        sealed record OrderExpression(string Sql, string? Path, string Kind)
        {
            public override string ToString() => Sql;
        }

        /// <summary>One key of an <c>ORDER BY</c>: its expression, direction and placement as written.</summary>
        /// <param name="Expression">What it orders by.</param>
        /// <param name="Descending">Whether it is <c>DESC</c>.</param>
        /// <param name="NullsFirst">The placement written, or <c>null</c> for none, which is
        /// <c>NULLS LAST</c> ascending and <c>NULLS FIRST</c> descending.</param>
        sealed record OrderTerm(OrderExpression Expression, bool Descending, bool? NullsFirst)
        {
            public CalciteSortKey Key(int field) => new(field, Descending, NullsFirst ?? Descending);

            public string Sql(string target) => target + (Descending ? " DESC" : "") + (NullsFirst switch { true => " NULLS FIRST", false => " NULLS LAST", null => "" });
        }

        /// <summary>A conjunct of a <c>WHERE</c>, and the value its <c>?</c> is bound to where it has one.</summary>
        sealed record OrderConjunct(string Sql, bool Bound = false, object? Parameter = null)
        {
            public override string ToString() => Bound ? $"{Sql} [? = {OrderAllowability.Show(Parameter)}]" : Sql;
        }

        enum OrderForm
        {
            /// <summary><c>SELECT … ORDER BY … [OFFSET] [FETCH]</c>.</summary>
            Sort,

            /// <summary><c>SELECT DISTINCT keys … ORDER BY …</c>.</summary>
            Distinct,

            /// <summary>A subquery's <c>ORDER BY … FETCH</c>, read by a parent that asks no order.</summary>
            Inner,

            /// <summary><c>… UNION ALL … ORDER BY</c>.</summary>
            Union,

            Min,

            Max,

            /// <summary>A filter whose answer depends on how the service compares: its rows, exactly.</summary>
            Rows,
        }

        /// <summary>
        /// A generated statement, kept as its parts so that it can be rendered two ways — as written,
        /// and as the input R it is judged over — and narrowed.
        /// </summary>
        sealed record OrderDraft(OrderContainer Container, OrderForm Form, IReadOnlyList<OrderTerm> Terms, bool SelectsKeys, IReadOnlyList<OrderConjunct> Where, IReadOnlyList<OrderConjunct> Other, int Offset, int? Fetch, bool Keyset = false)
        {

            const string Identity = "c.\"id\", JSON_VALUE(c.\"DOC\", '$.category')";

            string Table => Container.Name;

            static string Filter(IReadOnlyList<OrderConjunct> where) => where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where.Select(w => "(" + w.Sql + ")"));

            string Page => (Offset > 0 ? $" OFFSET {Offset} ROWS" : "") + (Fetch is int f ? $" FETCH NEXT {f} ROWS ONLY" : "");

            string OrderBy(Func<int, OrderTerm, string> target) => Terms.Count == 0 ? "" : " ORDER BY " + string.Join(", ", Terms.Select((t, i) => t.Sql(target(i, t))));

            string Keys => string.Join(", ", Terms.Select(t => t.Expression.Sql));

            /// <summary>The values the statement's <c>?</c> are bound to, in the order they are written.</summary>
            public IReadOnlyList<object?> Parameters => Where.Concat(Other).Where(w => w.Bound).Select(w => w.Parameter).ToList();

            /// <summary>The statement as written.</summary>
            public string Sql => Form switch
            {
                OrderForm.Sort => $"SELECT {Identity}{(SelectsKeys ? ", " + Keys : "")} FROM {Table} AS c{Filter(Where)}{OrderBy((_, t) => t.Expression.Sql)}{Page}",
                OrderForm.Distinct => $"SELECT DISTINCT {Keys} FROM {Table} AS c{Filter(Where)}{OrderBy((i, _) => (i + 1).ToString(CultureInfo.InvariantCulture))}{Page}",
                OrderForm.Inner => $"SELECT x.\"id\", x.\"pk\" FROM (SELECT c.\"id\" AS \"id\", JSON_VALUE(c.\"DOC\", '$.category') AS \"pk\", " +
                    string.Join(", ", Terms.Select((t, i) => $"{t.Expression.Sql} AS \"k{i}\"")) + $" FROM {Table} AS c{Filter(Where)}{OrderBy((i, _) => $"\"k{i}\"")}{Page}) AS x",
                OrderForm.Union => $"{Branch(Where)} UNION ALL {Branch(Other)}{OrderBy((_, _) => "3")}{Page}",
                OrderForm.Min or OrderForm.Max => $"SELECT {(Form == OrderForm.Min ? "MIN" : "MAX")}({Keys}) FROM {Table} AS c{Filter(Where)}",
                _ => $"SELECT {Identity} FROM {Table} AS c{Filter(Where)}",
            };

            /// <summary>One side of a union: a row's identity and its one key.</summary>
            string Branch(IReadOnlyList<OrderConjunct> where) =>
                $"SELECT c.\"id\" AS \"id\", JSON_VALUE(c.\"DOC\", '$.category') AS \"pk\", {Keys} AS \"k\" FROM {Table} AS c{Filter(where)}";

            /// <summary>R: what the statement orders, read in process, its keys beside its columns.</summary>
            public string InputSql => Form switch
            {
                OrderForm.Sort or OrderForm.Inner => $"SELECT {Identity}{(SelectsKeys && Form == OrderForm.Sort ? ", " + Keys : "")}{(Terms.Count > 0 ? ", " + Keys : "")} FROM {Table} AS c{Filter(Where)}",
                OrderForm.Distinct => $"SELECT DISTINCT {Keys} FROM {Table} AS c{Filter(Where)}",
                OrderForm.Union => $"{Branch(Where)} UNION ALL {Branch(Other)}",
                OrderForm.Min or OrderForm.Max => $"SELECT {Keys} FROM {Table} AS c{Filter(Where)}",
                _ => Sql,
            };

            /// <summary>How many leading columns of R the answer's rows hold.</summary>
            public int Width => Form switch
            {
                OrderForm.Sort => 2 + (SelectsKeys ? Terms.Count : 0),
                OrderForm.Inner or OrderForm.Rows => 2,
                OrderForm.Distinct => Terms.Count,
                OrderForm.Union => 3,
                _ => 1,
            };

            /// <summary>The collation, over R's columns.</summary>
            public IReadOnlyList<CalciteSortKey> Collation => Form switch
            {
                OrderForm.Distinct => Terms.Select((t, i) => t.Key(i)).ToList(),
                OrderForm.Union => [Terms[0].Key(2)],
                OrderForm.Rows => [],
                _ => Terms.Select((t, i) => t.Key(Width + i)).ToList(),
            };

            /// <summary>
            /// The collation over the answer's own columns, where the answer shows every key: a sort that
            /// selects its keys, a <c>DISTINCT</c> and a union; otherwise <c>null</c>.
            /// </summary>
            public IReadOnlyList<CalciteSortKey>? Shown => Form switch
            {
                OrderForm.Sort when SelectsKeys && Terms.Count > 0 => Terms.Select((t, i) => t.Key(2 + i)).ToList(),
                OrderForm.Distinct => Collation,
                OrderForm.Union => Collation,
                _ => null,
            };

            /// <summary>Whether R's first two columns are a row's identity, its id and partition key.</summary>
            public bool Identified => Form is OrderForm.Sort or OrderForm.Inner or OrderForm.Union or OrderForm.Rows;

            /// <summary>
            /// The smaller statements a failure is narrowed through, in the order they are asked: the
            /// page dropped, then each key, then each conjunct.
            /// </summary>
            public IEnumerable<OrderDraft> Smaller()
            {
                if ((Offset > 0 || Fetch is not null) && Form != OrderForm.Inner)
                    yield return this with { Offset = 0, Fetch = null };

                if (Terms.Count > 1)
                    for (var i = 0; i < Terms.Count; i++)
                        yield return this with { Terms = Terms.Where((_, j) => j != i).ToList() };

                for (var i = 0; i < Where.Count; i++)
                    if (Form != OrderForm.Rows || Where.Count > 1)
                        yield return this with { Where = Where.Where((_, j) => j != i).ToList() };

                for (var i = 0; i < Other.Count; i++)
                    yield return this with { Other = Other.Where((_, j) => j != i).ToList() };
            }

            public override string ToString() => Sql + (Parameters.Count > 0 ? "  [" + string.Join(", ", Parameters.Select(OrderAllowability.Show)) + "]" : "");

        }

        /// <summary>
        /// Draws statements: the forms whose answers depend on an order, over keys of every reading the
        /// adapter has, each with a direction, a placement and sometimes a page.
        /// </summary>
        sealed class OrderGenerator(Random random)
        {

            static OrderExpression Text(string path) => new($"JSON_VALUE(c.\"DOC\", '$.{path}')", path, "text");

            static readonly OrderExpression Id = new("c.\"id\"", "id", "id");

            /// <summary>
            /// Every key: text accessors, every <c>RETURNING</c> reading, the service's own properties,
            /// monotone and other computed keys, casts to <c>UUID</c> and <c>TIMESTAMP</c>, a JSON
            /// fragment, and the promoted column. The common ones twice, to be asked about twice as often.
            /// </summary>
            static readonly OrderExpression[] Keys =
            [
                Text("s"), Text("s"), Text("t"), Text("t"), Text("e"), Text("v"), Text("g"),
                Id, Id,
                new("c.\"_etag\"", "_etag", "etag"),
                new("c.\"_ts\"", "_ts", "timestamp"),
                new("JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER)", "k", "integer"),
                new("JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER)", "k", "integer"),
                new("JSON_VALUE(c.\"DOC\", '$.w' RETURNING BIGINT)", "w", "bigint"),
                new("JSON_VALUE(c.\"DOC\", '$.w' RETURNING BIGINT)", "w", "bigint"),
                new("JSON_VALUE(c.\"DOC\", '$.d' RETURNING DOUBLE)", "d", "double"),
                new("JSON_VALUE(c.\"DOC\", '$.b' RETURNING BOOLEAN)", "b", "boolean"),
                new("(JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER) + 1)", null, "monotone"),
                new("-JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER)", null, "monotone"),
                new("CAST(JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER) AS DOUBLE)", null, "monotone"),
                new("CAST(JSON_VALUE(c.\"DOC\", '$.s') AS VARCHAR(2))", null, "monotone"),
                new("COALESCE(JSON_VALUE(c.\"DOC\", '$.t'), '')", null, "monotone"),
                new("MOD(JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER), 3)", null, "computed"),
                new("UPPER(JSON_VALUE(c.\"DOC\", '$.s'))", null, "computed"),
                new("CHAR_LENGTH(JSON_VALUE(c.\"DOC\", '$.s'))", null, "computed"),
                new("CAST(JSON_VALUE(c.\"DOC\", '$.g') AS UUID)", null, "cast"),
                new("CAST(JSON_VALUE(c.\"DOC\", '$.at') AS TIMESTAMP)", null, "cast"),
                new("JSON_QUERY(c.\"DOC\", '$.v')", null, "json"),
            ];

            /// <summary>Keys no document holds a null at, which a keyset can page by.</summary>
            static readonly OrderExpression[] Present = [Text("s"), Id, Text("e")];

            static readonly OrderExpression[] TextOperands = [Text("s"), Text("t"), Text("e"), Text("v"), Id];

            /// <summary>Literals under the default charset, which holds nothing at or above U+0100.</summary>
            static readonly string[] TextLiterals = ["a", "B", "ab", "_", "1", "\u00E9", "30", "true", "a b"];

            static readonly string[] Numbers = ["JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER)", "JSON_VALUE(c.\"DOC\", '$.w' RETURNING BIGINT)"];

            static readonly string[] NumberLiterals = ["0", "3", "2147483648", "9007199254740993", "1152921504606846976"];

            /// <summary>What the ternary logic gate's generator may say of these containers, one connective deep.</summary>
            static readonly Vocabulary Words = new(
                "",
                [],
                ["JSON_VALUE(c.\"DOC\", '$.s')", "JSON_VALUE(c.\"DOC\", '$.t')", "JSON_VALUE(c.\"DOC\", '$.e')", "JSON_VALUE(c.\"DOC\", '$.v')", "c.\"id\"", "CAST(c.\"$.category\" AS VARCHAR)"],
                ["JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER)"],
                ["a", "B", "ab", "_", "1", "\u00E9", "30", "true", "o1"],
                ["0", "1", "3", "10"],
                ["a%", "%b", "_", "%"]);

            readonly PredicateGenerator _predicates = new(random, depth: 1);

            T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];

            static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

            public OrderDraft Next(OrderContainer container) => random.Next(21) switch
            {
                20 => Led(container),
                < 9 => Sort(container, OrderForm.Sort, Count(3)),
                9 or 10 => Sort(container, OrderForm.Distinct, Count(2)),
                11 => Inner(container),
                12 or 13 => Sort(container, OrderForm.Union, 1) with { Other = Conjuncts(1, 1, []) },
                14 => Extreme(container, OrderForm.Min),
                15 => Extreme(container, OrderForm.Max),
                16 => new OrderDraft(container, OrderForm.Sort, [new(Pick(Present), random.Next(2) == 0, null)], true, [], [], 0, 3, Keyset: true),
                17 => new OrderDraft(container, OrderForm.Rows, [], false, [Bound(Pick(TextOperands).Sql, random.Next(2) == 0 ? ">" : "<")], [], 0, null),
                18 => new OrderDraft(container, OrderForm.Rows, [], false, [new($"{Pick(TextOperands).Sql} {(random.Next(2) == 0 ? ">" : "<=")} {Pick(TextOperands).Sql}")], [], 0, null),
                _ => Pinned(container),
            };

            /// <summary>A subquery's sort, which only a page keeps: Calcite drops one with none.</summary>
            OrderDraft Inner(OrderContainer container)
            {
                var inner = Sort(container, OrderForm.Inner, Count(2));
                return inner.Fetch is null ? inner with { Fetch = random.Next(1, 5) } : inner;
            }

            /// <summary>
            /// A sort led by a key that is null in one document and absent in the next, under the
            /// service's own placement, before a key every document holds: Calcite ties the two nulls,
            /// and only the second key may order them.
            /// </summary>
            OrderDraft Led(OrderContainer container)
            {
                var descending = random.Next(2) == 0;
                var nullable = random.Next(2) == 0 ? Text("t") : new OrderExpression("JSON_VALUE(c.\"DOC\", '$.k' RETURNING INTEGER)", "k", "integer");
                var unique = random.Next(2) == 0 ? Id : Text("s");
                int? fetch = random.Next(2) == 0 ? random.Next(1, 5) : null;

                return new OrderDraft(container, OrderForm.Sort, [new(nullable, descending, descending == false), new(unique, random.Next(2) == 0, null)], random.Next(2) == 0, [], [], 0, fetch);
            }

            /// <summary>A sort led by the promoted column, which a filter pins to one value.</summary>
            OrderDraft Pinned(OrderContainer container)
            {
                var sort = Sort(container, OrderForm.Sort, 1);
                return sort with
                {
                    Terms = [new(new("c.\"$.category\"", "category", "variant"), false, null), .. sort.Terms],
                    Where = [new("c.\"$.category\" = 'o1'"), .. sort.Where],
                };
            }

            int Count(int most) => most == 1 ? 1 : random.NextDouble() switch { < 0.5 => 1, < 0.85 => 2, _ => Math.Min(3, most) };

            OrderDraft Sort(OrderContainer container, OrderForm form, int count)
            {
                var terms = new List<OrderTerm>();
                while (terms.Count < count)
                {
                    var key = Pick(Keys);
                    if (terms.Any(t => t.Expression.Sql == key.Sql))
                        continue;

                    terms.Add(new OrderTerm(key, random.Next(2) == 0, random.Next(4) switch { 0 => true, 1 => false, _ => null }));
                }

                var offset = random.Next(3) == 0 ? random.Next(1, 5) : 0;
                int? fetch = random.Next(2) == 0 ? random.Next(0, 5) : null;

                return new OrderDraft(container, form, terms, random.Next(2) == 0, Conjuncts(0, 2, terms), [], offset, fetch);
            }

            OrderDraft Extreme(OrderContainer container, OrderForm form)
            {
                var key = Pick(Keys.Where(k => k.Kind is not ("json" or "variant")).Append(Id).Append(new("c.\"_etag\"", "_etag", "etag")).ToList());
                return new OrderDraft(container, form, [new OrderTerm(key, false, null)], false, Conjuncts(0, 1, []), [], 0, null);
            }

            /// <summary>A conjunct comparing an operand with a parameter bound to a string of any character.</summary>
            OrderConjunct Bound(string operand, string comparison) => new($"{operand} {comparison} ?", true, Pick(OrderStrings));

            /// <summary>
            /// Between <paramref name="least"/> and <paramref name="most"/> conjuncts, over the keys
            /// half the time: a null test, an equality, a membership, a range against a literal or a
            /// parameter, a <c>LIKE</c>, a partition pinned, a number's range, a boolean, or the ternary
            /// logic gate's predicate.
            /// </summary>
            List<OrderConjunct> Conjuncts(int least, int most, IReadOnlyList<OrderTerm> terms)
            {
                var count = random.Next(least, most + 1);
                var conjuncts = new List<OrderConjunct>();

                for (var i = 0; i < count; i++)
                {
                    var text = terms.Count > 0 && random.Next(2) == 0 && terms[random.Next(terms.Count)].Expression is { Kind: "text" or "id" } key ? key.Sql : Pick(TextOperands).Sql;
                    var any = terms.Count > 0 && random.Next(2) == 0 ? terms[random.Next(terms.Count)].Expression.Sql : text;

                    conjuncts.Add(random.Next(10) switch
                    {
                        0 => new($"{any} IS NOT NULL"),
                        1 => new($"{text} = {Quote(Pick(TextLiterals))}"),
                        2 => new($"{text} IN ({Quote(Pick(TextLiterals))}, {Quote(Pick(TextLiterals))})"),
                        3 => new($"{text} > {Quote(Pick(TextLiterals))}"),
                        4 => Bound(text, random.Next(2) == 0 ? ">" : "<"),
                        5 => new($"{text} LIKE 'a%'"),
                        6 => new($"c.\"$.category\" = '{Pick(new[] { "o0", "o1", "o2", "o3" })}'"),
                        7 => new($"{Pick(Numbers)} > {Pick(NumberLiterals)}"),
                        8 => new(random.Next(2) == 0 ? "JSON_VALUE(c.\"DOC\", '$.b' RETURNING BOOLEAN)" : "NOT JSON_VALUE(c.\"DOC\", '$.b' RETURNING BOOLEAN)"),
                        _ => new(_predicates.Next(Words).Sql),
                    });
                }

                return conjuncts;
            }

        }

        // ── The judgement ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What one statement came to.
        /// </summary>
        /// <param name="Draft">The statement.</param>
        /// <param name="Judged">Whether it was judged: <c>false</c> where Calcite raises over its input,
        /// or its answer could not be matched to it.</param>
        /// <param name="Failure">What is wrong, or <c>null</c>.</param>
        /// <param name="Family">The family a failure belongs to.</param>
        /// <param name="Runtime">Whether the failure is the CLR runtime's own: the service was handed no
        /// order, and the answer holds the right rows in an order the runtime chose.</param>
        /// <param name="Sent">What the plan sent the service.</param>
        /// <param name="Answer">The rows the pushed plan answered, which a keyset's next page is bound
        /// from.</param>
        sealed record OrderJudgement(OrderDraft Draft, bool Judged, string? Failure, string? Family, bool Runtime, IReadOnlyList<string> Sent, IReadOnlyList<object?[]>? Answer)
        {
            public bool Failed => Judged && Failure is not null && Runtime == false;

            /// <summary>Whether the service was handed an order: a sort or an extreme.</summary>
            public bool Pushed => Sent.Any(s => s.Contains(" ORDER BY ", StringComparison.Ordinal) || ExtremeSent.IsMatch(s));
        }

        static readonly Regex ExtremeSent = new(@"\b(MIN|MAX)\(", RegexOptions.Compiled);

        /// <summary>A top-level accessor in a generated statement.</summary>
        static readonly Regex Accessor = new(@"'\$\.(?<path>[A-Za-z_]+)'", RegexOptions.Compiled);

        /// <summary>The service's string functions that count code points, where Calcite's count UTF-16 units.</summary>
        static readonly Regex CodePointFunctions = new(@"\b(LENGTH|SUBSTRING|LEFT|RIGHT|INDEX_OF|REVERSE)\(", RegexOptions.Compiled);

        static readonly Regex OrderBySent = new(@" ORDER BY (?!RANK )(?<items>.*?)(?= OFFSET |$)", RegexOptions.Compiled);

        static readonly Regex PathSent = new(@"^c(?<segment>\.[A-Za-z_][A-Za-z0-9_]*|\[""[^""]*""\])+$", RegexOptions.Compiled);

        /// <summary>
        /// Holds every <c>ORDER BY</c> a plan sends to what Azure accepts under a container's indexing
        /// policy, and describes the first it refuses, or returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Measured on the probe account: an <c>ORDER BY</c> over a path the policy excludes is a 400,
        /// <c>/"_etag"/?</c> being excluded by default; an expression is a 400; and so is any multi-key
        /// order, a repeated key included, that no composite index serves in its directions or their
        /// exact reverse. The emulator accepts all three.
        /// </remarks>
        static string? Acceptance(IEnumerable<string> sent, IndexingPolicy policy)
        {
            foreach (var statement in sent)
                foreach (Match order in OrderBySent.Matches(statement))
                {
                    var keys = new List<(string Path, bool Descending)>();

                    foreach (var item in order.Groups["items"].Value.Split(", "))
                    {
                        var descending = item.EndsWith(" DESC", StringComparison.Ordinal);
                        var expression = item.EndsWith(" DESC", StringComparison.Ordinal) || item.EndsWith(" ASC", StringComparison.Ordinal) ? item[..item.LastIndexOf(' ')] : item;

                        var path = PathSent.Match(expression);
                        if (path.Success == false)
                            return $"excluded path: an ORDER BY over an expression, {expression}, which the service refuses";

                        var policyPath = string.Concat(path.Groups["segment"].Captures.Select(c => "/" + c.Value.TrimStart('.').Trim('[', ']', '"')));
                        if (policy.ExcludedPaths.Select(e => e.Path.Replace("\"", "")).Any(e => Covers(e, policyPath)))
                            return $"excluded path: an ORDER BY over {policyPath}, which the indexing policy excludes: a 400 on Azure";

                        keys.Add((policyPath, descending));
                    }

                    if (keys.Count > 1 && policy.CompositeIndexes.Any(index => Serves(index, keys)) == false)
                        return $"composite index: an ORDER BY over {string.Join(", ", keys.Select(k => k.Path + (k.Descending ? " DESC" : "")))} that no composite index serves: a 400 on Azure";
                }

            return null;

            static bool Covers(string pattern, string path) =>
                pattern.EndsWith("/?", StringComparison.Ordinal) ? pattern[..^2] == path
                : pattern.EndsWith("/*", StringComparison.Ordinal) && (pattern[..^2] == path || path.StartsWith(pattern[..^2] + "/", StringComparison.Ordinal));

            static bool Serves(Collection<CompositePath> index, List<(string Path, bool Descending)> keys)
            {
                if (index.Count != keys.Count)
                    return false;

                for (var i = 0; i < keys.Count; i++)
                    if (index[i].Path != keys[i].Path)
                        return false;

                bool Same(int i) => (index[i].Order == CompositePathSortOrder.Descending) == keys[i].Descending;
                return Enumerable.Range(0, keys.Count).All(Same) || Enumerable.Range(0, keys.Count).All(i => Same(i) == false);
            }
        }

        /// <summary>
        /// Runs and judges statements over the yardstick's containers, asking each statement of each
        /// once however often it comes up — the generator repeats itself, and narrowing asks again.
        /// </summary>
        sealed class OrderJudge(IReadOnlyList<(CosmosContainerMetadata Metadata, Container Container)> containers)
        {

            readonly ConcurrentDictionary<string, Lazy<Task<(List<object?[]>? Rows, Exception? Raised, IReadOnlyList<string> Sent)>>> _runs = new();

            readonly ConcurrentDictionary<string, Lazy<Task<OrderJudgement>>> _judged = new();

            public int Statements => _runs.Count;

            Task<(List<object?[]>? Rows, Exception? Raised, IReadOnlyList<string> Sent)> Execute(string sql, bool pushdown, IReadOnlyList<object?> parameters)
            {
                var key = (pushdown ? "pushed " : "input ") + sql + " | " + string.Join(", ", parameters.Select(OrderAllowability.Show));
                return _runs.GetOrAdd(key, _ => new(async () =>
                {
                    var sent = new List<string>();
                    try
                    {
                        var rows = await Run(sql, pushdown, sent, containers, parameters);
                        return (rows.Select(r => r as object?[] ?? [r]).ToList(), null, sent);
                    }
                    catch (Exception e) when (e is not Xunit.Sdk.SkipException)
                    {
                        return (null, e, sent);
                    }
                })).Value;
            }

            public Task<OrderJudgement> Judge(OrderDraft draft) => _judged.GetOrAdd(draft.ToString(), _ => new(() => JudgeCore(draft))).Value;

            async Task<OrderJudgement> JudgeCore(OrderDraft draft)
            {
                var parameters = draft.Parameters;
                var pushed = await Execute(draft.Sql, pushdown: true, parameters);
                var input = await Execute(draft.InputSql, pushdown: false, parameters);

                if (input.Raised is Exception raised)
                {
                    // An answer that shows its keys is held to its own order even so: no input makes
                    // rows allowable whose keys, as the answer itself shows them, run against the ORDER
                    // BY. That is what judges 2^63 stored as a double, which a whole document carries as
                    // written and Calcite's BIGINT cannot read, while a projection of it is printed to 17
                    // digits, as another integer, and ordered by the double.
                    if (pushed.Rows is { } rows && draft.Shown is { } shown
                        && OrderAllowability.Judge(rows, rows, draft.Width, shown, 0, null, ordered: true) is { Failed: true } own)
                    {
                        var judged = new OrderJudgement(draft, true, "out of its own order, where Calcite raises over the input: " + own.Failure, OwnFamily(own, shown), false, pushed.Sent, rows);
                        return judged.Pushed ? judged : judged with { Family = Runtime, Runtime = true };
                    }

                    return new(draft, false, "Calcite raises over the input: " + raised.Message.Split('\n')[0], null, false, pushed.Sent, null);
                }

                if (pushed.Raised is Exception failed)
                {
                    // Calcite may raise over the statement where it answers its input: a VARIANT key under
                    // two keys is not comparable. Any outcome refines that.
                    var oracle = await Execute(draft.Sql, pushdown: false, parameters);
                    if (oracle.Raised is not null)
                        return new(draft, false, "Calcite raises over the statement: " + oracle.Raised.Message.Split('\n')[0], null, false, pushed.Sent, null);

                    return new(draft, true, "the pushdown failed to run: " + failed.Message.Split('\n')[0], "the pushdown failed to run", false, pushed.Sent, null);
                }

                var policy = OrderContainers.Single(c => c.Name == draft.Container.Name).Policy;
                var refused = Acceptance(pushed.Sent, policy);

                var verdict = Verdict(draft, input.Rows!, pushed.Rows!);
                if (verdict.Judged == false && refused is null)
                    return new(draft, false, verdict.Reason, null, false, pushed.Sent, pushed.Rows);

                if (refused is not null)
                    return new(draft, true, refused + (verdict.Failed ? "; and " + verdict.Failure : ""), refused[..refused.IndexOf(':')], false, pushed.Sent, pushed.Rows);

                if (verdict.Failed == false)
                    return new(draft, true, null, null, false, pushed.Sent, pushed.Rows);

                var judgement = new OrderJudgement(draft, true, verdict.Failure, null, false, pushed.Sent, pushed.Rows);

                // Where the service was handed no order and the answer holds the right rows, the order
                // is the CLR runtime's: multi-key text by culture, or NaN first. A window of the wrong
                // rows is the runtime's only where the same statement unpaged holds the right ones; one a
                // filter beneath it lost rows from is the filter's.
                if (judgement.Pushed == false && IsOrderOnly(verdict))
                {
                    if (verdict.Failure!.StartsWith("(A3)", StringComparison.Ordinal) == false || draft.Form == OrderForm.Inner
                        || await Judge(draft with { Offset = 0, Fetch = null }) is { Failed: false })
                        return judgement with { Family = Runtime, Runtime = true };
                }

                return judgement with { Family = await Classify(draft, verdict, input.Rows!, pushed.Rows!, pushed.Sent) };
            }

            const string Runtime = "the CLR runtime's own order";

            /// <summary>Whether a failure is of the order alone, the answer holding rows of its input in the right number.</summary>
            static bool IsOrderOnly(OrderAllowability.Verdict verdict) =>
                verdict.Failure!.StartsWith("(A2)", StringComparison.Ordinal) || verdict.Failure.StartsWith("(A3)", StringComparison.Ordinal);

            /// <summary>
            /// The family of an answer out of its own order, by the key values it holds out of order:
            /// text from both sides of U+D800, or a number past 2^53.
            /// </summary>
            static string OwnFamily(OrderAllowability.Verdict own, IReadOnlyList<CalciteSortKey> shown)
            {
                if (own.Inversion is not (var before, var after))
                    return "out of its own order";

                var values = shown.SelectMany(k => new[] { before[k.Field], after[k.Field] }).ToList();
                if (HasBothBands(values))
                    return "text across U+D800";

                return values.Any(IsLarge) ? "a double read back as another integer" : "out of its own order";
            }

            /// <summary>Whether a value, a number or the text of one, lies past 2^53.</summary>
            static bool IsLarge(object? value) => value is not null
                && decimal.TryParse(value is string s ? s : CalciteOrder.Canonical(value).Split(':')[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && Math.Abs(n) >= 9007199254740992m;

            static OrderAllowability.Verdict Verdict(OrderDraft draft, List<object?[]> input, List<object?[]> answer) => draft.Form switch
            {
                OrderForm.Min or OrderForm.Max => answer.Count == 1
                    ? OrderAllowability.JudgeExtreme(input.Select(r => r[0]).ToList(), answer[0][0], draft.Form == OrderForm.Max)
                    : new OrderAllowability.Verdict(true, $"an aggregate answered {answer.Count} rows"),
                _ => OrderAllowability.Judge(input, answer, draft.Width, draft.Collation, draft.Offset, draft.Fetch, draft.Form is not (OrderForm.Inner or OrderForm.Rows)),
            };

            /// <summary>
            /// Names a failure's family from what its witness holds.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>Rows a comparison lost or gained</b> — an answer missing rows of its input, or holding
            /// rows it does not — are classified by what the rows that differ hold: a pushed string
            /// function, which counts code points where Calcite counts UTF-16 units; text from both
            /// sides of U+D800, in the rows, their documents or a parameter; or a double past 2^53, which
            /// the emulator prints to 17 digits in a projection where the document it returns whole keeps
            /// what was written — which is not an order's doing, and is named apart.
            /// </para>
            /// <para>
            /// <b>An order is asked again under another comparator.</b> An answer that would be
            /// allowable had Calcite compared text by code point, as a partition does, is the string
            /// order's. Otherwise the keys say: a key before the last whose path one document leaves
            /// absent and another holds null, which Calcite ties and the service does not; a path
            /// holding a double the service prints as another integer; a null where Calcite does not put
            /// it. A window that holds the wrong rows for none of those reasons is classified by the same
            /// statement unpaged, where a filter beneath the page lost them.
            /// </para>
            /// </remarks>
            async Task<string> Classify(OrderDraft draft, OrderAllowability.Verdict verdict, List<object?[]> input, List<object?[]> answer, IReadOnlyList<string> sent)
            {
                var unpaged = draft.Offset == 0 && draft.Fetch is null;

                if (draft.Form == OrderForm.Rows || IsOrderOnly(verdict) == false && (unpaged || verdict.Failure!.StartsWith("(A0)", StringComparison.Ordinal)))
                {
                    if (sent.Any(s => CodePointFunctions.IsMatch(s)))
                        return "a string function counts code points";

                    var differing = Differing(draft, input, answer, lost: unpaged);

                    // Not an order: a projection of a double past 2^53 is printed to 17 digits, where a
                    // document read whole comes back as it was written, on the emulator.
                    if (differing.SelectMany(r => r.Take(draft.Width)).Any(IsLarge))
                        return "a double printed two ways";

                    // What the filter compared, in the rows that differ, and what a parameter was bound
                    // to; or, for a parameter's comparison, every string the container holds.
                    var compared = Compared(draft);
                    var documents = draft.Identified ? differing.Select(r => Stored(draft.Container, r)).ToList() : [];
                    var values = differing.SelectMany(r => r.Take(draft.Width))
                        .Concat(documents.SelectMany(d => Strings(d, compared)))
                        .Concat(draft.Parameters)
                        .ToList();

                    if (HasBothBands(values) || draft.Parameters.OfType<string>().Any() && HasBothBands(draft.Parameters.Concat(draft.Container.Strings)))
                        return "text across U+D800";

                    return "rows";
                }

                if (draft.Form is OrderForm.Min or OrderForm.Max)
                    return HasBothBands(input.Select(r => r[0])) ? "text across U+D800" : "other";

                if (OrderAllowability.Judge(input, answer, draft.Width, draft.Collation, draft.Offset, draft.Fetch, draft.Form != OrderForm.Inner, ServiceOrderModel.CompareCodePoints).Failed == false)
                    return "text across U+D800";

                if (draft.Identified)
                {
                    var documents = input.Select(r => Stored(draft.Container, r)).ToList();
                    for (var i = 0; i + 1 < draft.Terms.Count; i++)
                        if (draft.Terms[i].Expression.Path is string path
                            && documents.Any(d => KindAt(d, path) == JsonValueKind.Undefined) && documents.Any(d => KindAt(d, path) == JsonValueKind.Null))
                            return "undefined and null on a leading key";

                    if (draft.Terms.Any(t => t.Expression.Path is string path && documents.Any(d => d.TryGetValue(path, out var v) && v.Kind == JsonValueKind.Number && IsPrintedAsAnother(v.Text))))
                        return "a double read back as another integer";
                }

                if (verdict.Inversion is (var before, var after) && draft.Collation.Any(k => before[k.Field] is null != after[k.Field] is null))
                    return "null placement";

                if (unpaged == false && draft.Form != OrderForm.Inner && await Judge(draft with { Offset = 0, Fetch = null }) is { Failed: true, Family: string family })
                    return family;

                // An order the service computed over text from both sides of the line, which the plan
                // carried on in process: a merge union reading a pushed sort, a page above it.
                if (sent.Any(s => s.Contains(" ORDER BY ", StringComparison.Ordinal)) && HasBothBands(input.SelectMany(r => draft.Collation.Select(k => r[k.Field]))))
                    return "text across U+D800";

                return "other";
            }

            /// <summary>
            /// The rows an answer holds more often than its input does, and, where the answer should be
            /// all of its input, the rows of the input it holds less often: by what the answer selects.
            /// </summary>
            static List<object?[]> Differing(OrderDraft draft, List<object?[]> input, List<object?[]> answer, bool lost)
            {
                string Row(object?[] row) => string.Join(" | ", row.Take(draft.Width).Select(CalciteOrder.Canonical));

                var held = input.GroupBy(Row).ToDictionary(g => g.Key, g => g.ToList());
                var differing = new List<object?[]>();

                foreach (var row in answer)
                {
                    if (held.TryGetValue(Row(row), out var same) && same.Count > 0)
                        same.RemoveAt(same.Count - 1);
                    else
                        differing.Add(row);
                }

                if (lost)
                    differing.AddRange(held.Values.SelectMany(rows => rows));

                return differing;
            }

            static JsonValueKind KindAt(Dictionary<string, (JsonValueKind Kind, string Text)> stored, string path) =>
                stored.TryGetValue(path, out var value) ? value.Kind : JsonValueKind.Undefined;

            /// <summary>Whether a number as written is a double the service prints as another integer.</summary>
            static bool IsPrintedAsAnother(string text) =>
                decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && Math.Abs(n) > long.MaxValue;

            /// <summary>The stored values of a row's document, by top-level property, absent ones absent.</summary>
            static Dictionary<string, (JsonValueKind Kind, string Text)> Stored(OrderContainer container, object?[] row)
            {
                if (row[0] is not string id || row[1] is not string partition || container.ById.TryGetValue((partition, id), out var document) == false)
                    return [];

                return document.EnumerateObject().ToDictionary(p => p.Name, p => (p.Value.ValueKind, p.Value.GetRawText()));
            }

            /// <summary>The strings a row's document holds at the given paths, for the characters a comparison may part over.</summary>
            static IEnumerable<object?> Strings(Dictionary<string, (JsonValueKind Kind, string Text)> stored, IReadOnlySet<string> paths) =>
                stored.Where(p => paths.Contains(p.Key) && p.Value.Kind == JsonValueKind.String).Select(p => (object?)JsonSerializer.Deserialize<string>(p.Value.Text));

            /// <summary>The top-level paths a statement's filters read: its accessors and <c>id</c>.</summary>
            static IReadOnlySet<string> Compared(OrderDraft draft)
            {
                var sql = string.Join(" ", draft.Where.Concat(draft.Other).Select(w => w.Sql));
                var paths = Accessor.Matches(sql).Select(m => m.Groups["path"].Value).ToHashSet(StringComparer.Ordinal);
                if (sql.Contains("c.\"id\"", StringComparison.Ordinal))
                    paths.Add("id");

                return paths;
            }

            /// <summary>The bands a string's units fall in: 1 below U+D800, 2 from U+E000, 4 a surrogate.</summary>
            static int Bands(string s) => s.Aggregate(0, (b, c) => b | (char.IsSurrogate(c) ? 4 : c >= '\uE000' ? 2 : 1));

            static bool HasBothBands(IEnumerable<object?> values) => (values.OfType<string>().Aggregate(0, (b, s) => b | Bands(s)) & 6) == 6;

            /// <summary>
            /// The smallest part of a failing statement that fails too, adapter failures only: the first
            /// smaller statement that does, narrowed in its turn.
            /// </summary>
            public async Task<OrderJudgement> Narrow(OrderJudgement failing)
            {
                foreach (var smaller in failing.Draft.Smaller())
                    if (await Judge(smaller) is { Failed: true } failed)
                        return await Narrow(failed);

                return failing;
            }

            /// <summary>
            /// Judges an item: one statement, or a keyset's three pages, each bound from the last row the
            /// page before it answered, and each judged on its own.
            /// </summary>
            public async Task<IReadOnlyList<OrderJudgement>> JudgeItem(OrderDraft draft)
            {
                var first = await Judge(draft);
                if (draft.Keyset == false)
                    return [first];

                var pages = new List<OrderJudgement> { first };
                var term = draft.Terms[0];

                while (pages.Count < 3 && pages[^1].Answer is { Count: > 0 } answer && answer[^1][2] is string last)
                {
                    var next = draft with { Where = [new OrderConjunct($"{term.Expression.Sql} {(term.Descending ? "<" : ">")} ?", true, last)] };
                    pages.Add(await Judge(next));
                }

                return pages;
            }

        }

        /// <summary>
        /// Every generated statement whose answer depends on an order answers one that Calcite's
        /// semantics allow, sends only what Azure accepts, and does not fail where Calcite answers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Explicit until the anchored read lands.</b> Before the order the service computes was
        /// asked in one place, the adapter pushed orders it could not prove and failed in four families:
        /// text either side of U+D800, over <c>id</c>, a declared string and their keysets and extremes;
        /// the two nulls of a leading key in a multi-key sort; 2^63 stored as a double; and
        /// <c>_etag</c>, which Azure refuses to order by. Asked once, no order it pushes fails, and what
        /// still fails is two families that are not orders: a projected double printed two ways, and
        /// <c>CHAR_LENGTH</c> pushed as <c>LENGTH</c>, which <c>TODO.md</c> records. See <c>DESIGN.md</c>
        /// under <em>Differential testing</em> for the counts. It becomes a default run of the class
        /// once those are fixed and the anchored read has raised the floors.
        /// </para>
        /// <para>
        /// <c>COSMOS_ORDER_GATE=1</c> asks for the gate's <see cref="GateOrderedItems"/>, of which the
        /// default <see cref="DefaultOrderedItems"/> are the first. <c>COSMOS_MEASUREMENT_LOG</c> names a
        /// file every failure is written to in full.
        /// </para>
        /// <para>
        /// <b>A floor per pushed kind is the gate's other half.</b> "As many as possible" is a number
        /// the gate holds: a stage that pushes orders records how many of the default run's statements
        /// push each kind in <see cref="PushedFloors"/>, and pushing fewer fails. The floors are the
        /// static question's: what it proves at plan time, before any proof completed as the rows arrive.
        /// </para>
        /// </remarks>
        [Fact(Explicit = true)]
        public async Task EveryOrderIsOneCalciteAllows()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var containers = await OrderContainersSeeded();

            var generator = new OrderGenerator(new Random(OrderSeed));
            var items = Enumerable.Range(0, OrderedItems).Select(i => generator.Next(OrderContainers[i % OrderContainers.Length])).ToList();

            var judge = new OrderJudge(containers);
            var judged = new IReadOnlyList<OrderJudgement>[items.Count];
            var clock = Stopwatch.StartNew();

            var done = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = PartitionParallelism }, async (i, _) =>
            {
                judged[i] = await judge.JudgeItem(items[i]);

                if (Interlocked.Increment(ref done) % 50 == 0)
                    Log($"orders: judged {done} of {items.Count} in {clock.Elapsed.TotalSeconds:F0} s");
            });

            var judging = clock.Elapsed;
            var all = judged.SelectMany(j => j).ToList();
            var failing = all.Where(j => j.Failed).ToList();
            var narrowed = new OrderJudgement[failing.Count];

            await Parallel.ForEachAsync(Enumerable.Range(0, failing.Count), new ParallelOptions { MaxDegreeOfParallelism = PartitionParallelism }, async (i, _) =>
            {
                narrowed[i] = await judge.Narrow(failing[i]);
            });

            var pushedKinds = all.Where(j => j.Judged).GroupBy(PushedKind).ToDictionary(g => g.Key, g => g.Count());
            var families = failing.GroupBy(f => f.Family ?? "other").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
            var runtime = all.Where(j => j.Runtime).ToList();

            var size = items.Count == GateOrderedItems ? "the gate's" : "the first of the gate's " + GateOrderedItems + "; COSMOS_ORDER_GATE=1 asks for all of them";
            var summary = $"{items.Count} items ({size}), {all.Count} statements, {all.Count(j => j.Judged == false)} not judged, " +
                $"pushed {string.Join(", ", pushedKinds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"))}; " +
                $"{failing.Count} failing ({string.Join(", ", families)}); {runtime.Count} the CLR runtime's own; " +
                $"{judge.Statements} runs; judged in {judging.TotalSeconds:F1} s, narrowed in {(clock.Elapsed - judging).TotalSeconds:F1} s";

            // One entry per smallest witness, most statements first.
            var witnesses = Enumerable.Range(0, failing.Count)
                .GroupBy(i => narrowed[i].Draft.ToString())
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"[{narrowed[g.First()].Family}] {g.Key}\n  the smallest failing part of {g.Count()} statement(s), among them {failing[g.First()].Draft}\n  " +
                    narrowed[g.First()].Failure + "\n  sent: " + string.Join(" || ", narrowed[g.First()].Sent))
                .ToList();

            Log("## EveryOrderIsOneCalciteAllows: " + summary);
            Trace(all);
            foreach (var witness in witnesses)
                Log("### " + witness);
            foreach (var own in runtime)
                Log($"### the CLR runtime's own: {own.Draft}\n  {own.Failure}");

            var floors = PushedFloors.Where(f => pushedKinds.GetValueOrDefault(f.Kind) < (items.Count == GateOrderedItems ? f.Gate : f.Default))
                .Select(f => $"{f.Kind}: {pushedKinds.GetValueOrDefault(f.Kind)} pushed, the floor is {(items.Count == GateOrderedItems ? f.Gate : f.Default)}")
                .ToList();

            witnesses.Concat(floors).Should().BeEmpty($"every order the service computes must be one Calcite allows, and as many must push as did ({summary}; {witnesses.Count} distinct smallest witnesses)");
        }

        /// <summary>
        /// Writes every statement's judgement, one line each, to the file <c>COSMOS_ORDER_TRACE</c>
        /// names, where it names one: what was pushed and what came of it, for comparing two commits.
        /// </summary>
        static void Trace(IEnumerable<OrderJudgement> judgements)
        {
            var trace = Environment.GetEnvironmentVariable("COSMOS_ORDER_TRACE");
            if (string.IsNullOrEmpty(trace))
                return;

            static string Cell(string? text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ');

            System.IO.File.WriteAllLines(trace, judgements.Select(j => string.Join('\t',
                j.Draft.Container.Name, j.Draft.Form, PushedKind(j), j.Judged ? (j.Failed ? "failed" : j.Runtime ? "runtime" : "allowable") : "not judged",
                Cell(j.Family), Cell(j.Failure), Cell(j.Draft.ToString()), Cell(string.Join(" || ", j.Sent)))));
        }

        /// <summary>
        /// What a statement had the service compute, for the floors: a sort, an extreme, a page with no
        /// order, or nothing.
        /// </summary>
        static string PushedKind(OrderJudgement judgement) =>
            judgement.Sent.Any(s => s.Contains(" ORDER BY ", StringComparison.Ordinal)) ? "sort"
            : judgement.Sent.Any(s => ExtremeSent.IsMatch(s)) ? "extreme"
            : judgement.Sent.Any(s => s.Contains(" OFFSET ", StringComparison.Ordinal)) ? "page"
            : "in process";

        /// <summary>
        /// The least number of the yardstick's statements each kind must push, over the default run and
        /// over the gate: as many as the static question proved when it was first asked, measured on
        /// the emulator. Each stage that changes what pushes records its counts here.
        /// </summary>
        static readonly (string Kind, int Default, int Gate)[] PushedFloors =
        [
            ("sort", 14, 64),
            ("extreme", 3, 14),
            ("page", 0, 2),
        ];

    }

}
