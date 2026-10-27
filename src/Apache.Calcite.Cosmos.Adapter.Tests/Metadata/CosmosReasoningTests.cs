using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Data;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using JavaTypeFactoryImpl = org.apache.calcite.jdbc.JavaTypeFactoryImpl;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.util;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// What a schema learns hangs off the schema (<see cref="CosmosReasoning"/>): one reasoning a schema object, shared
    /// as the schema is shared, gone when it goes, and none for a theory or a container no schema has bound.
    /// </summary>
    /// <remarks>
    /// The data source tests need a service, because a model builds its schema by reading container definitions; they
    /// report inconclusive where none is reachable. The database is named per target framework, as the other fixtures'.
    /// </remarks>
    public sealed class CosmosReasoningTests
    {

        const string Sql = "SELECT c.\"id\" FROM c AS c WHERE c.\"id\" = 'x' AND JSON_VALUE(c.\"DOC\", '$.kind') = 'k'";

        static readonly string DatabaseName = "calcite_cosmos_reasoning_" +
            System.Text.RegularExpressions.Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        static readonly SemaphoreSlim Seeding = new(1, 1);
        static bool _seeded;
        static string? _unavailable;

        static CosmosContainerMetadata Unbound(string name) => new(name, new[] { "/pk" });

        static CosmosTable Table(org.apache.calcite.schema.Schema schema, string name) => (CosmosTable)schema.tables().get(name)!;

        static org.apache.calcite.schema.Schema Database(org.apache.calcite.schema.Schema schema, string name) => (org.apache.calcite.schema.Schema)schema.subSchemas().get(name)!;

        static void Collect()
        {
            for (var i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            GC.Collect();
        }

        /// <summary>
        /// A schema let go takes its graphs with it: a container bound as the factory binds one, planned once with a filter
        /// the oracle reads and once through a data source, then dropped, leaves nothing it learned reachable — not its
        /// reasoning, its generations, their graph, the bound theory nor its oracle. Main kept every one of them: the
        /// generations' binding table, keyed on the static service facts, held the bound copy and through it the table.
        /// </summary>
        [Fact]
        public void ASchemaLetGoTakesItsGraphsWithIt()
        {
            var held = PlanAndLetGo();
            Collect();

            held.Where(h => h.Reference.IsAlive).Select(h => h.Name).Should().BeEmpty("nothing the schema learned outlives it");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (string Name, WeakReference Reference)[] PlanAndLetGo()
        {
            var reasoning = new CosmosReasoning();
            var undeclared = CosmosContainerMetadataReader.FromProperties(new ContainerProperties("c", "/pk")).WithReasoning(reasoning);
            var declared = CosmosContainerMetadataReader.FromProperties(new ContainerProperties("c", "/pk")).WithReasoning(reasoning).WithFacts([Outright(P("kind"), Present)]);

            var operators = SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Adapter.Sql.CosmosOperators.Instance);
            PlannerHarness.Plan(Sql, [undeclared], operators, collations: true, calc: true);
            PlannerHarness.Plan(Sql, [declared], operators, collations: true, calc: true);

            // And through a data source, as a host adds a schema of its own: the connection planned, then all of it dropped.
            var schema = new CosmosSchema([CosmosContainerMetadataReader.FromProperties(new ContainerProperties("c", "/pk"))]);
            using (var dataSource = new CalciteDataSourceBuilder(new CalciteConnectionStringBuilder { CaseSensitive = true }.ConnectionString).AddSchema("COSMOS", schema).Build())
            using (var connection = dataSource.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "EXPLAIN PLAN FOR SELECT c.\"id\" FROM \"COSMOS\".\"c\" AS c WHERE c.\"id\" = 'x'";
                command.ExecuteScalar().Should().NotBeNull();
            }

            var generation = reasoning.Facts.Current;
            var hosted = Table(schema, "c").Container.Reasoning!;
            (generation.Number, reasoning.Facts.Started, hosted.Facts.Started).Should().Be((1, 1, 1), "each plan compiled its containers into its schema's first generation");

            return
            [
                ("reasoning", new WeakReference(reasoning)),
                ("generations", new WeakReference(reasoning.Facts)),
                ("generation", new WeakReference(generation)),
                ("graph", new WeakReference(generation.Contexts.Graph)),
                ("service facts, bound", new WeakReference(undeclared.Facts)),
                ("declared theory", new WeakReference(declared.Facts)),
                ("oracle", new WeakReference(CosmosOracle.For(declared.Facts))),
                ("the hosted schema's reasoning", new WeakReference(hosted)),
                ("the hosted schema's graph", new WeakReference(hosted.Facts.Current.Contexts.Graph)),
                ("the hosted schema", new WeakReference(schema)),
            ];
        }

        /// <summary>
        /// Each schema object reasons in its own: a host's schema binds the containers handed to it unbound to one it
        /// makes, an account one for all its databases, a table built alone its own; metadata a schema bound keeps that
        /// one, and the factory's path binds to the one it is given and makes none. Undeclared containers share the bound
        /// service facts, so one context and one oracle. The bound copy is another object than the one handed in: the
        /// executor factory is handed it, and a refresh of the host's own object does not reach it.
        /// </summary>
        [Fact]
        public void EachSchemaObjectReasonsInItsOwn()
        {
            var first = new CosmosSchema([Unbound("a"), Unbound("b").WithFacts([Outright(P("kind"), Present)])]);
            var second = new CosmosSchema([Unbound("a")]);
            var reasoning = Table(first, "a").Container.Reasoning!;

            reasoning.Should().NotBeNull();
            Table(first, "b").Container.Reasoning.Should().BeSameAs(reasoning, "a schema's containers share one");
            Table(first, "b").Container.Facts.Generations.Should().BeSameAs(reasoning.Facts, "a declared container reasons in its schema's graphs");
            Table(second, "a").Container.Reasoning.Should().NotBeSameAs(reasoning, "two schemas, two reasonings");
            Table(first, "a").Container.Facts.Should().BeSameAs(reasoning.ServiceFacts, "an undeclared container takes the bound service facts");
            var beside = Table(new CosmosSchema([Unbound("z")], reasoning), "z");
            CosmosOracle.For(beside.Container.Facts).Should().BeSameAs(CosmosOracle.For(Table(first, "a").Container.Facts), "undeclared containers of one reasoning share one oracle");

            var account = new CosmosAccountSchema([new("x", [Unbound("a")]), new("y", [Unbound("b")])]);
            var shared = Table(Database(account, "x"), "a").Container.Reasoning!;
            Table(Database(account, "y"), "b").Container.Reasoning.Should().BeSameAs(shared, "an account's databases share one");
            shared.Should().NotBeSameAs(reasoning);

            var given = Unbound("c");
            var bare = new CosmosTable(given);
            given.Reasoning.Should().BeNull("a table built alone binds a copy");
            bare.Container.Should().NotBeSameAs(given);
            bare.Container.Reasoning.Should().NotBeNull("and owns its reasoning");
            bare.Container.Reasoning.Should().NotBeSameAs(reasoning).And.NotBeSameAs(shared);
            bare.Convention.Container.Should().BeSameAs(bare.Container, "the convention owns the copy the table plans with");

            var kept = Table(first, "a").Container;
            new CosmosTable(kept).Container.Should().BeSameAs(kept, "metadata a schema bound is kept as it is");
            var mixed = new CosmosSchema([kept, Unbound("d")]);
            Table(mixed, "a").Container.Reasoning.Should().BeSameAs(reasoning);
            Table(mixed, "d").Container.Reasoning.Should().NotBeSameAs(reasoning, "the containers handed in unbound are bound to one of the schema's own");
            kept.WithStatistics(new CosmosContainerStatistics(1, 1, 1)).Reasoning.Should().BeSameAs(reasoning, "a copy carries it");

            var factory = new CosmosReasoning();
            Table(new CosmosSchema([Unbound("a")], factory), "a").Container.Reasoning.Should().BeSameAs(factory);
            var factoryAccount = new CosmosAccountSchema([new("x", [Unbound("a")]), new("y", [Unbound("b")])], factory);
            Table(Database(factoryAccount, "x"), "a").Container.Reasoning.Should().BeSameAs(factory, "the factory's account binds every database to the one it makes");
            Table(Database(factoryAccount, "y"), "b").Container.Reasoning.Should().BeSameAs(factory);

            var handed = new List<CosmosContainerMetadata>();
            var fetches = 0;
            var counted = Unbound("e").WithStatisticsProvider(() => new CosmosContainerStatistics(++fetches * 10, 100, 1));
            var hosted = new CosmosSchema([counted], container => { handed.Add(container); return null!; });
            var table = Table(hosted, "e");
            handed.Should().Equal([table.Container], "the executor factory is handed the bound copy");
            _ = table.getStatistic().getRowCount();
            counted.RefreshStatistics();
            _ = table.getStatistic().getRowCount();
            fetches.Should().Be(1, "the host's own object is not the copy the table reads");
            hosted.RefreshStatistics();
            _ = table.getStatistic().getRowCount();
            fetches.Should().Be(2, "the schema's refresh reaches the copy");
        }

        /// <summary>
        /// A theory, an oracle or metadata no schema has bound reasons nowhere, and says so: an
        /// <see cref="ArgumentException"/>, which no caller reads as an engine's failure, so a missed binding fails a plan
        /// rather than reading as "unknown". The empty theory states nothing without asking a graph.
        /// </summary>
        [Fact]
        public void NoTheoryReasonsUnbound()
        {
            static void Fails(Action act)
            {
                var e = act.Should().Throw<ArgumentException>().WithMessage("*bound to no schema's reasoning*").Which;
                JsonFactTheory.IsEngineFailure(e).Should().BeFalse("an engine's failure would read as knowing nothing");
            }

            var theory = Theory(Outright(P("a"), Present));
            theory.Generations.Should().BeNull();
            Fails(() => _ = theory.Declared);
            Fails(() => theory.Declaration());
            Fails(() => CosmosOracle.For(theory));
            Fails(() => _ = new CosmosOracle(theory));
            Fails(() => CosmosOracle.For(JsonFactTheory.Empty));
            JsonFactTheory.Empty.Declared.Should().BeSameAs(JsonFactSet.Empty, "the empty theory states nothing, and asks no graph to say so");

            var unbound = Unbound("c");
            unbound.Reasoning.Should().BeNull();
            unbound.Facts.Generations.Should().BeNull();
            unbound.WithFacts([Outright(P("a"), Present)]).Facts.Generations.Should().BeNull("facts added to unbound metadata stay unbound");
            Fails(() => CosmosOracle.For(unbound.Facts));
            Fails(() => new CosmosImplementor(new RexBuilder(new JavaTypeFactoryImpl()), unbound).FactsOf(null));

            var bound = unbound.WithReasoning(new CosmosReasoning());
            new CosmosImplementor(new RexBuilder(new JavaTypeFactoryImpl()), bound).FactsOf(null).Should().NotBeNull("bound, it reads its declaration");
        }

        /// <summary>
        /// Connections of one data source plan against one schema and so one reasoning: a pooled data source's
        /// connections, a host's own data source's, and a schema added with <c>AddSchema</c>, across <c>Clear</c> and under
        /// <c>Pooling=false</c> alike.
        /// </summary>
        [Fact]
        public async Task ConnectionsOfOneDataSourceShareOneReasoning()
        {
            await RequireService();

            var pooled = ConnectionString(pooling: true);
            try
            {
                (await ReasoningOf(() => new CalciteConnection(pooled))).Should().BeSameAs(await ReasoningOf(() => new CalciteConnection(pooled)), "a pooled data source's connections share its schema");
            }
            finally
            {
                using var clearing = new CalciteConnection(pooled);
                CalciteConnection.ClearPool(clearing);
            }

            using (var owned = new CalciteDataSource(ConnectionString(pooling: true)))
                (await ReasoningOf(owned.CreateConnection)).Should().BeSameAs(await ReasoningOf(owned.CreateConnection), "a host's data source's connections share its schema");

            var added = new CosmosSchema([Unbound("items")]);
            foreach (var pooling in new[] { true, false })
            {
                using var dataSource = new CalciteDataSourceBuilder(new CalciteConnectionStringBuilder { CaseSensitive = true, Pooling = pooling }.ConnectionString).AddSchema("COSMOS", added).Build();
                var before = await ReasoningOf(dataSource.CreateConnection);
                dataSource.Clear();
                (await ReasoningOf(dataSource.CreateConnection)).Should().BeSameAs(before, $"a schema added once is added to every root (Pooling={pooling})");
                before.Should().BeSameAs(Table(added, "items").Container.Reasoning);
            }
        }

        /// <summary>
        /// Two data sources reason apart, and so does every root a model is read into again: two pooled connection
        /// strings, each connection under <c>Pooling=false</c>, a host's data source after <c>Clear</c>, and a step that
        /// builds its schema per root.
        /// </summary>
        [Fact]
        public async Task TwoDataSourcesReasonApart()
        {
            await RequireService();

            var (one, two) = (ConnectionString(pooling: true), ConnectionString(pooling: true));
            try
            {
                (await ReasoningOf(() => new CalciteConnection(one))).Should().NotBeSameAs(await ReasoningOf(() => new CalciteConnection(two)), "two connection strings, two data sources");
            }
            finally
            {
                foreach (var pooled in new[] { one, two })
                {
                    using var clearing = new CalciteConnection(pooled);
                    CalciteConnection.ClearPool(clearing);
                }
            }

            var unpooled = ConnectionString(pooling: false);
            (await ReasoningOf(() => new CalciteConnection(unpooled))).Should().NotBeSameAs(await ReasoningOf(() => new CalciteConnection(unpooled)), "under Pooling=false each connection builds its root");

            using (var owned = new CalciteDataSource(ConnectionString(pooling: true)))
            {
                var before = await ReasoningOf(owned.CreateConnection);
                owned.Clear();
                (await ReasoningOf(owned.CreateConnection)).Should().NotBeSameAs(before, "a cleared data source reads its model into a new root");
            }

            var step = new CalciteDataSourceBuilder(new CalciteConnectionStringBuilder { CaseSensitive = true }.ConnectionString)
                .ConfigureRootSchema(root => root.add("COSMOS", new CosmosSchema([Unbound("items")])));
            using (var a = step.Build())
            using (var b = step.Build())
                (await ReasoningOf(a.CreateConnection)).Should().NotBeSameAs(await ReasoningOf(b.CreateConnection), "a step builds a schema per root");
        }

        /// <summary>
        /// The reasoning a connection's <c>COSMOS</c>.<c>items</c> table plans in; the connection is opened, read and
        /// disposed, and the reasoning read off the root it planned against.
        /// </summary>
        static async Task<CosmosReasoning> ReasoningOf(Func<DbConnection> connect)
        {
            await using var connection = connect();
            await connection.OpenAsync();
            var root = ((CalciteConnection)connection).RootSchema;
            return Table(Database(root, "COSMOS"), "items").Container.Reasoning!;
        }

        /// <summary>A model over the fixture's database, in a connection string no other test opens.</summary>
        static string ConnectionString(bool pooling) => new CalciteConnectionStringBuilder
        {
            Model = "inline:" + $$"""
                {
                  "version": "1.0",
                  "defaultSchema": "COSMOS",
                  "schemas": [ { "name": "COSMOS", "type": "custom",
                    "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
                    "operand": { "endpoint": "{{CosmosEmulator.Endpoint}}", "key": "{{CosmosEmulator.Key}}", "database": "{{DatabaseName}}",
                                 "containers": [ "items" ], "connectionMode": "gateway" } } ]
                }
                """,
            CaseSensitive = true,
            Pooling = pooling,
            ConnectionIdleLifetime = 3600 + Interlocked.Increment(ref _strings),
        }.ConnectionString;

        static int _strings;

        /// <summary>
        /// Creates the fixture's database and container once, or reports inconclusive where no account answers.
        /// </summary>
        static async Task RequireService()
        {
            CosmosEmulator.RequireAccount();

            await Seeding.WaitAsync();
            try
            {
                if (_seeded == false && _unavailable is null)
                {
                    // A model names its factory by type name, resolved through IKVM, and finds it only if the assembly is
                    // loaded.
                    _ = new CosmosSchemaFactory();

                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 30 : 120));
                        using var client = new CosmosClient(CosmosEmulator.Endpoint, CosmosEmulator.Key, new CosmosClientOptions
                        {
                            ConnectionMode = ConnectionMode.Gateway,
                            LimitToEndpoint = CosmosEmulator.IsEmulator,
                            RequestTimeout = TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 5 : 30),
                        });

                        var database = (await client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;
                        await database.CreateContainerIfNotExistsAsync(new ContainerProperties("items", "/pk"), cancellationToken: cts.Token);
                        _seeded = true;
                    }
                    catch (Exception e)
                    {
                        _unavailable = e.Message;
                    }
                }
            }
            finally
            {
                Seeding.Release();
            }

            if (_unavailable is string reason)
                Assert.Skip("These need a service. " + reason);
        }

    }

}
