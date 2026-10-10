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
    /// A page of one container joined to another on that container's key — the shape an ORM writes for a
    /// page of rows with the names of what they point at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>links</c> holds links, each naming a park by a canonical UUID; <c>parks</c> holds parks keyed by
    /// the same, a soft-deleted park, and a document of another type. What is asked is whether the parks
    /// are fetched by the page's keys or read whole, and whether a left join keeps the links whose park is
    /// absent, deleted, or not named at all.
    /// </para>
    /// <para>
    /// Needs a service, because a model document builds its schema by reading container definitions.
    /// </para>
    /// </remarks>
    public class CosmosLookupJoinEndToEndTests : IClassFixture<CosmosLookupJoinEndToEndTests.Fixture>
    {

        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await ClassInitialize();

            public ValueTask DisposeAsync() { ClassCleanup(); return default; }

        }

        static string Endpoint => CosmosEmulator.Endpoint!;
        static string Key => CosmosEmulator.Key!;

        static bool IsEmulator => CosmosEmulator.IsEmulator;

        static readonly string DatabaseName = "calcite_cosmos_lookup_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        const string L1 = "11111111-1111-4111-8111-111111111111";
        const string L2 = "22222222-2222-4222-8222-222222222222";
        const string L3 = "33333333-3333-4333-8333-333333333333";
        const string L4 = "44444444-4444-4444-8444-444444444444";
        const string L5 = "55555555-5555-4555-8555-555555555555";

        const string Arches = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        const string Zion = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        const string Gone = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        const string Nowhere = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";

        /// <summary>
        /// The links, in label order: one on Arches, one on Zion, one on a park since deleted, one on a park
        /// that was never written, and one naming no park at all.
        /// </summary>
        static readonly string[] Links = new[]
        {
            """{"id":"1","type":"Link","data":{"guid":"@L1","label":"a","parkId":"@Arches"}}""",
            """{"id":"2","type":"Link","data":{"guid":"@L2","label":"b","parkId":"@Zion"}}""",
            """{"id":"3","type":"Link","data":{"guid":"@L3","label":"c","parkId":"@Gone"}}""",
            """{"id":"4","type":"Link","data":{"guid":"@L4","label":"d","parkId":"@Nowhere"}}""",
            """{"id":"5","type":"Link","data":{"guid":"@L5","label":"e"}}""",
        }.Select(Fill).ToArray();

        /// <summary>
        /// The parks, a deleted one, and a map that shares the container and no key with them.
        /// </summary>
        static readonly string[] Parks = new[]
        {
            """{"id":"p1","type":"Park","data":{"id":"@Arches","name":"Arches"}}""",
            """{"id":"p2","type":"Park","data":{"id":"@Zion","name":"Zion"}}""",
            """{"id":"p3","type":"Park","data":{"id":"@Gone","name":"Gone","metadata":{"deleteUtcTime":"2026-01-01T00:00:00.000Z"}}}""",
            """{"id":"m1","type":"ParkMap","data":{"id":"eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee","name":"A map"}}""",
        }.Select(Fill).ToArray();

        static string Fill(string json) => json
            .Replace("@L1", L1).Replace("@L2", L2).Replace("@L3", L3).Replace("@L4", L4).Replace("@L5", L5)
            .Replace("@Arches", Arches).Replace("@Zion", Zion).Replace("@Gone", Gone).Replace("@Nowhere", Nowhere);

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

                foreach (var (name, documents) in new[] { ("links", Links), ("parks", Parks) })
                {
                    try { await database.GetContainer(name).DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                    var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties(name, "/id"), cancellationToken: cts.Token)).Container;

                    foreach (var json in documents)
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                        using var doc = JsonDocument.Parse(json);
                        using var response = await container.CreateItemStreamAsync(stream, new PartitionKey(doc.RootElement.GetProperty("id").GetString()), cancellationToken: cts.Token);
                        response.EnsureSuccessStatusCode();
                    }
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

        /// <summary>
        /// A schema giving every identifier the views read a canonical lowercase UUID form, which is what
        /// lets a key be written in the spelling the container stores.
        /// </summary>
        static JsonObject Schema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject { ["type"] = "string" },
                ["data"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                        ["guid"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                        ["parkId"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                        ["label"] = new JsonObject { ["type"] = "string" },
                        ["name"] = new JsonObject { ["type"] = "string" },
                    },
                },
            },
        };

        static string Model()
        {
            static string Text(string alias, string path) => $"""JSON_VALUE({alias}."DOC", '{path}' RETURNING VARCHAR)""";

            static string Identifier(string alias, string path) => $"CAST({Text(alias, path)} AS UUID)";

            var model = new JsonObject
            {
                ["version"] = "1.0",
                ["defaultSchema"] = "V",
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
                                new JsonObject { ["name"] = "links", ["schema"] = Schema() },
                                new JsonObject { ["name"] = "parks", ["schema"] = Schema() },
                            },
                        },
                    },
                    new JsonObject
                    {
                        ["name"] = "V",
                        ["tables"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "Link",
                                ["type"] = "view",
                                ["sql"] = $"""
                                    SELECT {Identifier("l", "$.data.guid")} AS "Id",
                                           {Text("l", "$.data.label")} AS "Label",
                                           {Identifier("l", "$.data.parkId")} AS "ParkId"
                                    FROM "COSMOS"."links" AS l
                                    WHERE {Text("l", "$.type")} = 'Link'
                                    """,
                            },
                            new JsonObject
                            {
                                ["name"] = "Park",
                                ["type"] = "view",
                                ["sql"] = $"""
                                    SELECT {Identifier("p", "$.data.id")} AS "Id",
                                           {Text("p", "$.data.name")} AS "Name"
                                    FROM "COSMOS"."parks" AS p
                                    WHERE {Text("p", "$.type")} = 'Park'
                                      AND {Text("p", "$.data.metadata.deleteUtcTime")} IS NULL
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
            var connectionString = new CalciteConnectionStringBuilder
            {
                Model = "inline:" + Model(),
                CaseSensitive = true,
            }.ConnectionString;

            var connection = new CalciteDataSourceBuilder(connectionString).Build().CreateConnection();
            await connection.OpenAsync();
            return connection;
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

        static async Task<string> ExplainAsync(string sql, params object[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql;
            Bind(command, parameters);
            return ((string?)await command.ExecuteScalarAsync() ?? "").Replace("\r\n", "\n");
        }

        static async Task<List<string>> RowsAsync(string sql, params object[] parameters)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
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

            return rows;
        }

        /// <summary>
        /// A page of links with the name of each one's park, as Entity Framework writes it.
        /// </summary>
        static string PageWithParks(string join) => $"""
            SELECT "l0"."Label", "p"."Name"
            FROM (
                SELECT "l"."Id", "l"."Label", "l"."ParkId"
                FROM "Link" AS "l"
                ORDER BY "l"."Label" NULLS FIRST
                OFFSET ? ROWS FETCH NEXT ? ROWS ONLY
            ) AS "l0"
            {join} JOIN "Park" AS "p" ON "l0"."ParkId" = "p"."Id"
            ORDER BY "l0"."Label" NULLS FIRST
            """;

        /// <summary>
        /// The parks are fetched by the page's keys rather than read whole. #193.
        /// </summary>
        /// <remarks>
        /// The key is a <c>UUID</c> cast and the join a left join, and each of those declined the lookup on
        /// its own: the parks container was read whole and joined in process, for at most one park per
        /// link on the page.
        /// </remarks>
        [Fact]
        public async Task APageLooksUpItsParksByKey()
        {
            RequireService();

            var plan = await ExplainAsync(PageWithParks("LEFT"), 0, 5);

            plan.Should().Contain("CosmosLookupJoin", "the parks are fetched by the page's keys:\n" + plan);
            plan.Should().NotContain("HashJoin", "and nothing is joined by reading both sides whole:\n" + plan);
        }

        /// <summary>
        /// And a left join keeps every link on the page: the deleted park, the park never written, and the
        /// link naming none each leave a null name.
        /// </summary>
        [Theory]
        [InlineData(0, 5, new[] { "a|Arches", "b|Zion", "c|null", "d|null", "e|null" })]
        [InlineData(1, 2, new[] { "b|Zion", "c|null" })]
        public async Task ALeftLookupKeepsTheLinksWithNoPark(int offset, int fetch, string[] expected)
        {
            RequireService();

            (await RowsAsync(PageWithParks("LEFT"), offset, fetch)).Should().Equal(expected);
        }

        /// <summary>
        /// An inner join keeps only the links whose park is there to be found.
        /// </summary>
        [Fact]
        public async Task AnInnerLookupKeepsOnlyTheLinksWithAPark()
        {
            RequireService();

            var plan = await ExplainAsync(PageWithParks("INNER"), 0, 5);
            plan.Should().Contain("CosmosLookupJoin", plan);

            (await RowsAsync(PageWithParks("INNER"), 0, 5)).Should().Equal("a|Arches", "b|Zion");
        }

        /// <summary>
        /// The same join without a page, narrowed by a filter instead.
        /// </summary>
        [Fact]
        public async Task AFilteredLeftLookupFindsTheSameParks()
        {
            RequireService();

            const string sql = """
                SELECT "l"."Label", "p"."Name"
                FROM "Link" AS "l"
                LEFT JOIN "Park" AS "p" ON "l"."ParkId" = "p"."Id"
                WHERE "l"."Label" IN ('a', 'b')
                ORDER BY "l"."Label"
                """;

            (await RowsAsync(sql)).Should().Equal("a|Arches", "b|Zion");
        }

    }

}
