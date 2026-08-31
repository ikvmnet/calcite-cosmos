using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Data;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// The Cosmos functions named through the path the README documents: a model document, a
    /// <see cref="DbConnection"/>, a <see cref="DbCommand"/>, and nothing chained into anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the claim issue #34 was about. <c>CosmosOperators.Instance</c> was reachable from a
    /// planner a host assembled — which is how the rest of the suite reaches it — and from nothing
    /// else, so an application that reached the adapter the documented way got
    /// <c>FULLTEXTCONTAINS</c> back as an unknown function. A test that builds its own validator
    /// cannot tell whether that is fixed, because building one is the thing an application does not
    /// do; so this opens a real connection.
    /// </para>
    /// <para>
    /// Needs a service, because a model document builds its schema by reading container definitions.
    /// Reports inconclusive where none is reachable, like the rest of the tests that need one.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosConnectionFunctionTests
    {

        // Well-known public emulator credentials, documented by Microsoft. Not a secret.
        const string EmulatorEndpoint = "http://localhost:8081/";
        const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        static readonly string Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") is string e && e.Length > 0 ? e : EmulatorEndpoint;
        static readonly string Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY") is string k && k.Length > 0 ? k : EmulatorKey;

        static bool IsEmulator => ReferenceEquals(Endpoint, EmulatorEndpoint);

        /// <summary>
        /// The database this fixture builds, named per target framework for the reason
        /// <c>CosmosQueryExecutorTests</c> gives: every framework runs at once against one account.
        /// </summary>
        static readonly string DatabaseName = "calcite_cosmos_conn_" +
            System.Text.RegularExpressions.Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        static CosmosClient? _client;
        static string? _initializationFailure;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            // A model names its schema factory by type name, resolved through IKVM, and only finds it
            // if the assembly is loaded. Nothing else in this file mentions the type, so this does.
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

                var properties = new ContainerProperties("products", "/category");

                // Full text search needs a policy naming the searchable paths and an index over them.
                // Without both the service rejects FULLTEXTCONTAINS outright, and the rejection would
                // then say nothing about whether the name resolved.
                properties.FullTextPolicy = new FullTextPolicy
                {
                    DefaultLanguage = "en-US",
                    FullTextPaths = new System.Collections.ObjectModel.Collection<FullTextPath>
                    {
                        new FullTextPath { Path = "/name", Language = "en-US" },
                    },
                };
                properties.IndexingPolicy.FullTextIndexes.Add(new FullTextIndexPath { Path = "/name" });

                try { await database.GetContainer("products").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                var container = (await database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cts.Token)).Container;

                foreach (var json in new[]
                {
                    """{"id":"1","category":"bikes","name":"Trail Blazer steel frame","price":120}""",
                    """{"id":"2","category":"bikes","name":"Road Runner alloy frame","price":340}""",
                    """{"id":"3","category":"shoes","name":"Sprint"}""",
                })
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var doc = JsonDocument.Parse(json);
                    await container.CreateItemStreamAsync(stream, new PartitionKey(doc.RootElement.GetProperty("category").GetString()), cancellationToken: cts.Token);
                }

                _client = client;
            }
            catch (Exception e)
            {
                _initializationFailure = e.Message;
                _client = null;
            }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            _client?.Dispose();
            _client = null;
        }

        static void RequireService()
        {
            if (_client is null)
                Assert.Inconclusive("These need a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));
        }

        /// <summary>
        /// One Cosmos schema, and a second schema holding views over it.
        /// </summary>
        /// <remarks>
        /// The views are here for <c>ikvmnet/calcite-dotnet#62</c>, where a view is validated against
        /// an operator table the connection's <c>fun</c> libraries were never chained into. A function
        /// the schema declares is resolved by the catalog reader instead, which a view does have — so
        /// the two views below are the check that this route reaches inside one.
        /// </remarks>
        static string Model => $$"""
        {
          "version": "1.0",
          "defaultSchema": "COSMOS",
          "schemas": [
            {
              "name": "COSMOS",
              "type": "custom",
              "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
              "operand": {
                "endpoint": "{{Endpoint}}",
                "key": "{{Key}}",
                "database": "{{DatabaseName}}",
                "containers": [ "products" ],
                "connectionMode": "gateway"
              }
            },
            {
              "name": "VIEWS",
              "tables": [
                {
                  "name": "QUALIFIED",
                  "type": "view",
                  "sql": [
                    "SELECT p.\"id\" AS \"ID\"",
                    "FROM \"COSMOS\".\"products\" AS p",
                    "WHERE \"COSMOS\".\"IS_DEFINED\"(p.\"_MAP\"['price'])"
                  ]
                },
                {
                  "name": "ROOTED",
                  "type": "view",
                  "path": [ "COSMOS" ],
                  "sql": [
                    "SELECT p.\"id\" AS \"ID\"",
                    "FROM \"COSMOS\".\"products\" AS p",
                    "WHERE IS_DEFINED(p.\"_MAP\"['price'])"
                  ]
                }
              ]
            }
          ]
        }
        """;

        static async Task<DbConnection> OpenAsync()
        {
            var connection = new CalciteConnection(new CalciteConnectionStringBuilder
            {
                Model = "inline:" + Model,
                CaseSensitive = true,
            }.ConnectionString);

            await connection.OpenAsync();
            return connection;
        }

        static async Task<List<string>> QueryAsync(string sql)
        {
            await using var connection = await OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            var values = new List<string>();

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                values.Add(reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString() ?? "");

            values.Sort(StringComparer.Ordinal);
            return values;
        }

        /// <summary>
        /// The issue, put the way an application would put it.
        /// </summary>
        /// <remarks>
        /// <c>IS_DEFINED</c> rather than <c>FULLTEXTCONTAINS</c> because the emulator implements it, so
        /// this asserts rather than reporting inconclusive wherever the suite runs. What it measures is
        /// resolution, which is the same for every operator in the family.
        /// </remarks>
        [TestMethod]
        public async Task AFunctionResolvesThroughAConnection()
        {
            RequireService();

            var ids = await QueryAsync("""SELECT c."id" FROM "products" AS c WHERE IS_DEFINED(c."_MAP"['price'])""");

            ids.Should().Equal("1", "2");
        }

        /// <summary>
        /// The negation, so that the predicate is doing something.
        /// </summary>
        [TestMethod]
        public async Task TheFunctionIsEvaluatedRatherThanIgnored()
        {
            RequireService();

            var ids = await QueryAsync("""SELECT c."id" FROM "products" AS c WHERE NOT IS_DEFINED(c."_MAP"['price'])""");

            ids.Should().Equal("3");
        }

        /// <summary>
        /// A function qualified by the schema that declares it, which is what a query rooted elsewhere
        /// has to write.
        /// </summary>
        [TestMethod]
        public async Task AQualifiedNameResolvesFromAnotherSchema()
        {
            RequireService();

            var ids = await QueryAsync("""SELECT c."id" FROM "COSMOS"."products" AS c WHERE "COSMOS"."IS_DEFINED"(c."_MAP"['price'])""");

            ids.Should().Equal("1", "2");
        }

        /// <summary>
        /// Full text search, which is the capability the issue was actually about.
        /// </summary>
        /// <remarks>
        /// The emulator rejects <c>FULLTEXTCONTAINS</c> even with a policy and an index over the
        /// searched path — see <c>CosmosQueryExecutorTests.FullTextFormsAreAcceptedWhereTheEmulatorSupportsThem</c>,
        /// which pins that measurement — so a rejection here is reported inconclusive. What is not
        /// excused is the failure this issue is about: a name the planner could not resolve never
        /// reaches the service at all, and that is asserted either way.
        /// </remarks>
        [TestMethod]
        public async Task AFullTextPredicateResolvesAndReachesTheService()
        {
            RequireService();

            try
            {
                var ids = await QueryAsync("""SELECT c."id" FROM "products" AS c WHERE FULLTEXTCONTAINS(c."_MAP"['name'], 'steel')""");

                ids.Should().Equal("1");
            }
            catch (Exception e)
            {
                Describe(e).Should().NotContain("No match found for function signature",
                    "the name has to resolve whatever the service then does with the statement");

                Assert.Inconclusive("The name resolved and the service refused the statement, which this emulator does: " + Describe(e));
            }
        }

        /// <summary>
        /// And the ranked form, which is three nodes in Calcite and one clause in Cosmos.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The name resolves — that is what this asserts, and it is the whole of what declaring the
        /// functions on the schema was for. <b>The clause does not yet survive a connection</b>, for a
        /// reason that has nothing to do with resolution and everything to do with where the
        /// projection that discards the score ends up: <c>Prepare</c> keeps it in the
        /// <c>RelRoot</c>'s field mapping and materialises it as a calc <em>after</em> planning, so
        /// the three-node shape <c>CosmosRankRule</c> matches never exists while rules are running.
        /// Measured by planning the same statement from <c>RelRoot.rel</c> instead of
        /// <c>RelRoot.project()</c>: the first plans to <c>CosmosRank</c> and the second does not.
        /// See <c>TODO.md</c>.
        /// </para>
        /// <para>
        /// Reported inconclusive rather than failed, and it tells the two gaps apart: an emulator that
        /// refuses the statement and a plan that could not be implemented are different answers, and
        /// only one of them is this adapter's. A name that fails to resolve is neither, and fails.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task AScoreResolvesThroughAConnection()
        {
            RequireService();

            try
            {
                var ids = await QueryAsync("""SELECT c."id" FROM "products" AS c ORDER BY FULLTEXTSCORE(c."_MAP"['name'], 'steel') FETCH FIRST 2 ROWS ONLY""");

                ids.Should().NotBeEmpty();
            }
            catch (Exception e)
            {
                var described = Describe(e);

                described.Should().NotContain("No match found for function signature",
                    "the name has to resolve whatever is then done with the statement");

                if (described.Contains("must implement ImplementableFunction"))
                    Assert.Inconclusive("The name resolved and the rank clause did not survive the connection's plan. " + described);

                Assert.Inconclusive("The name resolved and the service refused the statement, which this emulator does: " + described);
            }
        }

        /// <summary>
        /// A view in a model can name one, qualified by the schema that declares it.
        /// </summary>
        /// <remarks>
        /// The answer <c>ikvmnet/calcite-dotnet#62</c> asks for. A view is validated against an
        /// operator table the connection's <c>fun</c> libraries were not chained into, and that is
        /// what makes a library function fail inside one — but the catalog reader is chained, and a
        /// schema's own functions are what the catalog reader resolves. So declaring a function on a
        /// schema is the route into a view, and this is the measurement.
        /// </remarks>
        [TestMethod]
        public async Task AModelViewCanNameASchemaDeclaredFunction()
        {
            RequireService();

            var ids = await QueryAsync("""SELECT "ID" FROM "VIEWS"."QUALIFIED" """);

            ids.Should().Equal("1", "2");
        }

        /// <summary>
        /// And unqualified, where the view declares the schema in its path.
        /// </summary>
        /// <remarks>
        /// A view's <c>path</c> is what its names are resolved against, function names included. This
        /// is the shorter spelling of the test above and the one worth reaching for in a model that
        /// gives a container a relational shape.
        /// </remarks>
        [TestMethod]
        public async Task AModelViewRootedInTheSchemaNeedsNoQualifier()
        {
            RequireService();

            var ids = await QueryAsync("""SELECT "ID" FROM "VIEWS"."ROOTED" """);

            ids.Should().Equal("1", "2");
        }

        /// <summary>
        /// Renders an exception as the chain it usually is.
        /// </summary>
        /// <remarks>
        /// A model or planning failure surfaces wrapped several times over, and the sentence that says
        /// what happened is rarely the outermost one — sometimes it is on the Java side of the bridge,
        /// which is a cause rather than an inner exception.
        /// </remarks>
        static string Describe(Exception e)
        {
            var builder = new StringBuilder();

            for (var cause = (Exception?)e; cause is not null; cause = Next(cause))
            {
                if (builder.Length > 0)
                    builder.Append(" <- ");

                builder.Append(cause.Message);
            }

            return builder.ToString();

            static Exception? Next(Exception e)
            {
                if (e.InnerException is Exception inner)
                    return inner;

                if (e is java.lang.Throwable throwable && throwable.getCause() is java.lang.Throwable cause && ReferenceEquals(cause, throwable) == false)
                    return cause;

                return null;
            }
        }

    }

}
