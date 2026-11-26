using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Text.Json;
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
    /// A side the declaration empties, beside a side Calcite fails on, through the path the README documents: a model, a
    /// connection, and the host's planner with Calcite's default rules, which prune a join, set operation, semi-join or
    /// decorrelated lateral over an input they read as empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each statement filters one side by a <c>kind</c> the declaration rules out, whose request is not sent, and casts
    /// the text <c>body</c> to <c>INTEGER</c> on the other, which fails on <c>"abc"</c>. Calcite reads the other side and
    /// fails. When the reduce answered with an empty <c>Values</c>, the host's prune-empty rules read it as empty and
    /// answered the whole statement with no rows and no request. Nothing in the plan stands for an empty answer now: the
    /// side is pushed as any other and left unsent (<see cref="CosmosQuery.SendsNothing"/>), and the other side is read as
    /// Calcite reads it.
    /// </para>
    /// <para>
    /// The control is the same statement over the same container declared nothing, in a second schema of the same model:
    /// what the host evaluates knowing nothing of the documents, which fails as Calcite does.
    /// </para>
    /// <para>
    /// One semi-join is an exception. One over a subquery whose request is left unsent is planned as a join the adapter
    /// drives by the subquery's keys, with the cast above it, as one whose subquery merely matches nothing is: the lookup
    /// join's, not the declaration's. One whose outer side sends nothing is planned as its undeclared twin, the
    /// subquery read first, and fails as the twin does; Calcite's own plan through this connection, a hash semi-join
    /// reading the empty outer side first, answers no rows (<c>docs/23</c>).
    /// </para>
    /// <para>
    /// The container holds a hundred documents, the number Calcite plans a container of no known count as, so a plan is the
    /// one these tests mean whether the service counts them at once (the emulator's EN20261123), reports zero (its releases
    /// before) or reports zero for a while (Azure). At the three documents the statements are about it is not: planned at
    /// 3 or 4 rows <c>sj2</c> and <c>j6</c> keep the filter by <c>kind</c> above their lookup join, and at 3 to 11
    /// <c>sj1</c>'s undeclared twin keeps the cast there. Measured on EN20261123, each statement plans alike in each schema
    /// from 12 documents to 2,000, and as it does on EN20261019 and EN20260907 with no count. The other ninety-seven are as
    /// small, a document's size moving a plan as a count does, and hold nothing a statement raises on or keeps: their
    /// <c>kind</c> is declared, and their <c>body</c> is a number other than 1.
    /// </para>
    /// </remarks>
    public class CosmosConnectionEmptySideTests : IClassFixture<CosmosConnectionEmptySideTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await ClassInitialize();

            public ValueTask DisposeAsync() { ClassCleanup(); return default; }

        }

        static string Endpoint => CosmosEmulator.Endpoint!;
        static string Key => CosmosEmulator.Key!;

        static bool IsEmulator => CosmosEmulator.IsEmulator;

        /// <summary>
        /// The database this fixture builds, named per target framework, since every framework may run at once against
        /// one account.
        /// </summary>
        static readonly string DatabaseName = "calcite_cosmos_sides_" +
            System.Text.RegularExpressions.Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary><c>kind</c> is never <c>C</c>, <c>body</c> is any string, and <c>n</c> is 1 or 2.</summary>
        const string Schema = """{ "type": "object", "required": ["kind", "body", "n"], "properties": { "kind": { "enum": ["A", "B"] }, "body": { "type": "string" }, "n": { "enum": [1, 2] } } }""";

        /// <summary>How many documents the container holds: what Calcite plans a container of no known count as.</summary>
        const int Documents = 100;

        static CosmosClient? _client;
        static string? _initializationFailure;

        static async Task ClassInitialize()
        {
            // A model names its schema factory by type name, resolved through IKVM, and only finds it if the assembly is
            // loaded.
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

                try { await database.GetContainer("sides").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("sides", "/category"), cancellationToken: cts.Token)).Container;

                // One body that is no number, which a cast to INTEGER fails on, beside two that are.
                var stated = new[]
                {
                    """{ "id": "r1", "category": "a", "kind": "A", "body": "abc", "n": 1 }""",
                    """{ "id": "r2", "category": "a", "kind": "B", "body": "1", "n": 2 }""",
                    """{ "id": "r3", "category": "b", "kind": "A", "body": "1", "n": 2 }""",
                };

                foreach (var json in stated)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var doc = JsonDocument.Parse(json);
                    await container.CreateItemStreamAsync(stream, new PartitionKey(doc.RootElement.GetProperty("category").GetString()), cancellationToken: cts.Token);
                }

                // And the rest of the hundred, in one request.
                var rest = container.CreateTransactionalBatch(new PartitionKey("c"));
                for (var i = stated.Length; i < Documents; i++)
                    rest.UpsertItem(new { id = $"f{i}", category = "c", kind = "B", body = "0", n = 2 });

                using (var written = await rest.ExecuteAsync(cts.Token))
                    if (written.IsSuccessStatusCode == false)
                        throw new InvalidOperationException($"The rest of the documents were not written: {written.StatusCode} {written.ErrorMessage}");

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

        /// <summary>
        /// One container twice: declared in <c>COSMOS</c>, and declared nothing in <c>PLAIN</c>.
        /// </summary>
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
                "serviceTarget": "{{CosmosEmulator.ServiceTargetOperand}}",
                "database": "{{DatabaseName}}",
                "containers": [ { "name": "sides", "schema": {{Schema}} } ],
                "connectionMode": "gateway"
              }
            },
            {
              "name": "PLAIN",
              "type": "custom",
              "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
              "operand": {
                "endpoint": "{{Endpoint}}",
                "key": "{{Key}}",
                "serviceTarget": "{{CosmosEmulator.ServiceTargetOperand}}",
                "database": "{{DatabaseName}}",
                "containers": [ "sides" ],
                "connectionMode": "gateway"
              }
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

            // Each schema reasons in a reasoning of its own, whose S build is awaited before anything plans.
            await connection.OpenAsync();
            await TestReasoning.Ready(connection, "COSMOS", "sides");
            await TestReasoning.Ready(connection, "PLAIN", "sides");
            return connection;
        }

        /// <summary>
        /// Each statement over a table, named for the probe that found it: the side filtered by <c>kind = 'C'</c> is the
        /// one the declaration empties, and the other casts <c>body</c>.
        /// </summary>
        static string Statement(string name, string t)
        {
            const string Kind = """JSON_VALUE(c."DOC", '$.kind') = 'C'""";
            const string Body = """CAST(JSON_VALUE(d."DOC", '$.body') AS INTEGER)""";

            return name switch
            {
                "ix3" => $"""SELECT c."id" FROM {t} AS c WHERE {Kind} INTERSECT ALL SELECT d."id" FROM {t} AS d WHERE {Body} = 1""",
                "ex1" => $"""SELECT c."id" FROM {t} AS c WHERE {Kind} EXCEPT SELECT d."id" FROM {t} AS d WHERE {Body} = 1""",
                "sj1" => $"""SELECT d."id" FROM {t} AS d WHERE {Body} = 1 AND d."id" IN (SELECT c."id" FROM {t} AS c WHERE {Kind})""",
                "sj1none" => $"""SELECT d."id" FROM {t} AS d WHERE {Body} = 1 AND d."id" IN (SELECT c."id" FROM {t} AS c WHERE c."id" = 'zz')""",
                "sj2" => $"""SELECT c."id" FROM {t} AS c WHERE {Kind} AND c."id" IN (SELECT d."id" FROM {t} AS d WHERE {Body} = 1)""",
                "j3" => $"""SELECT c."id" FROM {t} AS c WHERE {Kind} AND EXISTS (SELECT 1 FROM {t} AS d WHERE d."id" = c."id" AND {Body} = 1)""",
                "j6" => $"""SELECT c."id" FROM {t} AS c WHERE {Kind} AND c."id" IN (SELECT CAST({Body} AS VARCHAR) FROM {t} AS d)""",
                "lj1" => $"""SELECT a."id", b.w FROM (SELECT c."id" FROM {t} AS c WHERE {Kind}) AS a LEFT JOIN (SELECT d."id", {Body} AS w FROM {t} AS d) AS b ON (a."id" = b."id")""",
                "jl10" => $"""SELECT COUNT(*) AS n FROM (SELECT c."id" FROM {t} AS c WHERE {Kind}) AS a JOIN (SELECT d."id" FROM {t} AS d WHERE {Body} = 1) AS b ON (a."id" = b."id")""",
                "lat3" => $"""SELECT c."id", l.w FROM {t} AS c CROSS JOIN LATERAL (SELECT CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER) AS w, JSON_VALUE(c."DOC", '$.kind') AS k2 FROM (VALUES (1)) AS v(x)) AS l WHERE l.k2 = 'C'""",
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such statement."),
            };
        }

        static async Task<(Exception? Raised, string Plan)> Run(DbConnection connection, string sql)
        {
            string plan;
            await using (var explain = connection.CreateCommand())
            {
                explain.CommandText = "EXPLAIN PLAN FOR " + sql;
                plan = (string?)await explain.ExecuteScalarAsync() ?? "";
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                }

                return (null, plan);
            }
            catch (Exception e)
            {
                return (e, plan);
            }
        }

        static string Innermost(Exception e)
        {
            while (e.InnerException is not null)
                e = e.InnerException;

            return e.GetType().Name + ": " + e.Message;
        }

        /// <summary>
        /// The side the declaration empties is pushed and sends nothing, as a statement or as the probes of a join the
        /// adapter drives by the other side's keys, and the other side, which Calcite reads and fails on, is read and fails
        /// as Calcite fails, as its undeclared twin does.
        /// </summary>
        [Theory]
        [InlineData("ix3")]
        [InlineData("ex1")]
        [InlineData("lj1")]
        [InlineData("jl10")]
        [InlineData("lat3")]
        public async Task ASideCalciteFailsOnBesideAnEmptiedSideFailsAsCalciteDoes(string name)
        {
            RequireService();

            await using var connection = await OpenAsync();

            var plain = await Run(connection, Statement(name, "\"PLAIN\".\"sides\""));
            plain.Raised.Should().NotBeNull("the control: knowing nothing of the documents, the host casts \"abc\" as Calcite does\n" + plain.Plan);

            var declared = await Run(connection, Statement(name, "\"COSMOS\".\"sides\""));
            declared.Plan.Should().ContainAny(new[] { "sends=[nothing]", "probes=[nothing]" }, "the declaration empties the side filtered by kind");
            declared.Raised.Should().NotBeNull("the other side is read, and fails as Calcite fails\n" + declared.Plan + "\nundeclared:\n" + plain.Plan);
            Innermost(declared.Raised!).Should().Be(Innermost(plain.Raised!));
        }

        /// <summary>
        /// A semi-join whose subquery the declaration empties is no longer pruned, but its unsent request does not decide
        /// what it answers: the declaration also keeps the cast of <c>body</c> from the service, so reading every document
        /// to cast it costs more than fetching the subquery's keys, and the plan chosen is a join the adapter drives by
        /// those keys (<c>CosmosLookupJoin</c>), with the cast above it as the query wrote it, evaluated on the
        /// documents the keys fetch. The same statement whose subquery matches nothing with nothing reduced, a key no
        /// document holds, is planned and answered alike. The undeclared twin pushes the cast beneath its join and
        /// fails on every document (<c>TODO.md</c>, <em>A filter above a lookup join</em>).
        /// </summary>
        [Fact]
        public async Task ASemiJoinOverAnEmptiedSubqueryAnswersAsOneWithNothingReduced()
        {
            RequireService();

            await using var connection = await OpenAsync();

            var plain = await Run(connection, Statement("sj1", "\"PLAIN\".\"sides\""));
            plain.Raised.Should().NotBeNull("the control pushes the cast beneath its join\n" + plain.Plan);

            var reduced = await Run(connection, Statement("sj1", "\"COSMOS\".\"sides\""));
            reduced.Plan.Should().Contain("sends=[nothing]").And.Contain("CosmosLookupJoin", reduced.Plan);

            var none = await Run(connection, Statement("sj1none", "\"COSMOS\".\"sides\""));
            none.Plan.Should().NotContain("sends=[nothing]").And.Contain("CosmosLookupJoin", none.Plan);

            (reduced.Raised is null).Should().Be(none.Raised is null, "the join, not the declaration, decides where the cast is evaluated\n" + reduced.Plan + "\n" + none.Plan);
        }

        /// <summary>
        /// A semi-join or correlated <c>EXISTS</c> whose outer side the declaration empties is planned as its undeclared
        /// twin is: a join the adapter drives by the subquery's keys, the subquery read first, its probes into the outer
        /// side not sent. The subquery casts <c>body</c>, in its condition or its select list, and fails as the twin fails.
        /// Calcite's own plan through this connection, with no rule of the adapter's, is a hash semi-join that reads the
        /// empty outer side first, never opens the subquery and answers no rows, as the host's semi-join did over the empty
        /// relation the reduce put in the outer side's place: pushdown reads the subquery first, with a schema or without
        /// (<c>docs/23</c>).
        /// </summary>
        [Theory]
        [InlineData("sj2")]
        [InlineData("j3")]
        [InlineData("j6")]
        public async Task ASemiJoinOverAnEmptiedOuterSideFailsAsItsUndeclaredTwinDoes(string name)
        {
            RequireService();

            await using var connection = await OpenAsync();

            var plain = await Run(connection, Statement(name, "\"PLAIN\".\"sides\""));
            plain.Raised.Should().NotBeNull("the control reads the subquery first and casts \"abc\"\n" + plain.Plan);

            var declared = await Run(connection, Statement(name, "\"COSMOS\".\"sides\""));
            declared.Plan.Should().ContainAny(new[] { "sends=[nothing]", "probes=[nothing]" }, "the declaration empties the outer side");
            declared.Raised.Should().NotBeNull("the subquery is read first, and fails as the twin fails\n" + declared.Plan + "\nundeclared:\n" + plain.Plan);
            Innermost(declared.Raised!).Should().Be(Innermost(plain.Raised!));
        }

    }

}
