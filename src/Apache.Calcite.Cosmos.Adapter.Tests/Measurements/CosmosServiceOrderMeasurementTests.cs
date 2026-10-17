using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json.Linq;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the service does with an order, measured: the facts <see cref="CosmosServiceOrder"/> and the sort's acceptance
    /// rest on, one test each, each a row of <c>CosmosAxioms</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Against whatever account the suite resolves</b> — see <see cref="CosmosEmulator"/>. Where the emulator and Azure
    /// agree a test asserts on both. Where they part — Azure refuses an <c>ORDER BY</c> over an excluded path, compares two
    /// constants by UTF-16, refuses a direction on <c>ORDER BY RANK</c> — the test <em>detects</em> the emulator's
    /// answer and skips rather than assuming it from the endpoint, so that an emulator that starts behaving as Azure does
    /// makes it assert. Measured on the probe account on 2026-10-16.
    /// </para>
    /// <para>
    /// <b>One partition.</b> The merge of two partitions needs a container past 10,000 RU/s, which no test provisions:
    /// <see cref="CosmosOrderMergeMeasurementTests"/> reads one a caller names. Every container here shares the
    /// database's 400 RU/s, and the database is deleted when the class ends.
    /// </para>
    /// </remarks>
    public class CosmosServiceOrderMeasurementTests : IClassFixture<CosmosServiceOrderMeasurementTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public ValueTask DisposeAsync() { Cleanup(); return default; }

        }

        static readonly string DatabaseName = "calcite_cosmos_order_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>The strings, either side of U+D800, and the two orders' disagreement among them.</summary>
        static readonly (string Id, string S)[] Strings =
        [
            ("low", "퟿"), ("a", "a"), ("B", "B"), ("e000", ""), ("ff21", "Ａ"), ("smile", "\U0001F600"), ("empty", ""),
        ];

        /// <summary>One value of each JSON type, and nothing, in the service's type order.</summary>
        static readonly string[] Types =
        [
            """{"id":"absent","pk":"a"}""",
            """{"id":"null","pk":"a","v":null}""",
            """{"id":"false","pk":"a","v":false}""",
            """{"id":"true","pk":"a","v":true}""",
            """{"id":"minus","pk":"a","v":-1}""",
            """{"id":"half","pk":"a","v":1.5}""",
            """{"id":"blank","pk":"a","v":""}""",
            """{"id":"word","pk":"a","v":"a"}""",
            """{"id":"array","pk":"a","v":[1]}""",
            """{"id":"object","pk":"a","v":{"a":1}}""",
        ];

        /// <summary>Integers past 2^53, where a double ties neighbours, up to 2^63 - 1.</summary>
        static readonly string[] Numbers =
        [
            """{"id":"p53","pk":"a","v":9007199254740992}""",
            """{"id":"p53p1","pk":"a","v":9007199254740993}""",
            """{"id":"p53p2","pk":"a","v":9007199254740994}""",
            """{"id":"p63m2","pk":"a","v":9223372036854775806}""",
            """{"id":"p63m1","pk":"a","v":9223372036854775807}""",
        ];

        static CosmosClient? _client;
        static Database? _database;
        static string? _unavailable;

        static async Task Initialize()
        {
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 5 : 30),
            };

            if (CosmosEmulator.IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                if (CosmosEmulator.Endpoint is not string endpoint || CosmosEmulator.Key is not string key)
                {
                    _unavailable = "No account is reachable.";
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 10 : 120));
                _client = new CosmosClient(endpoint, key, options);

                try
                {
                    _database = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, 400, cancellationToken: cts.Token)).Database;
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.BadRequest)
                {
                    // A serverless account takes no throughput.
                    _database = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;
                }

                var strings = await Fresh(new ContainerProperties("strings", "/pk"), cts.Token);
                foreach (var (id, s) in Strings)
                    await Write(strings, new JObject { ["id"] = id, ["pk"] = "a", ["s"] = s, ["a"] = 1, ["text"] = id == "a" ? "cosmos cosmos cosmos" : id == "B" ? "cosmos" : "other" }.ToString(Newtonsoft.Json.Formatting.None), cts.Token);

                var types = await Fresh(new ContainerProperties("types", "/pk"), cts.Token);
                foreach (var json in Types)
                    await Write(types, json, cts.Token);

                var numbers = await Fresh(new ContainerProperties("numbers", "/pk"), cts.Token);
                foreach (var json in Numbers)
                    await Write(numbers, json, cts.Token);

                var excluding = new ContainerProperties("excluded", "/pk");
                excluding.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
                excluding.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/x/?" });
                var excluded = await Fresh(excluding, cts.Token);
                foreach (var (id, x) in new[] { ("X1", 1), ("X2", 3), ("X3", 2) })
                    await Write(excluded, $$"""{"id":"{{id}}","pk":"a","x":{{x}},"y":{{x}}}""", cts.Token);

                var computing = new ContainerProperties("computed", "/pk");
                computing.ComputedProperties.Add(new ComputedProperty { Name = "cp_lower", Query = "SELECT VALUE LOWER(c.s) FROM c" });
                computing.ComputedProperties.Add(new ComputedProperty { Name = "cp_neg", Query = "SELECT VALUE 0 - c.n FROM c" });
                computing.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
                computing.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/cp_lower/?" });
                var computed = await Fresh(computing, cts.Token);
                foreach (var (id, s, n) in new[] { ("P1", "Alpha", 1), ("P2", "beta", 2), ("P3", "Gamma", 3) })
                    await Write(computed, $$"""{"id":"{{id}}","pk":"a","s":"{{s}}","n":{{n}}}""", cts.Token);
            }
            catch (Exception e)
            {
                _unavailable = e.Message;
                _database = null;
            }
        }

        static async Task<Container> Fresh(ContainerProperties properties, CancellationToken cancellation)
        {
            try { await _database!.GetContainer(properties.Id).DeleteContainerAsync(cancellationToken: cancellation); } catch (CosmosException) { }
            return (await _database!.CreateContainerIfNotExistsAsync(properties, cancellationToken: cancellation)).Container;
        }

        static async Task Write(Container container, string json, CancellationToken cancellation)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            using var response = await container.CreateItemStreamAsync(stream, new PartitionKey("a"), cancellationToken: cancellation);
            response.EnsureSuccessStatusCode();
        }

        static void Cleanup()
        {
            try { _database?.DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }
            _client?.Dispose();
            _client = null;
            _database = null;
        }

        static Container Container(string name)
        {
            if (_database is null)
                Assert.Skip("These need a service. " + _unavailable);

            return _database!.GetContainer(name);
        }

        /// <summary>
        /// Runs a statement and answers its rows as JSON text, or the service's refusal.
        /// </summary>
        static async Task<(List<string>? Rows, CosmosException? Refused)> Run(Container container, QueryDefinition query)
        {
            var rows = new List<string>();

            try
            {
                using var iterator = container.GetItemQueryIterator<JToken>(query);
                while (iterator.HasMoreResults)
                    foreach (var row in await iterator.ReadNextAsync())
                        rows.Add(row.Type == JTokenType.String ? (string)row! : row.ToString(Newtonsoft.Json.Formatting.None));

                return (rows, null);
            }
            catch (CosmosException e)
            {
                return (null, e);
            }
        }

        static async Task<List<string>> Rows(Container container, string sql, params (string Name, object Value)[] parameters)
        {
            var query = new QueryDefinition(sql);
            foreach (var (name, value) in parameters)
                query = query.WithParameter(name, value);

            var (rows, refused) = await Run(container, query);
            refused.Should().BeNull(sql);
            return rows!;
        }

        static async Task Refuses(Container container, string sql)
        {
            var (_, refused) = await Run(container, new QueryDefinition(sql));

            refused.Should().NotBeNull(sql + " is refused");
            refused!.StatusCode.Should().Be(HttpStatusCode.BadRequest, sql);
        }

        static int CodePoints(string left, string right) => ServiceOrderModel.CompareCodePoints(left, right);

        // ── Strings ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A document's strings are ordered, compared and extremed by code point, with no collation: U+E000 below
        /// U+1F600, where Calcite's UTF-16 order puts it above.
        /// </summary>
        [Fact]
        public async Task ADocumentsStringsOrderByCodePoint()
        {
            var strings = Container("strings");
            var codePoint = Strings.Select(s => s.S).OrderBy(s => s, Comparer<string>.Create(CodePoints)).ToList();

            (await Rows(strings, "SELECT VALUE c.s FROM c ORDER BY c.s")).Should().Equal(codePoint);
            (await Rows(strings, "SELECT VALUE c.s FROM c ORDER BY c.s DESC")).Should().Equal(Enumerable.Reverse(codePoint));
            (await Rows(strings, "SELECT VALUE MAX(c.s) FROM c")).Should().Equal("\U0001F600");
            (await Rows(strings, "SELECT VALUE c.s FROM c WHERE c.s > @p ORDER BY c.s", ("@p", ""))).Should().Equal("Ａ", "\U0001F600");

            codePoint.Should().NotEqual(codePoint.OrderBy(s => s, StringComparer.Ordinal), "the strings are ones the two orders part over");
        }

        /// <summary>
        /// A lone surrogate written as a JSON escape is stored as U+FFFD on Azure, a unit of the high band: what the
        /// theory's rule of what is stored reads as data, so that a <c>pattern</c> admitting a surrogate proves nothing of
        /// the high band of what is stored.
        /// </summary>
        /// <remarks>
        /// The emulator refuses the write, with a 403, so there the measurement skips. Measured on the probe account on
        /// 2026-10-16.
        /// </remarks>
        [Fact]
        public async Task ALoneSurrogateIsStoredAsTheReplacementCharacter()
        {
            if (_database is null)
                Assert.Skip("These need a service. " + _unavailable);

            var stored = await Fresh(new ContainerProperties("stored", "/pk"), TestContext.Current.CancellationToken);

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"lone\",\"pk\":\"a\",\"s\":\"\\ud800\"}")))
            using (var response = await stored.CreateItemStreamAsync(stream, new PartitionKey("a"), cancellationToken: TestContext.Current.CancellationToken))
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    Assert.Skip("This service refuses a lone surrogate, as the emulator does; Azure stores it as U+FFFD.");
                else
                    response.EnsureSuccessStatusCode();

            (await Rows(stored, "SELECT VALUE c.s FROM c WHERE c.id = 'lone'")).Should().Equal("\uFFFD");
        }

        /// <summary>
        /// Two constants are compared by UTF-16 code unit on Azure, where a document's strings are compared by code point.
        /// </summary>
        [Fact]
        public async Task TwoConstantsCompareByUtf16()
        {
            var answer = await Rows(Container("strings"), "SELECT VALUE [@a < @b, @b < @a] FROM c WHERE c.id = 'a'", ("@a", ""), ("@b", "\U0001F600"));

            if (answer.Single() == "[true,false]")
                Assert.Skip("This service compares two constants by code point, as the emulator does; Azure compares them by UTF-16.");

            answer.Should().Equal("[false,true]");
        }

        /// <summary>
        /// A range over one path whose bounds the two orders rank apart keeps nothing as a filter on Azure, though the
        /// same predicate projected is true of rows between them by code point.
        /// </summary>
        [Fact]
        public async Task ARangeWhoseBoundsTheOrdersRankApartKeepsNothing()
        {
            var strings = Container("strings");
            (string, object)[] bounds = [("@lo", ""), ("@hi", "\U0001F600")];

            var filtered = await Rows(strings, "SELECT VALUE c.id FROM c WHERE c.s BETWEEN @lo AND @hi", bounds);
            if (filtered.Count > 0)
                Assert.Skip("This service keeps the rows between the bounds by code point, as the emulator does.");

            (await Rows(strings, "SELECT c.id, (c.s BETWEEN @lo AND @hi) AS r FROM c WHERE c.id = 'ff21'", bounds)).Single().Should().Contain("\"r\":true", "projected, the predicate is the code point one");
        }

        // ── Types and numbers ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The type order — undefined, null, false, true, numbers, strings, arrays, objects — with <c>DESC</c> its exact
        /// reverse.
        /// </summary>
        [Fact]
        public async Task TheTypeOrderAndItsReverse()
        {
            var types = Container("types");
            var order = Types.Select(json => (string)JObject.Parse(json)["id"]!).ToList();

            (await Rows(types, "SELECT VALUE c.id FROM c ORDER BY c.v")).Should().Equal(order);
            (await Rows(types, "SELECT VALUE c.id FROM c ORDER BY c.v DESC")).Should().Equal(Enumerable.Reverse(order));
        }

        /// <summary>
        /// An int64 is held and ordered exactly, past 2^53 where a double ties its neighbours, up to 2^63 - 1.
        /// </summary>
        [Fact]
        public async Task IntegersOrderExactly()
        {
            var numbers = Container("numbers");
            var order = Numbers.Select(json => (string)JObject.Parse(json)["id"]!).ToList();

            (await Rows(numbers, "SELECT VALUE c.id FROM c ORDER BY c.v")).Should().Equal(order);
            (await Rows(numbers, "SELECT VALUE c.id FROM c ORDER BY c.v DESC")).Should().Equal(Enumerable.Reverse(order));
            (await Rows(numbers, "SELECT VALUE c.id FROM c WHERE c.v = 9007199254740993")).Should().Equal("p53p1");
        }

        // ── What the service accepts ──────────────────────────────────────────────────────────

        /// <summary>
        /// An <c>ORDER BY</c> over a path the indexing policy excludes is a 400 on Azure, <c>_etag</c> among them, which
        /// the default policy excludes; and the adapter's metadata, read from the container, refuses both.
        /// </summary>
        [Fact]
        public async Task AnOrderByOverAnExcludedPathFails()
        {
            var excluded = Container("excluded");
            var metadata = CosmosContainerMetadataReader.FromProperties((await excluded.ReadContainerAsync()).Resource);

            metadata.IsSortSupported([new("/x", false)]).Should().BeFalse("the policy excludes it");
            metadata.IsSortSupported([new("/y", false)]).Should().BeTrue();
            metadata.IsSortSupported([new("/_etag", false)]).Should().BeFalse("the default excludes it");

            var (_, refused) = await Run(excluded, new QueryDefinition("SELECT VALUE c.id FROM c ORDER BY c.x"));
            if (refused is null)
                Assert.Skip("This service orders by a path its policy excludes, as the emulator does; Azure refuses it.");

            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await Refuses(excluded, "SELECT VALUE c.id FROM c ORDER BY c._etag");
            (await Run(excluded, new QueryDefinition("SELECT VALUE c.id FROM c ORDER BY c.y"))).Refused.Should().BeNull("an indexed path orders");
            (await Run(excluded, new QueryDefinition("SELECT VALUE MAX(c.x) FROM c"))).Refused.Should().BeNull("an aggregate over an excluded path scans");
        }

        /// <summary>
        /// A computed property orders where an included path names it and is a 400 where none does, as an excluded path
        /// is; the adapter reads the names and the policy and refuses the second.
        /// </summary>
        [Fact]
        public async Task AnUnindexedComputedPropertyFailsToOrder()
        {
            var computed = Container("computed");
            var properties = (await computed.ReadContainerAsync()).Resource;

            if (properties.ComputedProperties.Count == 0)
                Assert.Skip("This service drops computed properties on create, as the emulator does.");

            var metadata = CosmosContainerMetadataReader.FromProperties(properties);
            metadata.IsSortSupported([new("/cp_lower", false)]).Should().BeTrue();
            metadata.IsSortSupported([new("/cp_neg", false)]).Should().BeFalse();

            (await Rows(computed, "SELECT VALUE c.id FROM c ORDER BY c.cp_lower")).Should().Equal("P1", "P2", "P3");
            await Refuses(computed, "SELECT VALUE c.id FROM c ORDER BY c.cp_neg");
        }

        /// <summary>
        /// A key repeated is two keys to the service, and without a composite index it refuses them.
        /// </summary>
        [Fact]
        public async Task ARepeatedKeyNeedsACompositeIndex()
        {
            var (_, refused) = await Run(Container("strings"), new QueryDefinition("SELECT VALUE c.id FROM c ORDER BY c.a, c.a"));
            if (refused is null)
                Assert.Skip("This service accepts a multi-key ORDER BY with no composite index, as the emulator does.");

            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        /// <summary>
        /// <c>ORDER BY RANK</c> returns the best match first and refuses a direction, <c>ASC</c> or <c>DESC</c>.
        /// </summary>
        [Fact]
        public async Task RankTakesNoDirection()
        {
            var strings = Container("strings");

            var (ranked, unsupported) = await Run(strings, new QueryDefinition("SELECT VALUE c.id FROM c ORDER BY RANK FullTextScore(c.text, 'cosmos')"));
            if (unsupported is not null)
                Assert.Skip("This service does not rank by a full text score, as the emulator does not: " + unsupported.StatusCode);

            ranked!.Take(2).Should().Equal(new[] { "a", "B" }, "the best match first");

            foreach (var direction in new[] { "ASC", "DESC" })
                await Refuses(strings, $"SELECT VALUE c.id FROM c ORDER BY RANK FullTextScore(c.text, 'cosmos') {direction}");
        }

    }

}
