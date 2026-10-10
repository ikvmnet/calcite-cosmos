using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Apache.Calcite.Data;

using FluentAssertions;

using Microsoft.Azure.Cosmos;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// One container presented as several views, each a projection of the same documents narrowed by a
    /// discriminator, and joined back together on a shared key — the shape a table-per-type mapping
    /// produces over a federation's views.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The views present links as a table-per-type hierarchy — a link, its body, and a body per kind — over
    /// a container holding one document per link, partitioned on <c>/linkId</c>, with the key every view
    /// projects — <c>$.data.guid</c> — unique by the application's convention and by nothing the service
    /// enforces. What is asked is whether a join of two of them reads the container once or once per view.
    /// </para>
    /// <para>
    /// Two containers hold the same documents and differ only in what is declared. <c>links</c> declares
    /// <c>/data/guid</c> a key; <c>undeclared</c> does not, and is the control: nothing the adapter can see
    /// says a guid names one document there, so its joins have to stay joins. Every query is run against
    /// both and the rows compared, which is what says a merged plan answers what the joins answered.
    /// </para>
    /// <para>
    /// Needs a service, because a model document builds its schema by reading container definitions.
    /// </para>
    /// </remarks>
    public class CosmosSelfJoinTests : IClassFixture<CosmosSelfJoinTests.Fixture>
    {

        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await ClassInitialize();

            public ValueTask DisposeAsync() { ClassCleanup(); return default; }

        }

        static string Endpoint => CosmosEmulator.Endpoint!;
        static string Key => CosmosEmulator.Key!;

        static bool IsEmulator => CosmosEmulator.IsEmulator;

        static readonly string DatabaseName = "calcite_cosmos_selfjoin_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        const string G1 = "11111111-1111-4111-8111-111111111111";
        const string G2 = "22222222-2222-4222-8222-222222222222";
        const string G3 = "33333333-3333-4333-8333-333333333333";
        const string G4 = "44444444-4444-4444-8444-444444444444";
        const string G5 = "55555555-5555-4555-8555-555555555555";

        const string Park = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const string MapA = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        const string MapB = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

        /// <summary>
        /// The documents: one link of each kind and a second map link, a link carrying neither an
        /// <c>offline</c> flag nor a change time, and a document of another type in the same partition as a
        /// link, carrying no guid, which no view selects.
        /// </summary>
        /// <remarks>
        /// The change times sit either side of <c>2026-08-02T12:00:00.000Z</c> and one exactly on it, and
        /// one is a fraction past it, so that a comparison against a parameter has every boundary to get
        /// wrong.
        /// </remarks>
        static readonly string[] Documents = new[]
        {
            """{"id":"Link$1","linkId":1,"type":"Link","data":{"id":1,"linkId":1,"guid":"@G1","type":"park","label":"Park one","offline":false,"metadata":{"changeUtcTime":"2026-08-01T10:00:00.000Z"},"data":{"parkId":"@Park"}}}""",
            """{"id":"Link$2","linkId":2,"type":"Link","data":{"id":2,"linkId":2,"guid":"@G2","type":"map","label":"Map two","offline":true,"metadata":{"changeUtcTime":"2026-08-02T12:00:00.000Z"},"data":{"parkId":"@Park","mapId":"@MapA"}}}""",
            """{"id":"Link$3","linkId":3,"type":"Link","data":{"id":3,"linkId":3,"guid":"@G3","type":"map","label":"Map three","offline":false,"metadata":{"changeUtcTime":"2026-08-02T12:00:00.123Z"},"data":{"parkId":"@Park","mapId":"@MapB"}}}""",
            """{"id":"Link$4","linkId":4,"type":"Link","data":{"id":4,"linkId":4,"guid":"@G4","type":"spot","label":"Spot four","offline":false,"metadata":{"changeUtcTime":"2026-08-03T00:00:00.500Z"},"data":{}}}""",
            """{"id":"Link$5","linkId":5,"type":"Link","data":{"id":5,"linkId":5,"guid":"@G5","type":"spot","label":"Spot five","data":{}}}""",
            """{"id":"Scan$9","linkId":2,"type":"LinkScan","data":{"id":9,"linkId":2}}""",
        }.Select(Fill).ToArray();

        /// <summary>
        /// The documents of the third container, which no view narrows and which declares only types: one
        /// <c>id</c> held in two partitions — <c>1</c> and the <c>null</c> partition — a second beside the
        /// first in partition 1, and a document with no partition key value at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>id</c> repeats across partitions legitimately, and a null partition key reads as SQL null. That
        /// is the case <c>pk + id</c> being a key has to survive.
        /// </para>
        /// <para>
        /// <b>The document with no value has an <c>id</c> of its own, and that is a measurement.</b> Written
        /// as a third <c>A</c>, the emulator refuses it with 409 Conflict after the <c>A</c> whose partition
        /// key is null: there, absent and null are one partition. Whether Azure keeps them apart is not
        /// measured, and nothing here depends on it — a null equates nothing either way.
        /// </para>
        /// </remarks>
        static readonly (string Json, PartitionKey Partition)[] Loose =
        {
            ("""{"id":"A","linkId":1,"label":"one"}""", new PartitionKey(1)),
            ("""{"id":"B","linkId":1,"label":"one b"}""", new PartitionKey(1)),
            ("""{"id":"A","linkId":null,"label":"null pk"}""", PartitionKey.Null),
            ("""{"id":"C","label":"no pk"}""", PartitionKey.None),
        };

        static string Fill(string json) => json
            .Replace("@G1", G1).Replace("@G2", G2).Replace("@G3", G3).Replace("@G4", G4).Replace("@G5", G5)
            .Replace("@Park", Park).Replace("@MapA", MapA).Replace("@MapB", MapB);

        static CosmosClient? _client;
        static string? _initializationFailure;

        static async Task ClassInitialize()
        {
            _ = new CosmosSchemaFactory();

            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(IsEmulator ? 5 : 30),
                MaxRetryAttemptsOnRateLimitedRequests = 0,
            };

            if (IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(IsEmulator ? 10 : 120));
                var client = new CosmosClient(Endpoint, Key, options);
                var database = (await client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;

                foreach (var name in new[] { "links", "undeclared" })
                {
                    try { await database.GetContainer(name).DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                    var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties(name, "/linkId"), cancellationToken: cts.Token)).Container;

                    foreach (var json in Documents)
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                        using var doc = JsonDocument.Parse(json);
                        using var response = await container.CreateItemStreamAsync(stream, new PartitionKey(doc.RootElement.GetProperty("linkId").GetInt32()), cancellationToken: cts.Token);
                        response.EnsureSuccessStatusCode();
                    }
                }

                try { await database.GetContainer("loose").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                var loose = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("loose", "/linkId"), cancellationToken: cts.Token)).Container;

                foreach (var (json, partition) in Loose)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var response = await loose.CreateItemStreamAsync(stream, partition, cancellationToken: cts.Token);
                    if (response.IsSuccessStatusCode == false)
                        throw new InvalidOperationException($"Writing {json} answered {(int)response.StatusCode}: {response.ErrorMessage}");
                }

                _client = client;
            }
            catch (Exception e)
            {
                _initializationFailure = e.Message;
                _client = null;
            }
        }

        static void ClassCleanup()
        {
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            _client?.Dispose();
            _client = null;
        }

        static void RequireService()
        {
            if (_client is null)
                Assert.Skip("These need a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));
        }

        const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

        const string Milliseconds = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$";

        /// <summary>
        /// The JSON Schema both containers declare, covering the paths these views read.
        /// </summary>
        /// <remarks>
        /// <c>guid</c> is a canonical lowercase UUID, which is what lets the identifier cast push at all.
        /// The other kind of document in the container is described too — a schema is a claim about every
        /// document — by leaving <c>data</c>'s members optional.
        /// </remarks>
        static JsonObject Schema() => new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("linkId", "type"),
            ["properties"] = new JsonObject
            {
                ["linkId"] = new JsonObject { ["type"] = "integer" },
                ["type"] = new JsonObject { ["type"] = "string" },
                ["data"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "integer" },
                        ["guid"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                        ["type"] = new JsonObject { ["type"] = "string" },
                        ["label"] = new JsonObject { ["type"] = "string" },
                        ["offline"] = new JsonObject { ["type"] = "boolean" },
                        ["metadata"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["changeUtcTime"] = new JsonObject { ["type"] = "string", ["pattern"] = Milliseconds },
                            },
                        },
                        ["data"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["parkId"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                                ["mapId"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                            },
                        },
                    },
                },
            },
        };

        static string Text(string path) => $"""JSON_VALUE(l."DOC", '{path}' RETURNING VARCHAR)""";

        static string Identifier(string path) => $"CAST({Text(path)} AS UUID)";

        const string IsLink = """JSON_VALUE(l."DOC", '$.type' RETURNING VARCHAR) = 'Link'""";

        /// <summary>
        /// The five views over the named container: identifiers read as <c>UUID</c> casts of text accessors,
        /// each narrowed by the link discriminator and its body's kind.
        /// </summary>
        static IEnumerable<(string Name, string Sql)> Views(string container)
        {
            yield return ("Link", $"""
                SELECT {Identifier("$.data.guid")} AS "Id",
                       CAST({Text("$.data.id")} AS INTEGER) AS "IntId",
                       {Text("$.data.label")} AS "Label",
                       CAST({Text("$.data.offline")} AS BOOLEAN) AS "Offline",
                       CAST({Text("$.data.metadata.changeUtcTime")} AS TIMESTAMP(3) FORMAT 'YYYY-MM-DD''T''HH24:MI:SS.FF3''Z''') AS "ChangeUtcTime"
                FROM "COSMOS"."{container}" AS l
                WHERE {IsLink}
                  AND {Text("$.data.type")} IN ('park', 'map', 'spot')
                """);

            yield return ("LinkBody", $"""
                SELECT {Identifier("$.data.guid")} AS "Id"
                FROM "COSMOS"."{container}" AS l
                WHERE {IsLink}
                  AND {Text("$.data.type")} IN ('park', 'map', 'spot')
                """);

            yield return ("ParkLinkBody", $"""
                SELECT {Identifier("$.data.guid")} AS "Id",
                       {Identifier("$.data.data.parkId")} AS "ParkId"
                FROM "COSMOS"."{container}" AS l
                WHERE {IsLink}
                  AND {Text("$.data.type")} = 'park'
                """);

            yield return ("MapLinkBody", $"""
                SELECT {Identifier("$.data.guid")} AS "Id",
                       {Identifier("$.data.data.parkId")} AS "ParkId",
                       {Identifier("$.data.data.mapId")} AS "MapId"
                FROM "COSMOS"."{container}" AS l
                WHERE {IsLink}
                  AND {Text("$.data.type")} = 'map'
                """);

            yield return ("SpotLinkBody", $"""
                SELECT {Identifier("$.data.guid")} AS "Id"
                FROM "COSMOS"."{container}" AS l
                WHERE {IsLink}
                  AND {Text("$.data.type")} = 'spot'
                """);
        }

        /// <summary>
        /// A model with the Cosmos schema and one schema of views per container: <c>DECLARED</c> over
        /// <c>links</c>, which declares the key, and <c>UNDECLARED</c> over <c>undeclared</c>, which does
        /// not.
        /// </summary>
        static string Model()
        {
            JsonArray ViewsOver(string container)
            {
                var views = new JsonArray();
                foreach (var (name, sql) in Views(container))
                    views.Add(new JsonObject { ["name"] = name, ["type"] = "view", ["sql"] = sql });
                return views;
            }

            var model = new JsonObject
            {
                ["version"] = "1.0",
                ["defaultSchema"] = "DECLARED",
                ["schemas"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "COSMOS",
                        ["type"] = "custom",
                        ["factory"] = "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
                        ["operand"] = new JsonObject
                        {
                            ["endpoint"] = Endpoint,
                            ["key"] = Key,
                            ["database"] = DatabaseName,
                            ["connectionMode"] = "gateway",
                            ["containers"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["name"] = "links",
                                    ["schema"] = Schema(),
                                    ["constraints"] = new JsonArray("UNIQUE (JSON_VALUE(DOC, '$.data.guid'))"),
                                },
                                new JsonObject
                                {
                                    ["name"] = "undeclared",
                                    ["schema"] = Schema(),
                                },
                                new JsonObject
                                {
                                    ["name"] = "loose",
                                    ["schema"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["linkId"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
                                            ["label"] = new JsonObject { ["type"] = "string" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                    new JsonObject { ["name"] = "DECLARED", ["tables"] = ViewsOver("links") },
                    new JsonObject { ["name"] = "UNDECLARED", ["tables"] = ViewsOver("undeclared") },
                    new JsonObject
                    {
                        ["name"] = "LOOSE",
                        ["tables"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "Doc",
                                ["type"] = "view",
                                ["sql"] = """
                                    SELECT JSON_VALUE(d."DOC", '$.linkId' RETURNING VARCHAR) AS "LinkId",
                                           d."id" AS "DocId",
                                           JSON_VALUE(d."DOC", '$.label' RETURNING VARCHAR) AS "Label"
                                    FROM "COSMOS"."loose" AS d
                                    """,
                            },
                        },
                    },
                },
            };

            return model.ToJsonString();
        }

        static async Task<DbConnection> OpenAsync()
        {
            var connection = new CalciteConnection(new CalciteConnectionStringBuilder
            {
                Model = "inline:" + Model(),
                CaseSensitive = true,
            }.ConnectionString);

            await connection.OpenAsync();
            return connection;
        }

        /// <summary>
        /// Names every view in a query through the given schema of views, which is the only thing that
        /// tells the two containers apart.
        /// </summary>
        static string Over(string schema, string sql) => sql.Replace("{views}", $"\"{schema}\"");

        static async Task<string> ExplainAsync(string schema, string sql, params object[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + Over(schema, sql);
            Bind(command, parameters);
            return ((string?)await command.ExecuteScalarAsync() ?? "").Replace("\r\n", "\n");
        }

        static void Bind(DbCommand command, object[] parameters)
        {
            foreach (var value in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
        }

        static async Task<List<string>> RowsAsync(string schema, string sql, params object[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = Over(schema, sql);
            Bind(command, parameters);

            var rows = new List<string>();

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                    values[i] = reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";

                rows.Add(string.Join("|", values));
            }

            rows.Sort(StringComparer.Ordinal);
            return rows;
        }

        static int Scans(string plan) => Regex.Matches(plan, "CosmosTableScan").Count;

        /// <summary>
        /// The map links on one map, as Entity Framework writes it over a table-per-type hierarchy: the
        /// link joined to its body, and the body to the subtype that matched.
        /// </summary>
        const string MapLinksOnMap = $$"""
            SELECT "l"."Id", "l"."Label", "s"."MapId"
            FROM {views}."Link" AS "l"
            LEFT JOIN (
                SELECT "l0"."Id", "m"."MapId"
                FROM {views}."LinkBody" AS "l0"
                LEFT JOIN {views}."MapLinkBody" AS "m" ON "l0"."Id" = "m"."Id"
            ) AS "s" ON "l"."Id" = "s"."Id"
            WHERE "s"."MapId" = UUID '{{MapA}}'
            """;

        /// <summary>
        /// Every link with every subtype's columns, and the discriminator Entity Framework computes from
        /// which subtype matched — the query that read the container five times.
        /// </summary>
        const string EveryLink = """
            SELECT "l"."Id", "l"."IntId", "l"."Label", "l"."Offline", "p"."ParkId", "m"."ParkId", "m"."MapId",
                   CASE WHEN "s"."Id" IS NOT NULL THEN 'spot'
                        WHEN "m"."Id" IS NOT NULL THEN 'map'
                        WHEN "p"."Id" IS NOT NULL THEN 'park'
                   END AS "Kind"
            FROM {views}."Link" AS "l"
            INNER JOIN {views}."LinkBody" AS "b" ON "l"."Id" = "b"."Id"
            LEFT JOIN {views}."ParkLinkBody" AS "p" ON "b"."Id" = "p"."Id"
            LEFT JOIN {views}."MapLinkBody" AS "m" ON "b"."Id" = "m"."Id"
            LEFT JOIN {views}."SpotLinkBody" AS "s" ON "b"."Id" = "s"."Id"
            """;

        /// <summary>
        /// The map links on one map read the container once, with the body's discriminator and the map's
        /// identifier in the one statement's predicate.
        /// </summary>
        [Fact]
        public async Task TheMapLinksOnAMapReadTheContainerOnce()
        {
            RequireService();

            var plan = await ExplainAsync("DECLARED", MapLinksOnMap);

            Scans(plan).Should().Be(1, "every view reads the same document by the same key:\n" + plan);
            plan.Should().NotContain("Join", "and there is nothing left to join:\n" + plan);
            plan.Should().Contain("'$.data.type'), 'map')", "the map body's discriminator reaches the service:\n" + plan);
            plan.Should().Contain("'$.data.data.mapId'), '" + MapA + "')", "and so does the map:\n" + plan);
        }

        /// <summary>
        /// Every link reads the container once rather than once per view.
        /// </summary>
        [Fact]
        public async Task EveryLinkReadsTheContainerOnce()
        {
            RequireService();

            var plan = await ExplainAsync("DECLARED", EveryLink);

            Scans(plan).Should().Be(1, "five views of one document are one read of it:\n" + plan);
            plan.Should().NotContain("Join", "and there is nothing left to join:\n" + plan);
        }

        /// <summary>
        /// Without the declaration the joins stay joins: nothing the service enforces says a guid names
        /// one document.
        /// </summary>
        [Fact]
        public async Task AnUndeclaredKeyLeavesTheJoins()
        {
            RequireService();

            var plan = await ExplainAsync("UNDECLARED", MapLinksOnMap);

            Scans(plan).Should().BeGreaterThan(1, "a guid unique by convention proves nothing:\n" + plan);
        }

        /// <summary>
        /// The partition key with <c>id</c> is a key the service enforces, so a join on both is one read
        /// with nothing declared — over a view with no filter at all, and over documents whose partition
        /// key is absent or null.
        /// </summary>
        /// <remarks>
        /// The two documents with <c>id</c> <c>A</c> sit in partition 1 and the null partition, and the one
        /// with no partition key value reads as null too; a null equals nothing, so only the documents in
        /// partition 1 pair, as the join pairs them.
        /// </remarks>
        [Fact]
        public async Task ThePartitionKeyWithIdIsAKeyWithNothingDeclared()
        {
            RequireService();

            const string sql = """
                SELECT a."LinkId", a."DocId", a."Label", b."Label"
                FROM "LOOSE"."Doc" AS a
                INNER JOIN "LOOSE"."Doc" AS b ON a."LinkId" = b."LinkId" AND a."DocId" = b."DocId"
                """;

            var plan = await ExplainAsync("LOOSE", sql);
            Scans(plan).Should().Be(1, "the service makes a partition key and an id one document:\n" + plan);

            (await RowsAsync("LOOSE", sql)).Should().Equal("1|A|one|one", "1|B|one b|one b");
        }

        /// <summary>
        /// And a left join keeps the documents with no partition key value, pairing them with nothing.
        /// </summary>
        [Fact]
        public async Task ALeftJoinOnThePartitionKeyWithIdKeepsTheUnpartitionedRowsUnpaired()
        {
            RequireService();

            const string sql = """
                SELECT a."LinkId", a."DocId", a."Label", b."Label"
                FROM "LOOSE"."Doc" AS a
                LEFT JOIN "LOOSE"."Doc" AS b ON a."LinkId" = b."LinkId" AND a."DocId" = b."DocId"
                """;

            var plan = await ExplainAsync("LOOSE", sql);
            Scans(plan).Should().Be(1, plan);

            (await RowsAsync("LOOSE", sql)).Should().Equal("1|A|one|one", "1|B|one b|one b", "null|A|null pk|null", "null|C|no pk|null");
        }

        /// <summary>
        /// <c>id</c> alone is unique only within a partition, so a join on it stays a join — and pairs the
        /// two documents named <c>A</c> with each other, which one read of each could not.
        /// </summary>
        [Fact]
        public async Task IdAloneIsNotAKey()
        {
            RequireService();

            const string sql = """
                SELECT a."Label", b."Label"
                FROM "LOOSE"."Doc" AS a
                INNER JOIN "LOOSE"."Doc" AS b ON a."DocId" = b."DocId"
                """;

            var plan = await ExplainAsync("LOOSE", sql);
            Scans(plan).Should().BeGreaterThan(1, plan);

            (await RowsAsync("LOOSE", sql)).Should().HaveCount(6, "two documents named A pair four ways, and B and C each with itself");
        }

        /// <summary>
        /// Whether a plan still holds a condition Calcite evaluates in process, where a pushed plan has
        /// none.
        /// </summary>
        static bool FiltersInProcess(string plan) => plan.Contains("ClrCursorFilter") || plan.Contains("$condition");

        /// <summary>
        /// A boolean column is tested at the service, where the schema says the path holds a boolean.
        /// #181.
        /// </summary>
        /// <remarks>
        /// The column is <c>CAST(JSON_VALUE(…) AS BOOLEAN)</c>, which Calcite reads by parsing the text —
        /// so without the declaration it is not the stored value, and the test kept only a definedness
        /// check at the service. Every truth test is asked here, and the link with no <c>offline</c> at
        /// all is the one that tells them apart: <c>IS NOT TRUE</c> keeps it and <c>NOT</c> does not.
        /// </remarks>
        [Theory]
        [InlineData("""
            "l"."Offline"
            """, new[] { G2 })]
        [InlineData("""
            "l"."Offline" IS TRUE
            """, new[] { G2 })]
        [InlineData("""
            "l"."Offline" = TRUE
            """, new[] { G2 })]
        [InlineData("""
            NOT "l"."Offline"
            """, new[] { G1, G3, G4 })]
        [InlineData("""
            "l"."Offline" IS FALSE
            """, new[] { G1, G3, G4 })]
        [InlineData("""
            "l"."Offline" IS NOT TRUE
            """, new[] { G1, G3, G4, G5 })]
        [InlineData("""
            "l"."Offline" IS NOT FALSE
            """, new[] { G2, G5 })]
        [InlineData("""
            "l"."Offline" IS NULL
            """, new[] { G5 })]
        public async Task ABooleanColumnIsTestedAtTheService(string predicate, string[] expected)
        {
            RequireService();

            var sql = $"""SELECT "l"."Id" FROM {"{views}"}."Link" AS "l" WHERE {predicate}""";

            var plan = await ExplainAsync("DECLARED", sql);
            FiltersInProcess(plan).Should().BeFalse("the test is the service's:\n" + plan);

            (await RowsAsync("DECLARED", sql)).Should().Equal(expected.OrderBy(g => g, StringComparer.Ordinal));
        }

        /// <summary>
        /// And the column reads back as the stored boolean — null for the link that has none.
        /// </summary>
        [Fact]
        public async Task ABooleanColumnReadsBackAsTheStoredBoolean()
        {
            RequireService();

            (await RowsAsync("DECLARED", """SELECT "l"."Id", "l"."Offline" FROM {views}."Link" AS "l" """))
                .Should().Equal($"{G1}|False", $"{G2}|True", $"{G3}|False", $"{G4}|False", $"{G5}|null");
        }

        /// <summary>
        /// A change time compared with a parameter is compared at the service, the parameter written in
        /// the stored spelling when the statement runs. #182.
        /// </summary>
        /// <remarks>
        /// The parameter is a <see cref="DateTime"/> through the ADO.NET driver, which is how a host sends
        /// one, and the view reads the stored instant with the format the declared shape is read by.
        /// The boundaries are the point: a value exactly on a stored instant, and one a fraction short of
        /// one.
        /// </remarks>
        [Theory]
        [InlineData(">", "2026-08-02T12:00:00.000", new[] { G3, G4 })]
        [InlineData(">=", "2026-08-02T12:00:00.000", new[] { G2, G3, G4 })]
        [InlineData("<", "2026-08-02T12:00:00.000", new[] { G1 })]
        [InlineData("=", "2026-08-02T12:00:00.123", new[] { G3 })]
        [InlineData("<>", "2026-08-02T12:00:00.123", new[] { G1, G2, G4 })]
        [InlineData(">", "2026-08-02T12:00:00.122", new[] { G3, G4 })]
        public async Task AChangeTimeComparedWithAParameterIsComparedAtTheService(string op, string value, string[] expected)
        {
            RequireService();

            var sql = $"""SELECT "l"."Id" FROM {"{views}"}."Link" AS "l" WHERE "l"."ChangeUtcTime" {op} CAST(? AS TIMESTAMP)""";
            var instant = DateTime.SpecifyKind(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

            var plan = await ExplainAsync("DECLARED", sql, instant);
            FiltersInProcess(plan).Should().BeFalse("the comparison is the service's:\n" + plan);

            (await RowsAsync("DECLARED", sql, instant)).Should().Equal(expected.OrderBy(g => g, StringComparer.Ordinal));
        }

        /// <summary>
        /// The merged plan answers what the joins answered.
        /// </summary>
        [Theory]
        [InlineData(MapLinksOnMap)]
        [InlineData(EveryLink)]
        public async Task TheMergedPlanAnswersWhatTheJoinsAnswered(string sql)
        {
            RequireService();

            var merged = await RowsAsync("DECLARED", sql);
            var joined = await RowsAsync("UNDECLARED", sql);

            // The comparison means something only if the two plans differ.
            Scans(await ExplainAsync("UNDECLARED", sql)).Should().BeGreaterThan(1);

            merged.Should().NotBeEmpty();
            merged.Should().Equal(joined);
        }

    }

}
