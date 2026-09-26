using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Apache.Calcite.Extensions.Adapter.Cursor;
using Apache.Calcite.Extensions.Adapter.Enumerable;
using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using org.apache.calcite;
using org.apache.calcite.adapter.java;
using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Covers the table modify as a compiled plan: both of its bodies built, opened, and read, against an
    /// executor that answers with documents rather than with a service.
    /// </summary>
    /// <remarks>
    /// The rule's tests settle which write a statement plans to, and the service-backed ones settle what a
    /// write does to a container. Neither builds the node's expression trees, so a body that composes a
    /// wrong type would pass both and fail only when a host ran it. These run it.
    /// </remarks>
    public class CosmosTableModifyTests
    {

        /// <summary>
        /// Answers a scan with documents and a count with a number, and records every statement and
        /// every write it was given.
        /// </summary>
        sealed class StubExecutor : ICosmosQueryExecutor, ICosmosItemWriter
        {

            readonly string[] _documents;

            public StubExecutor(params string[] documents)
            {
                _documents = documents;
            }

            public List<CosmosQuery> Executed { get; } = new();

            public List<string> Deleted { get; } = new();

            public List<PartitionKey> PartitionsDeleted { get; } = new();

            public List<string> Created { get; } = new();

            public List<string> Replaced { get; } = new();

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                Executed.Add(query);

                await Task.Yield();

                return query.Sql.Contains("COUNT(1)", StringComparison.Ordinal)
                    ? ListCursor.Documents(new[] { _documents.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                    : ListCursor.Documents(_documents);
            }

            public Task CreateItemAsync(byte[] document, PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                Created.Add(System.Text.Encoding.UTF8.GetString(document));
                return Task.CompletedTask;
            }

            public Task<bool> DeleteItemAsync(string id, PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                Deleted.Add(id);
                return Task.FromResult(true);
            }

            public Task<bool> DeletePartitionAsync(PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                PartitionsDeleted.Add(partitionKey);
                return Task.FromResult(true);
            }

            public Task<bool> SupportsPartitionDeleteAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(true);

            public Task<bool> ReplaceItemAsync(byte[] document, string id, PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                Replaced.Add(id);
                return Task.FromResult(true);
            }

        }

        sealed class TestDataContext : DataContext
        {

            readonly SchemaPlus _rootSchema;
            readonly JavaTypeFactory _typeFactory;

            public TestDataContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory)
            {
                _rootSchema = rootSchema;
                _typeFactory = typeFactory;
            }

            public SchemaPlus getRootSchema() => _rootSchema;

            public JavaTypeFactory getTypeFactory() => _typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => null!;

        }

        static readonly string[] Bikes =
        [
            """{"DOC":{"id":"a","category":"bikes"},"id":"a","_ts":1,"_etag":"e","category":"bikes"}""",
            """{"DOC":{"id":"b","category":"bikes"},"id":"b","_ts":1,"_etag":"e","category":"bikes"}""",
        ];

        readonly JavaTypeFactoryImpl _typeFactory = new();
        readonly CalciteSchema _rootSchema = CalciteSchema.createRootSchema(false);
        StubExecutor _executor = null!;

        /// <summary>
        /// Registers the container, backed by the stub, and says whether the account allows a
        /// whole-partition delete.
        /// </summary>
        void Given(bool partitionDelete, params string[] documents)
        {
            _executor = new StubExecutor(documents);

            var metadata = new CosmosContainerMetadata("products", new[] { "/category" }).WithPartitionKeyDeleteProbe(() => partitionDelete);
            _rootSchema.add("products", new CosmosTable(metadata, _executor));
        }

        RelNode Plan(string sql)
        {
            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(_rootSchema, java.util.Collections.emptyList(), _typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseStmt();
            var validator = SqlValidatorUtil.newValidator(
                org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), CosmosOperators.Instance), catalogReader, _typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(_typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).rel;

            var table = (CosmosTable)_rootSchema.getTable("products", true)!.getTable();

            foreach (var rule in CosmosRules.GetRules(table.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            var best = planner.findBestExp();

            var program = new org.apache.calcite.plan.hep.HepProgramBuilder();
            foreach (var rule in ClrCursorRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
            hep.setRoot(best);

            return hep.findBestExp();
        }

        ClrCursorFactory Implement(RelNode rel)
        {
            var implementor = new ClrCursorRelImplementor(rel.getCluster().getRexBuilder(), new java.util.HashMap());
            return implementor.ImplementRoot((ClrCursorRel)rel, ClrEnumerablePrefer.Array);
        }

        async Task<long> CountAsync(RelNode rel)
        {
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            await using var cursor = await Implement(rel).OpenAsync(context, CancellationToken.None);

            (await cursor.ReadAsync(CancellationToken.None)).Should().BeTrue("a write answers with one row");
            var count = AsLong(cursor.Current);
            (await cursor.ReadAsync(CancellationToken.None)).Should().BeFalse("a write answers with one row");

            return count;
        }

        long Count(RelNode rel)
        {
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            using var cursor = Implement(rel).Open(context);

            cursor.Read().Should().BeTrue("a write answers with one row");
            var count = AsLong(cursor.Current);
            cursor.Read().Should().BeFalse("a write answers with one row");

            return count;
        }

        /// <summary>
        /// Reads the count, which the physical type may hand back as Java's box rather than the CLR's.
        /// </summary>
        static long AsLong(object? value) => value switch
        {
            java.lang.Long boxed => boxed.longValue(),
            _ => System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        };

        const string DeleteRows = "DELETE FROM products WHERE \"$.category\" = 'bikes'";

        [Fact]
        public async Task ARowAtATimeDeleteWritesEveryRowItReadsWhenOpenedWithAwait()
        {
            Given(partitionDelete: false, Bikes);

            var count = await CountAsync(Plan(DeleteRows));

            count.Should().Be(2);
            _executor.Deleted.Should().Equal("a", "b");
        }

        [Fact]
        public void ARowAtATimeDeleteWritesEveryRowItReadsWhenOpenedSynchronously()
        {
            Given(partitionDelete: false, Bikes);

            var count = Count(Plan(DeleteRows));

            count.Should().Be(2);
            _executor.Deleted.Should().Equal("a", "b");
        }

        /// <remarks>
        /// <b>The input is never opened, and that is the assertion that matters.</b> In the cursor
        /// convention opening is acquisition: an input's leaf sends its statement at the open. So a
        /// whole-partition delete that visited its input and merely left it unread would still have
        /// scanned a page of the partition it is emptying. The only statement the service may see is the
        /// count.
        /// </remarks>
        [Fact]
        public async Task AWholePartitionDeleteSendsOnlyTheCountWhenOpenedWithAwait()
        {
            Given(partitionDelete: true, Bikes);

            var count = await CountAsync(Plan(DeleteRows));

            count.Should().Be(2);
            _executor.PartitionsDeleted.Should().ContainSingle().Which.Should().Be(new PartitionKey("bikes"));
            _executor.Executed.Should().ContainSingle().Which.Sql.Should().Contain("COUNT(1)");
            _executor.Deleted.Should().BeEmpty();
        }

        const string InsertTwo = """INSERT INTO products ("DOC") VALUES ('{"id":"x","category":"bikes"}'), ('{"id":"y","category":"shoes"}')""";

        /// <remarks>
        /// An insert's input is a <c>VALUES</c>, which the cursor convention implements itself: this is
        /// the one write whose rows never came from the container.
        /// </remarks>
        [Fact]
        public async Task AnInsertCreatesEveryRowItIsGivenWhenOpenedWithAwait()
        {
            Given(partitionDelete: false);

            var count = await CountAsync(Plan(InsertTwo));

            count.Should().Be(2);
            _executor.Created.Select(d => JsonDocument.Parse(d).RootElement.GetProperty("id").GetString()).Should().Equal("x", "y");
            _executor.Executed.Should().BeEmpty("an insert reads nothing from the container");
        }

        [Fact]
        public void AnInsertCreatesEveryRowItIsGivenWhenOpenedSynchronously()
        {
            Given(partitionDelete: false);

            var count = Count(Plan(InsertTwo));

            count.Should().Be(2);
            _executor.Created.Should().HaveCount(2);
        }

        [Fact]
        public async Task AnUpdateReplacesEveryRowItReads()
        {
            Given(partitionDelete: false, Bikes);

            var count = await CountAsync(Plan("""UPDATE products SET "DOC" = '{"id":"a","category":"bikes","price":1}' WHERE "$.category" = 'bikes'"""));

            count.Should().Be(2);
            _executor.Replaced.Should().Equal("a", "b");
        }

        [Fact]
        public void AWholePartitionDeleteSendsOnlyTheCountWhenOpenedSynchronously()
        {
            Given(partitionDelete: true, Bikes);

            var count = Count(Plan(DeleteRows));

            count.Should().Be(2);
            _executor.PartitionsDeleted.Should().ContainSingle().Which.Should().Be(new PartitionKey("bikes"));
            _executor.Executed.Should().ContainSingle().Which.Sql.Should().Contain("COUNT(1)");
        }

    }

}
