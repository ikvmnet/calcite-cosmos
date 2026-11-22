using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
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
    /// A lookup join whose small side holds numbers, read through the connection: projected, sorted, joined again and
    /// counted above the join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>products</c> holds three products numbered 7, 8 and 9. Each statement joins a small side of <c>VALUES</c> to
    /// them by key, and what is asked is whether it answers what Calcite's own join of the same two sides answers:
    /// the rows below are those. The connection's reader reads every column of the row above the join in the box
    /// Calcite holds a value of its type in, and so does every operator between them.
    /// </para>
    /// <para>
    /// Needs a service, because a model document builds its schema by reading container definitions.
    /// </para>
    /// </remarks>
    public class CosmosLookupJoinRowEndToEndTests : IClassFixture<CosmosLookupJoinRowEndToEndTests.Fixture>
    {

        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await ClassInitialize();

            public ValueTask DisposeAsync() { ClassCleanup(); return default; }

        }

        static string Endpoint => CosmosEmulator.Endpoint!;
        static string Key => CosmosEmulator.Key!;

        static bool IsEmulator => CosmosEmulator.IsEmulator;

        static readonly string DatabaseName = "calcite_cosmos_lookup_rows_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>
        /// The products: two the small sides name, and one no key names.
        /// </summary>
        static readonly string[] Products =
        [
            """{"id":"p7","category":"x","n":7}""",
            """{"id":"p8","category":"x","n":8}""",
            """{"id":"p9","category":"x","n":9}""",
        ];

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

                try { await database.GetContainer("products").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("products", "/id"), cancellationToken: cts.Token)).Container;

                foreach (var json in Products)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var doc = JsonDocument.Parse(json);
                    using var response = await container.CreateItemStreamAsync(stream, new PartitionKey(doc.RootElement.GetProperty("id").GetString()), cancellationToken: cts.Token);
                    response.EnsureSuccessStatusCode();
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

        static string Model()
        {
            var model = new JsonObject
            {
                ["version"] = "1.0",
                ["defaultSchema"] = "COSMOS",
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
                            ["serviceTarget"] = CosmosEmulator.ServiceTargetOperand,
                            ["database"] = DatabaseName,
                            ["connectionMode"] = "gateway",
                            ["containers"] = new JsonArray
                            {
                                new JsonObject { ["name"] = "products" },
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
            await TestReasoning.Ready(connection, "COSMOS", "products");
            return connection;
        }

        static async Task<string> ExplainAsync(string sql)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql;
            return ((string?)await command.ExecuteScalarAsync() ?? "").Replace("\r\n", "\n");
        }

        static async Task<List<string>> RowsAsync(string sql)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

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
        /// The products read with their numbers as <c>BIGINT</c>.
        /// </summary>
        const string Numbered = """(SELECT c."id", JSON_VALUE(c."DOC", '$.n' RETURNING BIGINT) AS "n" FROM "products" AS c)""";

        /// <summary>
        /// A small side of three numbers keyed by a <c>BIGINT</c>, one key 2^53 + 1, which a double cannot hold exactly
        /// and no product is numbered.
        /// </summary>
        const string Keys = "(VALUES (1, CAST(7 AS BIGINT)), (2, CAST(9007199254740993 AS BIGINT)), (3, CAST(8 AS BIGINT))) AS v(i, k)";

        /// <summary>
        /// The plainest lookup: a small side's number projected beside the product it found.
        /// </summary>
        [Fact]
        public async Task ASmallSidesNumberIsReadAsCalcitesJoinAnswersIt()
        {
            RequireService();

            var sql = $"""SELECT v.i, t."id" FROM {Keys} JOIN {Numbered} AS t ON v.k = t."n" """;

            var plan = await ExplainAsync(sql);
            plan.Should().Contain("CosmosLookupJoin", "the products are fetched by the small side's keys:\n" + plan);

            (await RowsAsync(sql)).Should().BeEquivalentTo("1|p7", "3|p8");
        }

        /// <summary>
        /// The same, sorted by the small side's key.
        /// </summary>
        [Fact]
        public async Task ASortByTheSmallSidesKeyAnswersAsCalcitesJoinDoes()
        {
            RequireService();

            var sql = $"""SELECT v.i, t."id" FROM {Keys} JOIN {Numbered} AS t ON v.k = t."n" ORDER BY v.k""";

            var plan = await ExplainAsync(sql);
            plan.Should().Contain("CosmosLookupJoin", plan);

            (await RowsAsync(sql)).Should().Equal("1|p7", "3|p8");
        }

        /// <summary>
        /// Two lookups, the second's small side the first's rows, keyed by the product's number the first found.
        /// </summary>
        [Fact]
        public async Task ASecondLookupOverTheFirstsRowsAnswersAsCalcitesJoinDoes()
        {
            RequireService();

            var sql = $"""SELECT v.i, t."id", u."id" FROM {Keys} JOIN {Numbered} AS t ON v.k = t."n" JOIN {Numbered} AS u ON t."n" = u."n" """;

            var plan = await ExplainAsync(sql);
            Regex.Matches(plan, @"CosmosLookupJoin\(").Count.Should().Be(2, "each join is a lookup, the second over the first's rows:\n" + plan);

            (await RowsAsync(sql)).Should().BeEquivalentTo("1|p7|p7", "3|p8|p8");
        }

        /// <summary>
        /// A small side's quantity beside a text key on the document's id.
        /// </summary>
        [Fact]
        public async Task ASmallSidesQuantityBesideATextKeyAnswersAsCalcitesJoinDoes()
        {
            RequireService();

            const string sql = """SELECT v.q, t."id" FROM (VALUES (5, 'p7'), (6, 'p8')) AS v(q, k) JOIN (SELECT c."id" FROM "products" AS c) AS t ON v.k = t."id" """;

            var plan = await ExplainAsync(sql);
            plan.Should().Contain("CosmosLookupJoin", plan);

            (await RowsAsync(sql)).Should().BeEquivalentTo("5|p7", "6|p8");
        }

        /// <summary>
        /// A small side's count beside the key it groups.
        /// </summary>
        [Fact]
        public async Task ASmallSidesCountAnswersAsCalcitesJoinDoes()
        {
            RequireService();

            var sql = $"""SELECT x.c, t."id" FROM (SELECT v.k, COUNT(*) AS c FROM {Keys} GROUP BY v.k) AS x JOIN {Numbered} AS t ON x.k = t."n" """;

            var plan = await ExplainAsync(sql);
            plan.Should().Contain("CosmosLookupJoin", plan);

            (await RowsAsync(sql)).Should().BeEquivalentTo("1|p7", "1|p8");
        }

    }

}
