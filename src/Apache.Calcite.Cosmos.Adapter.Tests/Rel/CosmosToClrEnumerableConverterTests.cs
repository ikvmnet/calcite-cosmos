using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Sql;

using Apache.Calcite.Extensions.Adapter.Enumerable;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

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

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Covers the one way out of the Cosmos convention: that the planner finds it, and that a plan
    /// ending in it produces the rows the documents hold.
    /// </summary>
    /// <remarks>
    /// The end-to-end tests compile the plan and run it against a stub executor. Nothing here reaches a
    /// service — that is the point of <see cref="ICosmosQueryExecutor"/> being the seam — but everything
    /// above the seam is the real thing: the real planner, the real statement, the real compiled
    /// expression tree.
    /// </remarks>
    [TestClass]
    public class CosmosToClrEnumerableConverterTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        /// <summary>
        /// An executor that answers with documents rather than with a service, and records what it was
        /// asked.
        /// </summary>
        sealed class StubExecutor : ICosmosQueryExecutor
        {

            readonly string[] _documents;

            public StubExecutor(params string[] documents)
            {
                _documents = documents;
            }

            public CosmosQuery? Executed { get; private set; }

            /// <summary>
            /// The token the executor was called with, which is the one the plan handed down rather
            /// than the one baked into it.
            /// </summary>
            public CancellationToken Token { get; private set; }

            public async IAsyncEnumerable<JsonElement> ExecuteAsync(CosmosQuery query, PartitionKey? partitionKey = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Executed = query;
                Token = cancellationToken;

                foreach (var document in _documents)
                {
                    // Yields on each row, so the plan above is exercised as a genuinely asynchronous
                    // sequence rather than one that happens to complete synchronously.
                    await Task.Yield();
                    yield return JsonDocument.Parse(document).RootElement.Clone();
                }
            }

        }

        /// <summary>
        /// The minimum a compiled plan needs to find its way back to the table it reads.
        /// </summary>
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

        CosmosTable _table = null!;
        StubExecutor _executor = null!;
        CalciteSchema _rootSchema = null!;
        JavaTypeFactoryImpl _typeFactory = null!;

        [TestInitialize]
        public void Initialize()
        {
            _typeFactory = new JavaTypeFactoryImpl();
            Given();
        }

        /// <summary>
        /// Exposes a table whose container holds the given documents.
        /// </summary>
        void Given(params string[] documents)
        {
            _executor = new StubExecutor(documents);
            Register(new CosmosTable(Products, _executor));
        }

        /// <summary>
        /// Exposes a table built from container metadata alone, with no client behind it.
        /// </summary>
        void GivenNoExecutor()
        {
            Register(new CosmosTable(Products));
        }

        void Register(CosmosTable table)
        {
            _table = table;
            _rootSchema = CalciteSchema.createRootSchema(false);
            _rootSchema.add("products", _table);
        }

        RelNode PlanLogical(string sql)
        {
            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                _rootSchema,
                java.util.Collections.emptyList(),
                _typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var validator = SqlValidatorUtil.newValidator(
                org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance), catalogReader, _typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(_typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).rel;
        }

        /// <summary>
        /// Plans a statement and asks for the best plan in the CLR convention, which is what a
        /// caller of this adapter asks for however it then reads the rows.
        /// </summary>
        RelNode PlanToClr(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// Compiles a planned tree and reads every row it produces.
        /// </summary>
        async Task<List<object>> Execute(RelNode rel)
        {
            var implementor = new ClrEnumerableRelImplementor(rel.getCluster().getRexBuilder(), new java.util.HashMap());
            var lambda = implementor.ImplementRootAsync((ClrEnumerableRel)rel, ClrEnumerablePrefer.Array);

            var run = (Func<DataContext, IAsyncEnumerable<object>>)lambda.Compile();
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            var rows = new List<object>();
            await foreach (var row in run(context))
                rows.Add(row);

            return rows;
        }

        /// <summary>
        /// Compiles the same planned tree the other way, and reads every row it produces.
        /// </summary>
        /// <remarks>
        /// <c>ImplementRoot</c> rather than <c>ImplementRootAsync</c>, which for a Cosmos plan is the
        /// bridge over the awaiting body rather than a second implementation of it. It blocks a thread
        /// per row; the test using it is about the rows, not about the cost.
        /// </remarks>
        List<object> ExecutePulled(RelNode rel)
        {
            var implementor = new ClrEnumerableRelImplementor(rel.getCluster().getRexBuilder(), new java.util.HashMap());
            var lambda = implementor.ImplementRoot((ClrEnumerableRel)rel, ClrEnumerablePrefer.Array);

            var run = (Func<DataContext, IEnumerable<object>>)lambda.Compile();
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            var rows = new List<object>();
            foreach (var row in run(context))
                rows.Add(row);

            return rows;
        }

        // ── Planning ──────────────────────────────────────────────────────────────

        /// <remarks>
        /// Without the converter rule the pushed-down subtree is a statement nothing can read the rows
        /// of, and the planner has no complete plan to return.
        /// </remarks>
        [TestMethod]
        public void ThePlannerReachesTheClrConventionThroughTheConverter()
        {
            var plan = PlanToClr("SELECT \"id\" FROM products AS c");

            plan.Should().BeOfType<CosmosToClrEnumerableConverter>();
            plan.getInput(0).getConvention().Should().BeSameAs(_table.Convention);
        }

        // ── Execution ─────────────────────────────────────────────────────────────

        /// <remarks>
        /// A one-column result is the value rather than a one-element row, which is what
        /// <c>ImplementRootAsync</c> arranges and what every caller of a query expects.
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadASingleColumnAsTheValueItself()
        {
            Given("""{ "id": "a" }""", """{ "id": "b" }""");

            var rows = await Execute(PlanToClr("SELECT \"id\" FROM products AS c"));

            rows.Should().Equal("a", "b");
        }

        /// <remarks>
        /// <b>The plan carries no mode, so the same tree reads either way.</b> While there were two Clr
        /// conventions this could not be written at all: the adapter published no converter into the
        /// pulled one, so a synchronous root found no plan. One convention removes that gate, and what
        /// is left is a bridge at the converter — so the thing to hold is that the bridge does not
        /// change the answer.
        /// <para>
        /// It says nothing about what the bridge costs, which is a blocked thread per row and is
        /// documented rather than asserted. A test cannot tell a blocked thread from a fast one.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void ShouldReadTheSameRowsWhenThePlanIsPulledRatherThanAwaited()
        {
            Given("""{ "id": "a" }""", """{ "id": "b" }""");

            var rows = ExecutePulled(PlanToClr("SELECT \"id\" FROM products AS c"));

            rows.Should().Equal("a", "b");
        }

        /// <remarks>
        /// <b>The reader's token reaches the service call, and the plan is what makes that possible by
        /// not carrying one.</b> The call site bakes in <c>default</c>, so
        /// <c>[EnumeratorCancellation]</c> substitutes whatever <c>GetAsyncEnumerator</c> was given and
        /// the operators hand it down to the leaf. A page in flight is therefore cancellable, rather
        /// than cancellation meaning only that nobody asks for the next one.
        /// <para>
        /// The discriminator is <c>CanBeCanceled</c>: it is <c>false</c> for the
        /// <c>CancellationToken.None</c> the expression tree holds, so this fails if the substitution
        /// ever stops happening and the baked-in token is what arrives.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task ShouldCarryTheReadersTokenIntoTheExecutor()
        {
            Given("""{ "id": "a" }""");

            var rel = PlanToClr("SELECT \"id\" FROM products AS c");

            var implementor = new ClrEnumerableRelImplementor(rel.getCluster().getRexBuilder(), new java.util.HashMap());
            var lambda = implementor.ImplementRootAsync((ClrEnumerableRel)rel, ClrEnumerablePrefer.Array);

            var run = (Func<DataContext, IAsyncEnumerable<object>>)lambda.Compile();
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            using var cts = new CancellationTokenSource();

            await foreach (var _ in run(context).WithCancellation(cts.Token))
                break;

            _executor.Token.CanBeCanceled.Should().BeTrue("the reader's token should reach the executor, not the default the plan holds");

            cts.Cancel();
            _executor.Token.IsCancellationRequested.Should().BeTrue("the token the executor holds should be the reader's own");
        }

        /// <remarks>
        /// The statement the stub is handed is the projected object constructor, not <c>SELECT VALUE c</c>.
        /// One row shape reaches the reader whatever the query did.
        /// </remarks>
        [TestMethod]
        public async Task ShouldProjectAnObjectKeyedByOutputFieldName()
        {
            Given("""{ "id": "a" }""");

            await Execute(PlanToClr("SELECT \"id\" FROM products AS c"));

            _executor.Executed!.Value.Sql.Should().Contain("SELECT VALUE {").And.Contain("\"id\": c.id");
        }

        /// <remarks>
        /// Cosmos omits a property whose value is undefined, so a row is a subset of the output fields and
        /// their positions are not the fields' positions. Reading by name is what makes that survivable.
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadAnAbsentPropertyAsNull()
        {
            Given("""{ "id": "a" }""", """{ }""");

            var rows = await Execute(PlanToClr("SELECT \"id\" FROM products AS c"));

            rows.Should().Equal(new object[] { "a", null! });
        }

        [TestMethod]
        public async Task ShouldReadEveryColumnOfAWiderRow()
        {
            Given("""{ "id": "a", "_ts": 17 }""");

            var rows = await Execute(PlanToClr("SELECT \"id\", \"_ts\" FROM products AS c"));

            rows.Should().HaveCount(1);
            ((object[])rows[0]).Should().Equal("a", java.lang.Long.valueOf(17L));
        }

        /// <summary>
        /// An array-typed column comes back as the array, which is what #119 said it did not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The read half of the fix. The statement asks for <c>IS_ARRAY(c.tags) ? c.tags : null</c>
        /// — asserted in <see cref="CosmosPlannerTests"/> — and this is what the row builder then does
        /// with the answer: <c>CosmosJson.GetList</c> builds the <see cref="java.util.List"/> Calcite
        /// holds a collection in, which <see cref="Sql.CalciteArrayReadingMeasurementTests"/> shows reaching a
        /// <c>DbDataReader</c> as a CLR array.
        /// </para>
        /// <para>
        /// Before the fix the column was read as text, so the row carried the string <c>[a, b]</c>
        /// where the plan had declared a collection and the cast to a list failed outright.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadAnArrayColumnAsTheArray()
        {
            Given("""{ "T": ["a", "b"] }""");

            var rows = await Execute(PlanToClr("SELECT JSON_VALUE(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"T\" FROM products AS c"));

            rows.Should().HaveCount(1);
            rows[0].Should().BeAssignableTo<java.util.List>();

            var list = (java.util.List)rows[0];
            list.size().Should().Be(2);
            list.get(0).Should().Be("a");
            list.get(1).Should().Be("b");
        }

        /// <remarks>
        /// The guard answers null at the service for a document holding no array at the path, and the
        /// column reads as SQL null rather than failing the query — which is the point of guarding
        /// rather than emitting the bare path.
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadAnArrayColumnAsNullWhereTheGuardAnsweredNull()
        {
            Given("""{ }""");

            var rows = await Execute(PlanToClr("SELECT JSON_VALUE(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"T\" FROM products AS c"));

            rows.Should().Equal(new object[] { null! });
        }

        /// <summary>
        /// The elements are read as the type the <c>RETURNING</c> named, not by their own JSON type.
        /// </summary>
        /// <remarks>
        /// <c>INTEGER ARRAY</c> holds <see cref="java.lang.Integer"/>, where reading each element
        /// naturally would hand back a <see cref="java.lang.Long"/> — a Cosmos number being a double
        /// and an integral one arriving as a Long wherever there is no schema to consult. Here there
        /// is one.
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadArrayElementsAsTheDeclaredComponentType()
        {
            Given("""{ "T": [1, 2] }""");

            var rows = await Execute(PlanToClr("SELECT JSON_VALUE(c.\"DOC\", '$.tags' RETURNING INTEGER ARRAY) AS \"T\" FROM products AS c"));

            var list = (java.util.List)rows[0];
            list.get(0).Should().Be(java.lang.Integer.valueOf(1));
            list.get(1).Should().Be(java.lang.Integer.valueOf(2));
        }

        /// <remarks>
        /// An element contradicting the declaration fails rather than lies, which is the same refusal
        /// <c>CosmosJson.GetString</c> makes for a scalar column and the reason a <c>RETURNING</c>
        /// clause is worth trusting at all.
        /// </remarks>
        [TestMethod]
        public async Task ShouldRefuseAnElementThatContradictsTheDeclaredComponentType()
        {
            Given("""{ "T": [1, 2] }""");

            var plan = PlanToClr("SELECT JSON_VALUE(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"T\" FROM products AS c");

            var act = async () => await Execute(plan);
            (await act.Should().ThrowAsync<CosmosMaterializationException>()).WithMessage("*Expected a JSON string*");
        }

        /// <summary>
        /// A scalar <c>RETURNING</c> reads as the type it names.
        /// </summary>
        /// <remarks>
        /// The same one-line mistake as the array case: every <c>JSON_VALUE</c> was rendered as the
        /// text form's guard and read back as text, so a column the plan had declared <c>INTEGER</c>
        /// carried a string and the row could not be built. Held here because the array case alone
        /// would not have caught it.
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadAScalarReturningAsTheTypeItNames()
        {
            Given("""{ "N": 7, "B": true }""");

            var rows = await Execute(PlanToClr(
                "SELECT JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"N\", JSON_VALUE(c.\"DOC\", '$.b' RETURNING BOOLEAN) AS \"B\" FROM products AS c"));

            rows.Should().HaveCount(1);
            ((object[])rows[0]).Should().Equal(java.lang.Integer.valueOf(7), java.lang.Boolean.TRUE);
        }

        /// <summary>
        /// A projected <c>JSON_QUERY</c> reads back as the fragment's JSON text, written as Calcite
        /// writes it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This threw before it had a rendering of its own.</b> The statement sent the bare path
        /// and the column was read as the declared <c>VARCHAR</c>, so the object the function exists
        /// to return was refused by <c>CosmosJson.GetString</c> — <em>Expected a JSON string, got
        /// Object</em> — while a scalar at the path came back as itself, where SQL/JSON says the
        /// function answers null. Wrong in both directions at once.
        /// </para>
        /// <para>
        /// The whitespace case is the one that decides the reading. The document here is stored with
        /// spaces between its tokens and the column reads <c>["a","b"]</c> without them, which is what
        /// Calcite's own <c>JSON_QUERY</c> answers — see
        /// <c>CalciteJsonValueMeasurementTests.JsonQueryAnswersStructureOnlyAndWritesItCompactly</c>.
        /// Handing over the service's own bytes, as the document column does, would have differed by
        /// exactly those spaces.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadAJsonQueryColumnAsCompactJsonText()
        {
            Given("""{ "q": { "a" : 1 } }""");
            (await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.o') AS \"q\" FROM products AS c")))
                .Should().Equal("{\"a\":1}");

            Given("""{ "q": [ "a" ,   "b" ] }""");
            (await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.v') AS \"q\" FROM products AS c")))
                .Should().Equal("[\"a\",\"b\"]");
        }

        /// <remarks>
        /// The guard answers null at the service for a scalar, and the column reads as SQL null —
        /// which is what the function means, and what it did not do when the bare path was sent.
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadAJsonQueryOverAScalarAsNull()
        {
            Given("""{ }""");

            (await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.s') AS \"q\" FROM products AS c")))
                .Should().Equal(new object[] { null! });
        }

        /// <remarks>
        /// A table built from container metadata alone plans identically and cannot be read from. This is
        /// the first point at which that could show, and it says so rather than throwing a null reference.
        /// </remarks>
        [TestMethod]
        public async Task ShouldRefuseToExecuteATableWithNoExecutor()
        {
            GivenNoExecutor();

            var plan = PlanToClr("SELECT \"id\" FROM products AS c");

            var act = async () => await Execute(plan);
            (await act.Should().ThrowAsync<CosmosExecutionException>()).WithMessage("*has no query executor*");
        }

    }

}
