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


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel.Convert
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

        public CosmosToClrEnumerableConverterTests()
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
        /// The schema of a catalog row: an identifier stored as a canonical UUID, and a name.
        /// </summary>
        const string Catalog = """
        { "type": "object",
          "required": ["ref", "name"],
          "properties": {
            "ref": { "type": "string",
                     "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" },
            "name": { "type": "string" } } }
        """;

        /// <summary>
        /// Exposes a table whose container declares <see cref="Catalog"/>, and holds the given
        /// documents.
        /// </summary>
        /// <remarks>
        /// A declaration is the only way a cast to a type Cosmos has no equivalent of renders, so it
        /// is the only way a pushed statement's row carries a column the plan types <c>UUID</c>.
        /// What a declaration licenses is argued in <c>CosmosDeclaredFactPlanningTests</c>; this
        /// wants one only to reach the reading on the other side.
        /// </remarks>
        void GivenDeclared(params string[] documents)
        {
            _executor = new StubExecutor(documents);

            Register(new CosmosTable(
                Products.WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(Catalog))),
                _executor));
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
        /// The same, with the engine's own rules registered beside the adapter's and the calc pass a
        /// host runs after them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="PlanToClr"/> registers only the Cosmos rules, which is enough while the whole
        /// projection pushes: what is left above the converter is nothing at all. A split leaves a
        /// residual projection there, and that needs two more things — the engine's rules to carry it
        /// into the CLR convention, and <b>a calc pass to turn it into something implementable</b>.
        /// </para>
        /// <para>
        /// <b>The calc pass is separate on purpose, and it is not optional.</b>
        /// <c>ClrEnumerableProject.Implement</c> raises <c>UnsupportedOperationException</c>, exactly as
        /// Calcite's own <c>EnumerableProject.implement</c> does — <em>"EnumerableCalcRel is always
        /// better"</em> — and the rule that rewrites one into a calc is a <c>TransformationRule</c>.
        /// <c>VolcanoPlanner.addRule</c> does not register a transformation rule's operand against a
        /// <c>PhysicalNode</c>, and <c>ClrEnumerableRel</c> is one, so no calc rule can fire during the
        /// Volcano pass however it is registered. It has to run afterwards, over the chosen plan, on a
        /// <see cref="HepPlanner"/>. Calcite protects itself the same way, with
        /// <c>Programs.standard</c>'s last pass.
        /// </para>
        /// <para>
        /// This was worth writing down because leaving the pass out looks exactly like a defect in the
        /// enumerable adapter — it was reported as one, and is not: see
        /// <see href="https://github.com/ikvmnet/calcite-dotnet/issues/155">calcite-dotnet#155</see>.
        /// A wholly-pushed plan compiles without it, which makes the gap invisible until a projection
        /// survives.
        /// </para>
        /// </remarks>
        RelNode PlanToClrWithHostRules(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            foreach (var rule in Apache.Calcite.Extensions.Adapter.Enumerable.ClrEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            var best = planner.findBestExp();

            var program = new org.apache.calcite.plan.hep.HepProgramBuilder();

            foreach (var rule in Apache.Calcite.Extensions.Adapter.Enumerable.ClrEnumerableRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
            hep.setRoot(best);

            return hep.findBestExp();
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
        [Fact]
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
        [Fact]
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
        [Fact]
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
        [Fact]
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
        [Fact]
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
        [Fact]
        public async Task ShouldReadAnAbsentPropertyAsNull()
        {
            Given("""{ "id": "a" }""", """{ }""");

            var rows = await Execute(PlanToClr("SELECT \"id\" FROM products AS c"));

            rows.Should().Equal(new object[] { "a", null! });
        }

        [Fact]
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
        /// — asserted in <see cref="EndToEnd.CosmosPlannerTests"/> — and this is what the row
        /// builder then does with the answer: <c>CosmosJson.GetList</c> builds the
        /// <see cref="java.util.List"/> Calcite holds a collection in, which
        /// <see cref="Measurements.CalciteArrayReadingMeasurementTests"/> shows reaching a
        /// <c>DbDataReader</c> as a CLR array.
        /// </para>
        /// <para>
        /// Before the fix the column was read as text, so the row carried the string <c>[a, b]</c>
        /// where the plan had declared a collection and the cast to a list failed outright.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldReadAnArrayColumnAsTheArray()
        {
            Given("""{ "T": ["a", "b"] }""");

            var rows = await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"T\" FROM products AS c"));

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
        [Fact]
        public async Task ShouldReadAnArrayColumnAsNullWhereTheGuardAnsweredNull()
        {
            Given("""{ }""");

            var rows = await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"T\" FROM products AS c"));

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
        [Fact]
        public async Task ShouldReadArrayElementsAsTheDeclaredComponentType()
        {
            Given("""{ "T": [1, 2] }""");

            var rows = await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.tags' RETURNING INTEGER ARRAY) AS \"T\" FROM products AS c"));

            var list = (java.util.List)rows[0];
            list.get(0).Should().Be(java.lang.Integer.valueOf(1));
            list.get(1).Should().Be(java.lang.Integer.valueOf(2));
        }

        /// <remarks>
        /// An element contradicting the declaration fails rather than lies, which is the same refusal
        /// <c>CosmosJson.GetString</c> makes for a scalar column and the reason a <c>RETURNING</c>
        /// clause is worth trusting at all.
        /// </remarks>
        [Fact]
        public async Task ShouldRefuseAnElementThatContradictsTheDeclaredComponentType()
        {
            Given("""{ "T": [1, 2] }""");

            var plan = PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY) AS \"T\" FROM products AS c");

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
        [Fact]
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
        [Fact]
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
        [Fact]
        public async Task ShouldReadAJsonQueryOverAScalarAsNull()
        {
            Given("""{ }""");

            (await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.s') AS \"q\" FROM products AS c")))
                .Should().Equal(new object[] { null! });
        }

        /// <summary>
        /// A residual cast reads the column the split pushed for it, and the value is right.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The half a planner test cannot answer.</b> That the accessor goes down as <c>$f0</c> is a
        /// statement about the plan; that the cast above it produces the number is a statement about
        /// the row, and the two halves of the split are only correct together — the pushed column is
        /// read by its own rules, so what arrives is the rendering rather than the raw value, and the
        /// residual has to be right about what it is reading.
        /// </para>
        /// <para>
        /// The canned document is shaped like the <em>pushed</em> row, because that is what the split
        /// makes the statement return.
        /// </para>
        /// <para>
        /// Runs through <see cref="PlanToClrWithHostRules"/> rather than <see cref="PlanToClr"/>,
        /// because a residual projection needs the calc pass; the reason that is a pass rather than a
        /// rule is recorded there.
        /// </para>
        /// <para>
        /// The value arrives as a <see cref="java.lang.Double"/> rather than a CLR one, because it was
        /// computed by Calcite's runtime rather than read out of a document — the same box an array
        /// column arrives in as a <see cref="java.util.List"/>. Asserted as such rather than converted,
        /// since what a residual hands a caller is part of what the split has to be right about.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldComputeAResidualCastOverThePushedFragment()
        {
            Given("""{ "$f0": "30.5" }""");

            var rows = await Execute(PlanToClrWithHostRules(
                "SELECT CAST(JSON_VALUE(c.\"DOC\", '$.n' RETURNING VARCHAR) AS DOUBLE) AS \"x\" FROM products AS c"));

            rows.Should().HaveCount(1);
            rows[0].Should().BeOfType<java.lang.Double>("a residual is computed by Calcite's runtime, which hands back its own box")
                .Which.doubleValue().Should().Be(30.5d);

            _executor.Executed!.Value.Sql.Should().Contain("IS_PRIMITIVE(c.n)",
                "the accessor was the service's to answer");
            _executor.Executed!.Value.Sql.Should().NotContain("\"\": c",
                "and the document had no reason to travel");
        }

        /// <summary>
        /// A pushed projection whose only column is a <c>UUID</c> reads back, which is what #150 said
        /// it did not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The row shape is the whole of it.</b> <c>JavaRowFormat.optimize</c> makes a one-field
        /// row the value itself rather than an <c>object[]</c>, so
        /// <see cref="CosmosConverters.RowBuilder"/> casts what it read to the field's physical type
        /// — and for a <c>UUID</c> that type is <c>org.apache.calcite.util.UuidValue</c>. Reading
        /// through <c>SqlFunctions.stringToUuid</c> produced the <c>java.util.UUID</c> that wrapper
        /// holds, which the cast refused: <em>java.util.UUID cannot be cast to UuidValue</em>, at the
        /// first row rather than at planning.
        /// </para>
        /// <para>
        /// Held here rather than in <c>CosmosJsonTests</c> alone because the reader's own test could
        /// not see it. The class was the right value in the wrong box, and nothing but a compiled row
        /// builder over a narrow row ever asked which box it was — which is also why the field
        /// reported it through a <c>$count</c> and an <c>$expand</c>, the two OData shapes that
        /// project a key on its own.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldReadALoneUuidColumn()
        {
            GivenDeclared("""{ "Id": "0123456f-89ab-7cde-8f01-23456789abcd" }""");

            var rows = await Execute(PlanToClr(
                "SELECT CAST(JSON_VALUE(c.\"DOC\", '$.ref') AS UUID) AS \"Id\" FROM products AS c"));

            _executor.Executed!.Value.Sql.Should().Contain("IS_PRIMITIVE(c.ref) ? c.ref : null",
                "the cast rendered, so the column really is the pushed one: " + _executor.Executed!.Value.Sql);

            rows.Should().HaveCount(1);
            rows[0].Should().BeOfType<org.apache.calcite.util.UuidValue>(
                    "which is what Calcite holds a UUID in, and what a one-column row is cast to")
                .And.Be(org.apache.calcite.util.UuidValue.fromString("0123456f-89ab-7cde-8f01-23456789abcd"));
        }

        /// <remarks>
        /// The shape that hid it. A row of two columns is an <c>object[]</c>, so the row builder boxes
        /// each column rather than casting it, and the wrong class survived to a reader that converts
        /// either one — measured in <c>CalciteUuidReadingMeasurementTests</c>, where the bare
        /// <c>java.util.UUID</c> reaches a caller as a <c>Guid</c> at two columns and raises at one.
        /// So this read fine throughout, and still does, now in the same box as the narrow row. Both
        /// halves are asserted because agreeing is the claim: one reader, one representation,
        /// whatever the arity.
        /// </remarks>
        [Fact]
        public async Task ShouldReadAUuidColumnTheSameWayBesideAnother()
        {
            GivenDeclared("""{ "Id": "0123456f-89ab-7cde-8f01-23456789abcd", "Name": "widget" }""");

            var rows = await Execute(PlanToClr(
                "SELECT CAST(JSON_VALUE(c.\"DOC\", '$.ref') AS UUID) AS \"Id\", JSON_VALUE(c.\"DOC\", '$.name') AS \"Name\" FROM products AS c"));

            rows.Should().HaveCount(1);
            ((object[])rows[0]).Should().Equal(
                org.apache.calcite.util.UuidValue.fromString("0123456f-89ab-7cde-8f01-23456789abcd"),
                "widget");
        }

        /// <summary>
        /// An array <c>RETURNING</c> on <c>JSON_QUERY</c> is a collection column, not a fragment.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The fragment rendering is for the text-typed form alone.</b> Reading this one as text
        /// hands a string to a column the plan typed <c>String[]</c>, which is the cast failure the
        /// guard was written to avoid one function earlier — and which #129 reports from the field
        /// against 1.0.0-pre.212, the release #127 merged as.
        /// </para>
        /// <para>
        /// Worth a test of its own because the two tests are different questions —
        /// <c>IsPlainJsonQuery</c> asks what the clauses say and the collection test asks what the
        /// call is typed, and matching on the first alone is what let this through.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldReadAnArrayReturningJsonQueryAsTheArray()
        {
            Given("""{ "q": ["a","b"] }""");

            var rows = await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.v' RETURNING VARCHAR ARRAY) AS \"q\" FROM products AS c"));

            rows.Should().HaveCount(1);
            var list = (java.util.List)rows[0];
            list.size().Should().Be(2);
            list.get(0).Should().Be("a");
            list.get(1).Should().Be("b");

            _executor.Executed!.Value.Sql.Should().Contain("IS_ARRAY(c.v)",
                "an array column takes the array guard, not the fragment's");
        }

        /// <summary>
        /// The same through a view, which is the spelling #129 was reported from.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The shape a typed caller actually writes.</b> A view gives the container a relational
        /// row and an ORM reads the column off it, so the accessor is a projection inside a derived
        /// table rather than the statement's own <c>SELECT</c> list. Calcite collapses the two
        /// projections into one before anything here sees them, which is why the fix needs no separate
        /// handling — but "needs none" is worth holding to a test rather than reasoning to, because
        /// the report is against this spelling and not the flat one.
        /// </para>
        /// <para>
        /// The reported failure is a materialiser reaching for a <see cref="java.util.List"/> and
        /// finding a <c>String</c>, so the assertion is on the value's own type rather than on
        /// whether it prints the same.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldReadAnArrayReturningJsonQueryThroughAView()
        {
            Given("""{ "Cities": ["Bryson City","Gatlinburg","Cherokee"] }""");

            var rows = await Execute(PlanToClr(
                "SELECT v.\"Cities\" FROM (SELECT JSON_QUERY(c.\"DOC\", '$.data.address.city' RETURNING VARCHAR ARRAY) AS \"Cities\" FROM products AS c) AS v"));

            rows.Should().HaveCount(1);
            rows[0].Should().BeAssignableTo<java.util.List>("a materialiser reads the column as a list, and a String is the regression");

            var list = (java.util.List)rows[0];
            list.size().Should().Be(3);
            list.get(0).Should().Be("Bryson City");
            list.get(2).Should().Be("Cherokee");

            _executor.Executed!.Value.Sql.Should().Contain("IS_ARRAY(c.data.address.city)",
                "the view's column is the accessor's own rendering, collapsed into one projection");
        }

        /// <summary>
        /// A null element is carried rather than dropped or refused, because the element type is
        /// nullable and there is no way to say otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nullability is not part of the type, so <c>RETURNING INTEGER ARRAY</c> means nullable
        /// elements.</b> In SQL it is a constraint rather than a component of a data type, the
        /// <c>RETURNING</c> clause names a type, and there is no syntax to narrow it — measured,
        /// <c>INTEGER NOT NULL ARRAY</c> and <c>INTEGER ARRAY NOT NULL</c> are both parse errors. The
        /// column reads back as <c>int?[]</c> for that reason, and <c>int[]</c> could not represent
        /// what the type describes.
        /// </para>
        /// <para>
        /// Calcite forces the array and its elements nullable deliberately —
        /// <see href="https://issues.apache.org/jira/browse/CALCITE-6208">CALCITE-6208</see> — because
        /// non-null elements let a <c>WHERE c IS NOT NULL</c> over an unnested array be optimised away
        /// and rows be lost. So carrying the null through is the reading that agrees with the engine,
        /// and dropping it or refusing it would not.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldCarryANullArrayElement()
        {
            Given("""{ "q": [1, null, 2] }""");

            var rows = await Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.n' RETURNING INTEGER ARRAY) AS \"q\" FROM products AS c"));

            var list = (java.util.List)rows[0];
            list.size().Should().Be(3, "the null is an element and not an absence");
            list.get(0).Should().Be(java.lang.Integer.valueOf(1));
            list.get(1).Should().BeNull("and it survives as a null entry");
            list.get(2).Should().Be(java.lang.Integer.valueOf(2));
        }

        /// <remarks>
        /// A table built from container metadata alone plans identically and cannot be read from. This is
        /// the first point at which that could show, and it says so rather than throwing a null reference.
        /// </remarks>
        [Fact]
        public async Task ShouldRefuseToExecuteATableWithNoExecutor()
        {
            GivenNoExecutor();

            var plan = PlanToClr("SELECT \"id\" FROM products AS c");

            var act = async () => await Execute(plan);
            (await act.Should().ThrowAsync<CosmosExecutionException>()).WithMessage("*has no query executor*");
        }

        // ── What crosses the wire decides the plan ────────────────────────────────

        /// <summary>
        /// Plans a statement with the columns nothing reads trimmed off the scan, which is what a
        /// host does and what <see cref="PlanToClr"/> leaves out.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>RelFieldTrimmer</c> is in Calcite's own prepare, so every host runs it, and what it
        /// does to this adapter is particular: a Cosmos scan presents five columns and a query that
        /// reads paths out of the document reads one of them, so the trimmer puts a
        /// <c>LogicalProject(DOC=[$0])</c> between the scan and whatever is above it. That projection
        /// is the whole of the shape this section is about — it takes the statement's one
        /// <c>SELECT</c>, and what the query actually projects then has nowhere to go.
        /// </para>
        /// <para>
        /// The engine's rules are registered beside the adapter's for the reason
        /// <see cref="PlanToClrWithHostRules"/> gives, and here it is load-bearing rather than
        /// incidental: without them the in-process alternative cannot be built at all, and a test
        /// that cannot express the losing plan cannot show the winning one was preferred.
        /// </para>
        /// </remarks>
        /// <param name="sql">The statement.</param>
        /// <returns>The best plan.</returns>
        RelNode PlanToClrAsAHostDoes(string sql)
        {
            var logical = PlanLogical(sql);

            var builder = org.apache.calcite.tools.RelBuilder.proto(Contexts.EMPTY_CONTEXT).create(logical.getCluster(), null);
            var trimmed = new RelFieldTrimmer(null, builder).trim(logical);

            var planner = (VolcanoPlanner)trimmed.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = trimmed.getTraitSet().replace(ClrEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(trimmed, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// A projection of any width pushes over a filter, rather than the query keeping the document
        /// and extracting the columns here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rows are the same either way and the bytes are not</b>, which is exactly what makes
        /// this a cost question the model has to get right rather than a correctness one. Measured on
        /// a container of 9,370 documents carrying registration points and image metadata, the same
        /// two columns over the same rows took 248 seconds unprojected against 2 seconds projected
        /// (#145).
        /// </para>
        /// <para>
        /// <b>One column used to pass and two used to fail</b>, which is what made this look like
        /// anything but a cost problem. It was one: this node was priced by counting the columns
        /// crossing it, the trimmed subtree presents one column, and so pushing a two-column
        /// projection doubled this node's cost and charged back precisely what the projection saved.
        /// The two plans then tied on rows, which is the only component <c>VolcanoCost</c> compares,
        /// and the tie went to whichever was registered first. At one column the multipliers matched
        /// and the pushed plan won on its own merits, which is why the width the query asks for
        /// appeared to decide anything at all.
        /// </para>
        /// <para>
        /// So the widths are the assertion. <c>products</c> declares no statistics — the container the
        /// rest of this class uses, and the ordinary case — so nothing here rests on a measured
        /// document size, only on <see cref="CosmosToClrEnumerableConverter.MinimumDocumentWidth"/>.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ShouldPushAProjectionOfAnyWidthOverAFilterTheTrimmerLeftAProjectionAbove(int columns)
        {
            var paths = new[] { "id", "name", "region" };
            var projected = string.Join(", ", System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Take(paths, columns),
                (path, i) => $"JSON_VALUE(c.\"DOC\", '$.data.{path}') AS \"c{i}\""));

            var plan = RelOptUtil.toString(PlanToClrAsAHostDoes(
                $"SELECT {projected} FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.type') = 'Park'"));

            plan.Should().NotContain("CosmosProject(DOC=[$0])",
                "the trimmer's projection of the document must not be the statement's SELECT");

            for (var i = 0; i < columns; i++)
                plan.Should().Contain($"c{i}=[JSON_VALUE($0, '$.data.{paths[i]}')]",
                    "every column the query asks for is extracted by the service");

            plan.Should().NotContain("ClrEnumerableProject",
                "and nothing is left above the converter to extract here");
        }

    }

}
