using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Apache.Calcite.Extensions.Adapter.Cursor;
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
    public class CosmosToClrCursorConverterTests
    {

        static readonly CosmosContainerMetadata Products = TestReasoning.Bind(new CosmosContainerMetadata("products", new[] { "/category" }));

        /// <summary>
        /// An executor that answers with documents rather than with a service, and records what it was
        /// asked.
        /// </summary>
        sealed class StubExecutor : ICosmosQueryExecutor
        {

            readonly string[] _documents;
            readonly JsonElement[]? _elements;

            public StubExecutor(params string[] documents)
            {
                _documents = documents;
            }

            /// <summary>
            /// Answers with documents given as the bytes the service sent, which a string cannot carry where they are
            /// not UTF-8.
            /// </summary>
            public StubExecutor(IEnumerable<byte[]> documents)
            {
                _documents = [];
                _elements = documents.Select(d => JsonDocument.Parse(d).RootElement.Clone()).ToArray();
            }

            public CosmosQuery? Executed { get; private set; }

            /// <summary>
            /// The token the executor was opened with, which is the one the plan's opener handed down
            /// rather than one baked into the plan.
            /// </summary>
            public CancellationToken Token { get; private set; }

            /// <summary>
            /// The cursor the executor opened, which records the token of every advance it was given.
            /// </summary>
            public ListCursor<JsonElement>? Cursor { get; private set; }

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                Executed = query;
                Token = cancellationToken;

                // Yields at the open and on each advance, so the plan above is exercised against a
                // genuinely asynchronous cursor rather than one that happens to complete synchronously.
                await Task.Yield();

                return Cursor = _elements is null ? ListCursor.Documents(_documents) : new ListCursor<JsonElement>(_elements);
            }

        }

        /// <summary>
        /// An executor that serves one document as each statement's select list selects it, whatever its <c>WHERE</c>: the item
        /// whole, or an object of the item, its paths and its guarded paths, as a projection writes them.
        /// </summary>
        internal sealed class SelectingExecutor(string document) : ICosmosQueryExecutor
        {

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                return new(ListCursor.Documents(new[] { Select(query.Sql) }));
            }

            /// <summary>
            /// The row a statement's select list selects of the document, failing where it is no shape this reads.
            /// </summary>
            string Select(string sql)
            {
                var statement = System.Text.RegularExpressions.Regex.Match(sql, @"^SELECT VALUE (c|\{ (?<members>.*) \}) FROM \w+ c( WHERE .*)?$");
                if (statement.Success == false)
                    throw new InvalidOperationException("No row is served for " + sql);

                if (statement.Groups["members"].Success == false)
                    return document;

                var item = System.Text.Json.Nodes.JsonNode.Parse(document)!;
                var row = new System.Text.Json.Nodes.JsonObject();
                foreach (var member in Members(statement.Groups["members"].Value))
                {
                    var named = System.Text.RegularExpressions.Regex.Match(member, @"^""(?<key>[^""]+)"": (?<value>.+)$");
                    var value = named.Groups["value"].Value;
                    var guarded = System.Text.RegularExpressions.Regex.Match(value, @"^\(IS_(?<test>[A-Z]+)\((?<path>c[\w.]*)\) \? \k<path> : null\)$");
                    var path = guarded.Success ? guarded.Groups["path"].Value : value;
                    if (named.Success == false || System.Text.RegularExpressions.Regex.IsMatch(path, @"^c(\.\w+)*$") == false)
                        throw new InvalidOperationException("No column is served for " + member + " in " + sql);

                    // An undefined value is no member of the row, as the service omits it.
                    if (At(item, path, out var held) == false)
                        continue;

                    row[named.Groups["key"].Value] = guarded.Success && Holds(guarded.Groups["test"].Value, held) == false ? null : held?.DeepClone();
                }

                return row.ToJsonString();
            }

            static IEnumerable<string> Members(string members)
            {
                var (depth, start) = (0, 0);
                for (var i = 0; i < members.Length; i++)
                {
                    if (members[i] == '(')
                        depth++;
                    else if (members[i] == ')')
                        depth--;
                    else if (depth == 0 && string.CompareOrdinal(members, i, ", ", 0, 2) == 0)
                    {
                        yield return members[start..i];
                        start = i + 2;
                    }
                }

                yield return members[start..];
            }

            static bool At(System.Text.Json.Nodes.JsonNode item, string path, out System.Text.Json.Nodes.JsonNode? held)
            {
                held = item;
                foreach (var name in path.Split('.').Skip(1))
                    if (held is not System.Text.Json.Nodes.JsonObject members || members.TryGetPropertyValue(name, out held) == false)
                        return false;

                return true;
            }

            static bool Holds(string test, System.Text.Json.Nodes.JsonNode? held)
            {
                var kind = held switch
                {
                    null => "NULL",
                    System.Text.Json.Nodes.JsonArray => "ARRAY",
                    System.Text.Json.Nodes.JsonObject => "OBJECT",
                    _ => held.GetValueKind() switch { JsonValueKind.String => "STRING", JsonValueKind.Number => "NUMBER", _ => "BOOL" },
                };

                return test == kind || (test == "PRIMITIVE" && kind is "NULL" or "STRING" or "NUMBER" or "BOOL");
            }

        }

        /// <summary>
        /// An executor that answers each request over the documents given as the service answers it on a target, its
        /// <c>WHERE</c> evaluated on each document (<see cref="DocumentRequests"/>).
        /// </summary>
        sealed class AnsweringExecutor(string[] documents, CosmosServiceTarget target) : ICosmosQueryExecutor
        {

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                return new(ListCursor.Documents(DocumentRequests.Answer(query, partitionKey, documents, target)));
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

        public CosmosToClrCursorConverterTests()
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
                Products.WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(Catalog))),
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
                org.apache.calcite.sql.util.SqlOperatorTables.chain(
                    SqlStdOperatorTable.instance(),
                    Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance,
                    // The geography operators are a host's to chain, and a geography column exists only
                    // where one has: no column is typed a geometry, so a shape reaches a query as
                    // CLR_ST_GEOG_GEOMFROMGEOJSON over a path.
                    Apache.Calcite.Geography.Sql.GeographyOperatorTable.Instance()),
                catalogReader, _typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(_typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).rel;
        }

        /// <summary>
        /// Plans a statement and asks for the best plan in the cursor convention, which is what a
        /// caller of this adapter asks for however it then reads the rows.
        /// </summary>
        RelNode PlanToClr(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
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
        /// <c>ClrCursorProject.Implement</c> raises <c>UnsupportedOperationException</c>, exactly as
        /// Calcite's own <c>EnumerableProject.implement</c> does — <em>"EnumerableCalcRel is always
        /// better"</em> — and the rule that rewrites one into a calc is a <c>TransformationRule</c>.
        /// <c>VolcanoPlanner.addRule</c> does not register a transformation rule's operand against a
        /// <c>PhysicalNode</c>, and <c>ClrCursorRel</c> is one, so no calc rule can fire during the
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

            foreach (var rule in Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return Calcs(planner.findBestExp());
        }

        /// <summary>
        /// Runs the calc pass a host runs over the plan chosen, without which a projection left in process does not
        /// implement (<see cref="PlanToClrWithHostRules"/>).
        /// </summary>
        static RelNode Calcs(RelNode best)
        {
            var program = new org.apache.calcite.plan.hep.HepProgramBuilder();

            foreach (var rule in Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
            hep.setRoot(best);

            return hep.findBestExp();
        }

        /// <summary>
        /// Implements a planned tree as the factory a caller opens.
        /// </summary>
        static ClrCursorFactory Implement(RelNode rel)
        {
            var implementor = new ClrCursorRelImplementor(rel.getCluster().getRexBuilder(), new java.util.HashMap());
            return implementor.ImplementRoot((ClrCursorRel)rel, ClrCursorPrefer.Array);
        }

        /// <summary>
        /// Compiles a planned tree, opens it with await, and reads every row it produces with awaited
        /// advances.
        /// </summary>
        async Task<List<object>> Execute(RelNode rel)
        {
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            var rows = new List<object>();

            await using (var cursor = await Implement(rel).OpenAsync(context, CancellationToken.None))
                while (await cursor.ReadAsync(CancellationToken.None))
                    rows.Add(cursor.Current!);

            return rows;
        }

        /// <summary>
        /// Compiles the same planned tree, opens it synchronously, and reads every row it produces with
        /// synchronous advances.
        /// </summary>
        /// <remarks>
        /// For a Cosmos plan that blocks a thread for the first page at the open and for each later page
        /// at the advance that needs it; the test using it is about the rows, not about the cost.
        /// </remarks>
        List<object> ExecutePulled(RelNode rel)
        {
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            var rows = new List<object>();

            using (var cursor = Implement(rel).Open(context))
                while (cursor.Read())
                    rows.Add(cursor.Current!);

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

            plan.Should().BeOfType<CosmosToClrCursorConverter>();
            plan.getInput(0).getConvention().Should().BeSameAs(_table.Convention);
        }

        // ── Execution ─────────────────────────────────────────────────────────────

        /// <remarks>
        /// A one-column result is the value rather than a one-element row, which is what
        /// <c>ImplementRoot</c> arranges and what every caller of a query expects.
        /// </remarks>
        [Fact]
        public async Task ShouldReadASingleColumnAsTheValueItself()
        {
            Given("""{ "id": "a" }""", """{ "id": "b" }""");

            var rows = await Execute(PlanToClr("SELECT \"id\" FROM products AS c"));

            rows.Should().Equal("a", "b");
        }

        /// <remarks>
        /// <b>The plan carries no mode, so the same tree reads either way.</b> The cursor convention
        /// builds both opens from one plan, and the converter's synchronous one waits for what the
        /// awaiting one awaits — so the thing to hold is that waiting does not change the answer.
        /// <para>
        /// It says nothing about what the waiting costs, which is a blocked thread per page and is
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
        /// <b>The opener's token reaches the service call that sends the statement.</b> The plan carries
        /// no token of its own: the awaiting open ends in the implementor's token parameter, which the
        /// factory binds to whatever its caller gave <c>OpenAsync</c>, and the converter hands it to the
        /// executor. The first page — the one the open awaits — is therefore cancellable.
        /// <para>
        /// The discriminator is <c>CanBeCanceled</c>: it is <c>false</c> for <c>CancellationToken.None</c>,
        /// so this fails if a token baked into the plan is ever what arrives instead.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldCarryTheOpenersTokenIntoTheExecutor()
        {
            Given("""{ "id": "a" }""");

            var factory = Implement(PlanToClr("SELECT \"id\" FROM products AS c"));
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            using var cts = new CancellationTokenSource();

            await using (await factory.OpenAsync(context, cts.Token))
            {
                _executor.Token.CanBeCanceled.Should().BeTrue("the opener's token should reach the executor, not a default the plan holds");

                cts.Cancel();
                _executor.Token.IsCancellationRequested.Should().BeTrue("the token the executor holds should be the opener's own");
            }
        }

        /// <remarks>
        /// <b>Each advance's token reaches the executor's cursor, which is the reason the adapter reads
        /// Cosmos through a cursor at all.</b> A sequence took its token once, at
        /// <c>GetAsyncEnumerator</c>, and a reader's per-call token had nowhere to go; a cursor takes one
        /// on every <c>ReadAsync</c>, and the converter's cursor hands it to the executor's, whose advance
        /// is where a page is fetched. The open's token is deliberately a different one, so this fails if
        /// the open's is what an advance passes on.
        /// </remarks>
        [Fact]
        public async Task ShouldCarryEachAdvancesTokenIntoTheExecutorsCursor()
        {
            Given("""{ "id": "a" }""", """{ "id": "b" }""");

            var factory = Implement(PlanToClr("SELECT \"id\" FROM products AS c"));
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            using var open = new CancellationTokenSource();
            using var first = new CancellationTokenSource();
            using var second = new CancellationTokenSource();

            await using var cursor = await factory.OpenAsync(context, open.Token);

            (await cursor.ReadAsync(first.Token)).Should().BeTrue();
            (await cursor.ReadAsync(second.Token)).Should().BeTrue();

            _executor.Cursor!.Tokens.Should().Equal(first.Token, second.Token);
        }

        /// <remarks>
        /// The plan's cursor owns the executor's, through every operator between them, so a reader that
        /// stops early and lets go of the plan releases the statement — whichever way it let go.
        /// </remarks>
        [Fact]
        public async Task DisposingThePlansCursorReleasesTheExecutorsWithAwait()
        {
            Given("""{ "id": "a" }""", """{ "id": "b" }""");

            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);
            var cursor = await Implement(PlanToClr("SELECT \"id\" FROM products AS c")).OpenAsync(context, CancellationToken.None);

            (await cursor.ReadAsync(CancellationToken.None)).Should().BeTrue();
            await cursor.DisposeAsync();

            _executor.Cursor!.Disposed.Should().BeTrue();
        }

        [Fact]
        public void DisposingThePlansCursorReleasesTheExecutorsSynchronously()
        {
            Given("""{ "id": "a" }""", """{ "id": "b" }""");

            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);
            var cursor = Implement(PlanToClr("SELECT \"id\" FROM products AS c")).Open(context);

            cursor.Read().Should().BeTrue();
            cursor.Dispose();

            _executor.Cursor!.Disposed.Should().BeTrue();
        }

        /// <remarks>
        /// One cursor, one position: a reader may alternate the two ways of advancing on one open plan,
        /// and reads consecutive rows either way.
        /// </remarks>
        [Fact]
        public async Task ThePlanMayBeAdvancedEitherWayOnEachRead()
        {
            Given("""{ "id": "a" }""", """{ "id": "b" }""", """{ "id": "c" }""");

            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);
            await using var cursor = await Implement(PlanToClr("SELECT \"id\" FROM products AS c")).OpenAsync(context, CancellationToken.None);

            cursor.Read().Should().BeTrue();
            cursor.Current.Should().Be("a");
            (await cursor.ReadAsync(CancellationToken.None)).Should().BeTrue();
            cursor.Current.Should().Be("b");
            cursor.Read().Should().BeTrue();
            cursor.Current.Should().Be("c");
            (await cursor.ReadAsync(CancellationToken.None)).Should().BeFalse();
        }

        /// <remarks>
        /// The statement is sent by the open, not by the first read: a plan opened and never read has
        /// still executed it, as a command's <c>ExecuteReader</c> has.
        /// </remarks>
        [Fact]
        public async Task TheOpenSendsTheStatement()
        {
            Given("""{ "id": "a" }""");

            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);
            await using var cursor = await Implement(PlanToClr("SELECT \"id\" FROM products AS c")).OpenAsync(context, CancellationToken.None);

            _executor.Executed.Should().NotBeNull();
            _executor.Cursor!.Tokens.Should().BeEmpty("nothing has been read yet");
        }

        /// <remarks>
        /// A plan is implemented once for each way it can be opened, and both render the same statement;
        /// a hook counting statements should see it once (#211).
        /// </remarks>
        [Fact]
        public void TheStatementIsAnnouncedOncePerPlan()
        {
            var plan = PlanToClr("SELECT \"id\" FROM products AS c");

            var announced = new List<string>();
            using (org.apache.calcite.runtime.Hook.QUERY_PLAN.addThread(new Collect(announced)))
                Implement(plan);

            announced.Should().ContainSingle().Which.Should().Contain("FROM products");
        }

        /// <summary>
        /// Collects every value a hook is run with as text.
        /// </summary>
        sealed class Collect(List<string> values) : java.util.function.Consumer
        {

            public void accept(object o) => values.Add(o?.ToString() ?? "");

            public java.util.function.Consumer andThen(java.util.function.Consumer after) => throw new NotSupportedException();

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
        /// A projected geography reads back, which is what #149 said it did not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The statement was right and there was nothing to receive it.</b> No column is typed a
        /// geometry, so a stored shape reaches a query as
        /// <c>CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location'))</c>; pushed down the
        /// constructor disappears and the path goes, because Cosmos reads the property as the shape.
        /// What came back was the GeoJSON object and the plan had typed the column <c>GEOMETRY</c>,
        /// for which <c>CosmosJson.GetValue</c> had no case at all — <em>No Cosmos JSON reading is
        /// defined for SQL type 'GEOMETRY'</em>, at the first row rather than at planning.
        /// </para>
        /// <para>
        /// The value is a JTS <see cref="org.locationtech.jts.geom.Geometry"/> stamped 4326, because
        /// the reading is the geography package's own constructor rather than a second one. A caller
        /// gets what it would have got had the projection stayed in process.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldReadAProjectedGeography()
        {
            Given("""{ "g": { "type": "Point", "coordinates": [0.5, 0.25] } }""");

            var rows = await Execute(PlanToClr(
                "SELECT CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c.\"DOC\", '$.location')) AS \"g\" FROM products AS c"));

            _executor.Executed!.Value.Sql.Should().Contain("IS_OBJECT(c.location) OR IS_ARRAY(c.location) ? c.location : null",
                "the shape is the path under a JSON_QUERY's own guard: " + _executor.Executed!.Value.Sql);

            rows.Should().HaveCount(1);

            var geometry = rows[0].Should().BeAssignableTo<org.locationtech.jts.geom.Geometry>().Subject;
            geometry.toText().Should().Be("POINT (0.5 0.25)");
            geometry.getSRID().Should().Be(4326, "the geography package's constructor stamps WGS84, and it is that constructor");
        }

        /// <summary>
        /// A scalar at the path reads as null rather than failing, which is what the accessor answers
        /// in process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The guard is what makes the two agree, and without it they did not.</b> Measured in
        /// <c>CalciteGeographyReadingMeasurementTests</c>: in process
        /// <c>JSON_QUERY</c> over a scalar answers null and the constructor is never reached, so the
        /// column is null. The bare path would have sent the scalar, and the reader would have raised
        /// over text that is not GeoJSON — an error where the engine answers a value, which is the one
        /// trade this adapter refuses everywhere.
        /// </para>
        /// <para>
        /// The stub ignores the SQL, so what is asserted here is the <em>reading</em> of a guard that
        /// answered null; that the service evaluates the guard is the statement asserted above it.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ShouldReadAGeographyAsNullWhereTheGuardAnsweredNull()
        {
            Given("""{ }""");

            (await Execute(PlanToClr(
                "SELECT CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c.\"DOC\", '$.location')) AS \"g\" FROM products AS c")))
                .Should().Equal(new object[] { null! });
        }

        /// <remarks>
        /// An object at the path that is not GeoJSON fails rather than lies, which is the refusal every
        /// other declared reading makes — and the in-process constructor raises over the same input, so
        /// the two agree by both failing.
        /// </remarks>
        [Fact]
        public async Task ShouldRefuseAnObjectThatIsNotGeoJson()
        {
            Given("""{ "g": { "kind": "somewhere" } }""");

            var plan = PlanToClr(
                "SELECT CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c.\"DOC\", '$.location')) AS \"g\" FROM products AS c");

            var act = async () => await Execute(plan);
            (await act.Should().ThrowAsync<CosmosMaterializationException>()).WithMessage("*not GeoJSON*");
        }

        /// <summary>
        /// A geography beside another column reads the same way, the row being an <c>object[]</c>.
        /// </summary>
        /// <remarks>
        /// Unlike the UUID case in <see cref="ShouldReadALoneUuidColumn"/>, the arity decided nothing
        /// here: with no reading for <c>GEOMETRY</c> at all the reader raised before any row shape was
        /// reached, which is why #149 reports it at one column and several alike.
        /// </remarks>
        [Fact]
        public async Task ShouldReadAGeographyBesideAnotherColumn()
        {
            Given("""{ "g": { "type": "Point", "coordinates": [0.5, 0.25] }, "n": "widget" }""");

            var rows = await Execute(PlanToClr(
                "SELECT CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c.\"DOC\", '$.location')) AS \"g\", JSON_VALUE(c.\"DOC\", '$.name') AS \"n\" FROM products AS c"));

            rows.Should().HaveCount(1);

            var row = (object[])rows[0];
            row[0].Should().BeAssignableTo<org.locationtech.jts.geom.Geometry>().Subject.toText().Should().Be("POINT (0.5 0.25)");
            row[1].Should().Be("widget");
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

        // ── Text that is not UTF-8 ────────────────────────────────────────────────

        /// <summary>
        /// Exposes a table whose container holds documents given as JSON text in which each <c>&lt;HH HH&gt;</c> stands
        /// for those raw bytes, as Azure stores and returns what a client wrote that way.
        /// </summary>
        void GivenBytes(params string[] templates)
        {
            static byte[] Bytes(string template)
            {
                var bytes = new List<byte>();

                for (var at = 0; at < template.Length;)
                {
                    var open = template.IndexOf('<', at);
                    if (open < 0)
                        open = template.Length;

                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(template[at..open]));
                    if (open == template.Length)
                        break;

                    var close = template.IndexOf('>', open);
                    foreach (var hex in template[(open + 1)..close].Split(' '))
                        bytes.Add(System.Convert.ToByte(hex, 16));

                    at = close + 1;
                }

                return bytes.ToArray();
            }

            _executor = new StubExecutor(templates.Select(Bytes));
            Register(new CosmosTable(Products, _executor));
        }

        /// <summary>
        /// A pushed projection over a value holding text a client wrote that is not UTF-8 fails naming the column, the
        /// document and the bytes, the document by the column the plan bound to its <c>id</c>.
        /// </summary>
        /// <remarks>
        /// What failed with <c>System.Text.Json</c>'s <em>"Cannot transcode invalid UTF-8 JSON text to UTF-16 string"</em>,
        /// which named none of them (<c>reports/verify-bytes.md</c>, L7). The rows are what the service sent the
        /// statement, measured on the probe account.
        /// </remarks>
        [Fact]
        public async Task ShouldRefuseTextThatIsNotUtf8NamingTheColumnAndTheDocument()
        {
            GivenBytes("""{ "id": "good", "s": "ok" }""", """{ "id": "raw", "s": "x<ED A0 80>y" }""");

            var act = () => Execute(PlanToClr("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.s') AS \"s\" FROM products AS c"));

            await act.Should().ThrowAsync<CosmosMaterializationException>()
                .WithMessage("""Column 's' of document "raw" holds text that is not UTF-8 at $: the bytes ED A0 80 in "x\xED\xA0\x80y". Cosmos DB stores*""");
        }

        /// <summary>
        /// The document column names the document by its own <c>id</c> where the statement keeps <c>DOC</c> and drops the
        /// column, and says where in the document the bytes are.
        /// </summary>
        [Fact]
        public async Task ShouldNameTheDocumentFromTheDocumentColumn()
        {
            GivenBytes("""{ "DOC": { "id": "raw", "category": "a", "o": { "t": "<FF>" } } }""");

            var act = () => Execute(PlanToClr("SELECT c.\"DOC\" FROM products AS c"));

            await act.Should().ThrowAsync<CosmosMaterializationException>()
                .WithMessage("""Column 'DOC' of document "raw" holds text that is not UTF-8 at $.o.t: the bytes FF in "\xFF".*""");
        }

        /// <summary>
        /// A column the plan only names <c>id</c> is not the document's: the document is named by the binding, not the
        /// name, and here there is none to name.
        /// </summary>
        [Fact]
        public async Task ShouldNotTakeAColumnNamedIdForTheDocuments()
        {
            GivenBytes("""{ "id": "Widget", "s": "<C0 AF>" }""");

            var act = () => Execute(PlanToClr("SELECT JSON_VALUE(c.\"DOC\", '$.name') AS \"id\", JSON_VALUE(c.\"DOC\", '$.s') AS \"s\" FROM products AS c"));

            await act.Should().ThrowAsync<CosmosMaterializationException>()
                .WithMessage("""Column 's' holds text that is not UTF-8 at $: the bytes C0 AF in "\xC0\xAF".*""");
        }

        /// <summary>
        /// A projected <c>JSON_QUERY</c> refuses the bytes as every other reading does, where it answered each as an
        /// escaped U+FFFD without a word (<c>reports/verify-bytes.md</c>, L15).
        /// </summary>
        [Fact]
        public async Task ShouldRefuseAJsonQueryOverTextThatIsNotUtf8RatherThanReplaceIt()
        {
            GivenBytes("""{ "q": { "k<FF>": 1 } }""");

            var act = () => Execute(PlanToClr("SELECT JSON_QUERY(c.\"DOC\", '$.o') AS \"q\" FROM products AS c"));

            await act.Should().ThrowAsync<CosmosMaterializationException>()
                .WithMessage("""Column 'q' holds text that is not UTF-8 at $, in a property name: the bytes FF in "k\xFF".*""");
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

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = trimmed.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
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
        /// document size, only on <see cref="CosmosToClrCursorConverter.MinimumDocumentWidth"/>.
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

            plan.Should().NotContain("ClrCursorProject",
                "and nothing is left above the converter to extract here");
        }

        /// <summary>
        /// A projection pushes under a conjunct that stays in process, carrying what the conjunct reads,
        /// rather than the statement selecting the document for it (#210).
        /// </summary>
        /// <remarks>
        /// <c>products</c> declares no UUID form, so the cast comparison has no Cosmos form and stays
        /// above the converter. The type test renders, and reaches the service.
        /// </remarks>
        [Fact]
        public void ShouldPushAProjectionUnderAResidualFilterThatReadsAProjectedColumn()
        {
            var plan = RelOptUtil.toString(PlanToClrAsAHostDoes(
                "SELECT JSON_VALUE(c.\"DOC\", '$.data.id') AS \"a\", JSON_VALUE(c.\"DOC\", '$.data.name') AS \"b\" FROM products AS c " +
                "WHERE JSON_VALUE(c.\"DOC\", '$.type') = 'Park' AND CAST(JSON_VALUE(c.\"DOC\", '$.data.id') AS UUID) = UUID'3a900000-0000-4000-8000-000000000001'"));

            plan.Should().NotContain("CosmosProject(DOC=[$0])", "the residual reads a column the projection already selects: " + plan);
            plan.Should().Contain("a=[JSON_VALUE($0, '$.data.id')]", plan);
            plan.Should().Contain("b=[JSON_VALUE($0, '$.data.name')]", plan);
            plan.Should().Contain("CosmosFilter", "the conjunct that renders still reaches the service: " + plan);
            plan.Should().Contain(":UUID", "and the one that does not is still checked, above: " + plan);
            System.Text.RegularExpressions.Regex.Matches(plan, "CosmosFilter").Count.Should().Be(1, "and the weakening it implies is pushed once: " + plan);
        }

        /// <summary>
        /// Where the residual reads a path the query does not select, the path is selected for it.
        /// </summary>
        [Fact]
        public void ShouldPushAProjectionUnderAResidualFilterCarryingThePathItReads()
        {
            var plan = RelOptUtil.toString(PlanToClrAsAHostDoes(
                "SELECT JSON_VALUE(c.\"DOC\", '$.data.name') AS \"b\" FROM products AS c " +
                "WHERE JSON_VALUE(c.\"DOC\", '$.type') = 'Park' AND CAST(JSON_VALUE(c.\"DOC\", '$.data.ref') AS UUID) = UUID'3a900000-0000-4000-8000-000000000001'"));

            plan.Should().NotContain("CosmosProject(DOC=[$0])", plan);
            plan.Should().Contain("JSON_VALUE($0, '$.data.ref')]", "the residual's path is carried as a column: " + plan);
            plan.Should().Contain("b=[JSON_VALUE($0, '$.data.name')]", plan);
        }

        /// <summary>
        /// Not a projection with a reading that can refuse a value: beneath the conjunct it is read for
        /// rows the conjunct drops, where Calcite never reads them.
        /// </summary>
        /// <remarks>
        /// A stored 1E+19 read as a <c>BIGINT</c> is refused, and over a document the conjunct drops the
        /// statement failed where Calcite answers. A <c>DOUBLE</c> refuses no number, and still moves. S sends no
        /// weakening of the cast to <c>UUID</c>, which the IR reads as opaque, so the residual projection carries the
        /// document beneath the filter alone (a projection placement the switch listed), and the <c>BIGINT</c> stays above.
        /// </remarks>
        [Fact]
        public void ShouldNotPushAReadingThatCanRefuseUnderAResidualFilter()
        {
            const string Residual = "WHERE JSON_VALUE(c.\"DOC\", '$.type') = 'Park' AND CAST(JSON_VALUE(c.\"DOC\", '$.data.ref') AS UUID) = UUID'3a900000-0000-4000-8000-000000000001'";

            var refusing = RelOptUtil.toString(PlanToClrAsAHostDoes(
                "SELECT JSON_VALUE(c.\"DOC\", '$.data.name') AS \"b\", JSON_VALUE(c.\"DOC\", '$.data.count' RETURNING BIGINT) AS \"n\" FROM products AS c " + Residual));
            var total = RelOptUtil.toString(PlanToClrAsAHostDoes(
                "SELECT JSON_VALUE(c.\"DOC\", '$.data.name') AS \"b\", JSON_VALUE(c.\"DOC\", '$.data.count' RETURNING DOUBLE) AS \"n\" FROM products AS c " + Residual));

            System.Text.RegularExpressions.Regex.Matches(refusing, "data.count").Count.Should().Be(1, "the BIGINT is read above the conjunct, by Calcite: " + refusing);
            refusing.Should().NotContain("CosmosProject(b=", refusing);
            refusing.Should().Contain("ClrCursorProject(b=[JSON_VALUE($0, '$.data.name')], n=[JSON_VALUE($0, '$.data.count')])", refusing);
            refusing.Should().Contain("CosmosFilter", "what renders of the filter still reaches the service: " + refusing);

            total.Should().Contain("CosmosProject(b=[JSON_VALUE($0, '$.data.name')], n=[JSON_VALUE($0, '$.data.count')]", "the DOUBLE moves beneath: " + total);
        }

        /// <summary>
        /// The document beside a column read from it, under a typed comparison that stays in process.
        /// </summary>
        const string DocumentBesideATypedResidual =
            "SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.price') AS \"p\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2 = 12";

        /// <summary>
        /// A weakening is pushed once beneath a residual over a projection that carries the document, though the split
        /// weakens a filter over a projection as it weakens any other.
        /// </summary>
        /// <remarks>
        /// The trimmer leaves its projection of the document beneath the filter, and the residual projection and the
        /// transpose put a projection between a residual and the half beneath it, where the weakening is already pushed.
        /// What a filter beneath a projection applies is read through the rows it reads
        /// (<c>CosmosFilterSplitRule.AlreadyApplied</c>), so the filter over the projection finds the weakening applied
        /// there. Read only through its own rows, with the split weakening over a projection, the same plan sent
        /// <c>IS_DEFINED(c.price) AND IS_DEFINED(c.price)</c> over two filters, as #210 found.
        /// </remarks>
        [Fact]
        public void ShouldPushAWeakeningOnceUnderAResidualProjectionCarryingTheDocument()
        {
            var best = PlanToClrAsAHostDoes(DocumentBesideATypedResidual);
            var plan = RelOptUtil.toString(best);

            System.Text.RegularExpressions.Regex.Matches(plan, "CosmosFilter").Count.Should().Be(1, "the weakening is pushed in one filter: " + plan);

            var sent = Sent(best).Should().ContainSingle(plan).Which;
            System.Text.RegularExpressions.Regex.Matches(sent, System.Text.RegularExpressions.Regex.Escape("IS_DEFINED(c.price)")).Count.Should().Be(1, "and sent once: {0}", sent);
        }

        /// <summary>
        /// Where a condition stays in process over the trimmer's projection of the document, the statement selects that
        /// projection, or the view's, under the <c>WHERE</c> it sent before.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The split weakens the filter over the trimmer's projection as over any input, and its pair there is the plan
        /// whose <c>SELECT</c> is that projection beneath the residual, which the converter's width prefers to the item
        /// whole or to the document beside columns read from it: the condition kept in process reads the document anyway,
        /// and what a column held is read from the document in process.
        /// </para>
        /// <para>
        /// The <c>WHERE</c> is a regression pin: the conjuncts the statement sent while the split weakened nothing over a
        /// projection, each once, so the same documents are read.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(DocumentBesideATypedResidual,
            "SELECT VALUE { \"DOC\": c }",
            "IS_DEFINED(c.price)")]
        [InlineData("SELECT c.\"DOC\" FROM products AS c WHERE UPPER(JSON_VALUE(c.\"DOC\", '$.name')) LIKE '%STEEL%' AND JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 10",
            "SELECT VALUE { \"DOC\": c }",
            "(IS_DEFINED(c.name) AND (c.price > @p0))")]
        [InlineData("SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"DOC\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.type') = 'Park') AS t WHERE UPPER(JSON_VALUE(t.\"DOC\", '$.name')) LIKE '%STEEL%'",
            "SELECT VALUE { \"id\": c.id, \"DOC\": c }",
            "((c.type = @p0) AND IS_DEFINED(c.name))")]
        [InlineData("SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.name') AS \"n\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.name') > 'st' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.region')) = 'North'",
            "SELECT VALUE { \"DOC\": c }",
            "((IS_BOOL(c.name) OR (c.name > @p0)) AND ((c.name > @p1) OR NOT IS_STRING(c.name)) AND IS_DEFINED(c.region))")]
        [InlineData("SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.price') AS \"p\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') = '30' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.region')) = 'North'",
            "SELECT VALUE { \"DOC\": c }",
            "(((c.price = @p0) OR (IS_NUMBER(c.price) AND (c.price = @p1))) AND IS_DEFINED(c.region))")]
        public void ShouldSelectTheTrimmersProjectionUnderTheSameWhereWhereAConditionStaysInProcess(string sql, string select, string where)
        {
            var best = PlanToClrAsAHostDoes(sql);
            var sent = Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which;

            var from = sent.IndexOf(" FROM products c", StringComparison.Ordinal);
            var conditions = sent.IndexOf(" WHERE ", StringComparison.Ordinal);
            from.Should().BePositive("{0}", sent);
            conditions.Should().BeGreaterThan(from, "{0}", sent);

            sent[..from].Should().Be(select, "the statement selects the projection beneath the condition kept in process: {0}", sent);
            sent[(conditions + " WHERE ".Length)..].Should().Be(where, "under the WHERE it sent before: {0}", sent);
            ConjunctsOf(sent[(conditions + " WHERE ".Length)..]).Should().OnlyHaveUniqueItems("no conjunct is sent twice: {0}", sent);
        }

        /// <summary>
        /// Not a view's column whose reading can refuse a value, under a condition a split over the view leaves in process,
        /// whatever that condition reads: the statement selects no such column, under the <c>WHERE</c> it sends where the
        /// split pushes beneath the view.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The view's projection, the statement's <c>SELECT</c> beneath the condition kept in process, is read for every row
        /// the service returns, and Calcite computes the column only for the rows that reach it: a stored 1.5 read as an
        /// <c>INTEGER</c> is refused, and over a document whose name the case fold drops the statement failed where Calcite
        /// answers.
        /// </para>
        /// <para>
        /// A condition that reads the column only in a <c>CASE</c> branch, or in a later operand of <c>COALESCE</c>, does not
        /// read it for every row, so it was selected there too and failed; and a <c>JSON_QUERY</c> returning an
        /// <c>INTEGER ARRAY</c> refuses <c>[1.5]</c>, though the walk reads it as JSON text, and one returning a
        /// <c>VARCHAR ARRAY</c> refuses <c>[1]</c>, an element the array's guard lets through.
        /// </para>
        /// <para>
        /// A view over a filtered view passes the column up as a bare reference, and the transpose carries the half beneath
        /// both projections, so the statement selected the column the view beneath computes and failed the same way.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(true, "SELECT t.\"n\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS t WHERE INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.name)")]
        [InlineData(true, "SELECT t.\"n\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS t WHERE JSON_VALUE(t.\"DOC\", '$.type') = 'Park' AND INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "((c.type = @p0) AND IS_DEFINED(c.name))")]
        [InlineData(false, "SELECT t.\"DOC\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS t WHERE INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.name)")]
        [InlineData(true, ViewOfARefusingReading + "INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel' AND COALESCE(JSON_VALUE(t.\"DOC\", '$.s'), CAST(t.\"n\" AS VARCHAR)) = 'a'",
            "IS_DEFINED(c.name)")]
        [InlineData(false, ViewOfARefusingReading + "INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel' AND COALESCE(JSON_VALUE(t.\"DOC\", '$.s'), CAST(t.\"n\" AS VARCHAR)) = 'a'",
            "IS_DEFINED(c.name)")]
        [InlineData(false, ViewOfARefusingReading + "CASE WHEN INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel' THEN t.\"n\" > 10 ELSE FALSE END",
            "(IS_DEFINED(c.name) AND (c.n > @p0))")]
        [InlineData(true, "SELECT t.\"a\" FROM (SELECT c.\"DOC\", JSON_QUERY(c.\"DOC\", '$.n' RETURNING INTEGER ARRAY) AS \"a\" FROM products AS c) AS t WHERE INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.name)")]
        [InlineData(true, "SELECT t.\"a\" FROM (SELECT c.\"DOC\", JSON_QUERY(c.\"DOC\", '$.n' RETURNING VARCHAR ARRAY) AS \"a\" FROM products AS c) AS t WHERE INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.name)")]
        [InlineData(false, "SELECT t.\"DOC\" FROM (SELECT c.\"DOC\", JSON_QUERY(c.\"DOC\", '$.n' RETURNING VARCHAR ARRAY) AS \"a\" FROM products AS c) AS t WHERE INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.name)")]
        [InlineData(false, "SELECT u.\"d\"" + ThroughAFilteredViewOfARefusingReading,
            "((c.type = @p0) AND IS_DEFINED(c.name))")]
        [InlineData(false, "SELECT u.\"n\"" + ThroughAFilteredViewOfARefusingReading,
            "((c.type = @p0) AND IS_DEFINED(c.name))")]
        [InlineData(true, "SELECT t.\"n\", t.\"s\" FROM (SELECT u.\"DOC\", u.\"n\", UPPER(JSON_VALUE(u.\"DOC\", '$.s')) AS \"s\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS u WHERE JSON_VALUE(u.\"DOC\", '$.type') = 'Park') AS t WHERE INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "((c.type = @p0) AND IS_DEFINED(c.name))")]
        [InlineData(true, "SELECT u.\"n\" FROM (SELECT t.\"DOC\" AS \"d\", t.\"n\" FROM (SELECT c.\"DOC\", c.\"$.category\" AS \"cat\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS t WHERE t.\"cat\" = 'bikes') AS u WHERE INITCAP(JSON_VALUE(u.\"d\", '$.name')) = 'Steel'",
            "((c.category = @p0) AND IS_DEFINED(c.name))")]
        public void ShouldNotSelectAViewsReadingThatCanRefuseUnderAConditionASplitLeavesInProcess(bool trimmed, string sql, string where)
        {
            var best = trimmed ? PlanToClrAsAHostDoes(sql) : PlanToClrWithHostRules(sql);
            var sent = Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which;

            var from = sent.IndexOf(" FROM products c", StringComparison.Ordinal);
            var conditions = sent.IndexOf(" WHERE ", StringComparison.Ordinal);
            from.Should().BePositive("{0}", sent);
            conditions.Should().BeGreaterThan(from, "{0}", sent);

            System.Text.RegularExpressions.Regex.IsMatch(sent[..from], @"c\.n\b").Should().BeFalse("the view's reading that can refuse is not selected beneath the condition kept in process: {0}", sent);
            sent[(conditions + " WHERE ".Length)..].Should().Be(where, "the split beneath the view sends what it pushes: {0}", sent);
        }

        /// <summary>
        /// A view whose projection holds the document and an <c>INTEGER</c> read from it, filtered above.
        /// </summary>
        const string ViewOfARefusingReading =
            "SELECT t.\"DOC\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS t WHERE ";

        /// <summary>
        /// The rest of a statement over a view that passes up, as they are, the document and the <c>INTEGER</c> a filtered view
        /// beneath it reads from it, filtered above on a case fold.
        /// </summary>
        const string ThroughAFilteredViewOfARefusingReading =
            " FROM (SELECT t.\"DOC\" AS \"d\", t.\"n\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS t WHERE JSON_VALUE(t.\"DOC\", '$.type') = 'Park') AS u WHERE INITCAP(JSON_VALUE(u.\"d\", '$.name')) = 'Steel'";

        /// <summary>
        /// Over a document the condition kept in process drops, and whose value at the view's column its reading refuses, the
        /// plan answers as Calcite does: no row, and no failure.
        /// </summary>
        /// <remarks>
        /// Planned bare, with the engine's rules and the calc pass, and run over the stub, which serves the document as the
        /// statement selects it: the item whole. Calcite drops the document on the case fold, or reads the column only in a
        /// branch or an operand it never reaches there, so it never reads the 1.5 or the 11.5 at <c>n</c>, and a statement that
        /// selected the column beneath the condition failed with "A JSON number of '1.5' is not a whole number and cannot be
        /// read as INTEGER", and over a <c>VARCHAR ARRAY</c> holding 1 with "Expected a JSON string".
        /// </remarks>
        [Theory]
        [InlineData(ViewOfARefusingReading + "INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "{ \"id\": \"x1\", \"n\": 1.5, \"name\": \"x\" }")]
        [InlineData(ViewOfARefusingReading + "INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel' AND COALESCE(JSON_VALUE(t.\"DOC\", '$.s'), CAST(t.\"n\" AS VARCHAR)) = 'a'",
            "{ \"id\": \"x1\", \"n\": 1.5, \"name\": \"x\", \"s\": \"q\" }")]
        [InlineData(ViewOfARefusingReading + "CASE WHEN INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel' THEN t.\"n\" > 10 ELSE FALSE END",
            "{ \"id\": \"x1\", \"n\": 11.5, \"name\": \"x\" }")]
        [InlineData("SELECT t.\"DOC\" FROM (SELECT c.\"DOC\", JSON_QUERY(c.\"DOC\", '$.n' RETURNING INTEGER ARRAY) AS \"a\" FROM products AS c) AS t WHERE INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "{ \"id\": \"x1\", \"n\": [1.5], \"name\": \"x\" }")]
        [InlineData("SELECT t.\"DOC\" FROM (SELECT c.\"DOC\", JSON_QUERY(c.\"DOC\", '$.n' RETURNING VARCHAR ARRAY) AS \"a\" FROM products AS c) AS t WHERE INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "{ \"id\": \"x1\", \"n\": [1], \"name\": \"x\" }")]
        [InlineData("SELECT u.\"n\"" + ThroughAFilteredViewOfARefusingReading,
            "{ \"id\": \"x1\", \"type\": \"Park\", \"n\": 1.5, \"name\": \"x\" }")]
        public async Task ShouldAnswerAsCalciteOverAViewsReadingThatCanRefuseUnderAConditionASplitLeavesInProcess(string sql, string document)
        {
            Given(document);

            var best = PlanToClrWithHostRules(sql);
            var sent = Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which;
            sent.Should().StartWith("SELECT VALUE c ", "the statement selects the item whole, which the stub serves: {0}", sent);

            (await Execute(best)).Should().BeEmpty("Calcite drops the document without reading the view's column: {0}", sent);
        }

        /// <summary>
        /// Where the transpose will not carry a filter beneath a view whose projection holds a reading that can refuse, the
        /// split over the view still pushes what renders beneath it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Sixty <c>LIKE</c>s of a case fold of the view's column, read through the view's projection, grow past the
        /// transpose's bloat, so the filter stays above the view whatever the split does. Declining the split's pair there
        /// left nothing of the condition beneath the view, and the statement selected the view's projection with less of a
        /// <c>WHERE</c>: over <c>{"a":"z","n":1.5}</c>, which the equality drops, it read the 1.5 as an <c>INTEGER</c> and
        /// failed where Calcite answers; and a type test only the service evaluates, left to Calcite beside the
        /// <c>LIKE</c>s, did not implement.
        /// </para>
        /// <para>
        /// The statement selects the view's column beneath the condition kept in process, as it did before the split weakened
        /// over a projection (<c>TODO.md</c>, <em>A filter kept in process over a view projecting a reading that can
        /// refuse</em>).
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(false, "SELECT t.\"n\" FROM " + ViewOfTextBesideARefusingReading + " WHERE t.\"a\" = 'k' AND ", "t.\"a\"",
            "(c.a = @p0)")]
        [InlineData(true, "SELECT t.\"n\" FROM " + ViewOfTextBesideARefusingReading + " WHERE t.\"a\" = 'k' AND ", "t.\"a\"",
            "(c.a = @p0)")]
        [InlineData(false, "SELECT t.\"DOC\" FROM " + ViewOfTextBesideARefusingReading + " WHERE t.\"a\" = 'k' AND ", "t.\"a\"",
            "(c.a = @p0)")]
        [InlineData(false, "SELECT u.\"n\" FROM (SELECT t.\"DOC\" AS \"d\", t.\"a\", t.\"n\" FROM " + ViewOfTextBesideARefusingReading + " WHERE JSON_VALUE(t.\"DOC\", '$.type') = 'Park') AS u WHERE u.\"a\" = 'k' AND ", "u.\"a\"",
            "((c.type = @p0) AND (c.a = @p1))")]
        [InlineData(true, "SELECT u.\"n\" FROM (SELECT t.\"DOC\" AS \"d\", t.\"a\", t.\"n\" FROM " + ViewOfTextBesideARefusingReading + " WHERE JSON_VALUE(t.\"DOC\", '$.type') = 'Park') AS u WHERE u.\"a\" = 'k' AND ", "u.\"a\"",
            "((c.type = @p0) AND (c.a = @p1))")]
        [InlineData(false, "SELECT t.\"n\" FROM " + ViewOfTextBesideARefusingReading + " WHERE IS_STRING(JSON_VALUE(t.\"DOC\", '$.k')) AND ", "t.\"a\"",
            "(IS_STRING(c.k) AND IS_DEFINED(c.a))")]
        public void ShouldPushWhatRendersBeneathAViewsReadingThatCanRefuseWhereTheTransposeWillNotCarryTheFilter(bool trimmed, string sql, string folded, string where)
        {
            var likes = "(" + string.Join(" OR ", Enumerable.Range(1, 60).Select(i => $"INITCAP({folded}) LIKE 'W{i}%'")) + ")";
            var best = trimmed ? PlanToClrAsAHostDoes(sql + likes) : PlanToClrWithHostRules(sql + likes);
            var sent = Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which;

            var conditions = sent.IndexOf(" WHERE ", StringComparison.Ordinal);
            conditions.Should().BePositive("the split beneath the view sends what renders: {0}", sent);
            sent[(conditions + " WHERE ".Length)..].Should().Be(where, "the split beneath the view sends what renders: {0}", sent);
        }

        /// <summary>
        /// A view whose projection holds the document, a text column and an <c>INTEGER</c> read from it.
        /// </summary>
        const string ViewOfTextBesideARefusingReading =
            "(SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.a') AS \"a\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING INTEGER) AS \"n\" FROM products AS c) AS t";

        /// <summary>
        /// Over a view's filter the transpose will not carry beneath the view, the split of a condition stacked on it still
        /// pushes that condition's weakening, and the plan answers as Calcite does over a document the weakening excludes: no
        /// row, and no failure.
        /// </summary>
        /// <remarks>
        /// Sixty equalities of the view's text column, read through the view's projection, grow past the transpose's bloat, so
        /// they stay a filter over the view, the service's <c>WHERE</c> beneath the view's projection, and the case fold of
        /// the name is left above. Its split was declined as though the transpose would carry it beneath the view, which it
        /// does only once it has carried the filter there, so the statement selected the view's <c>VARCHAR ARRAY</c> under
        /// the equalities alone, and over a document with no name, which Calcite drops on the case fold without reading the
        /// array, its reading refused the element that is no string. Planned bare, with the engine's rules and the calc pass,
        /// and run over the stub, which answers each request as the service does on the target.
        /// </remarks>
        [Theory]
        [InlineData(CosmosServiceTarget.Azure, "{ \"id\": \"q1\", \"a\": \"w1\", \"x\": [\"5\", 5] }")]
        [InlineData(CosmosServiceTarget.Azure, "{ \"id\": \"q2\", \"a\": \"w1\", \"x\": [\"5\", 1.5] }")]
        [InlineData(CosmosServiceTarget.Azure, "{ \"id\": \"q3\", \"a\": \"w1\", \"x\": [\"5\", { \"b\": 1 }] }")]
        [InlineData(CosmosServiceTarget.Emulator, "{ \"id\": \"q1\", \"a\": \"w1\", \"x\": [\"5\", 5] }")]
        [InlineData(CosmosServiceTarget.Emulator, "{ \"id\": \"q2\", \"a\": \"w1\", \"x\": [\"5\", 1.5] }")]
        [InlineData(CosmosServiceTarget.Emulator, "{ \"id\": \"q3\", \"a\": \"w1\", \"x\": [\"5\", { \"b\": 1 }] }")]
        public async Task ShouldAnswerAsCalciteOverAViewsArrayBeneathAFilterTheTransposeWillNotCarry(CosmosServiceTarget target, string document)
        {
            Register(new CosmosTable(Products.WithServiceTarget(target), new AnsweringExecutor([document], target)));

            var sql = "SELECT t.\"id\" FROM (SELECT c.\"id\", c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.a') AS \"a\", JSON_QUERY(c.\"DOC\", '$.x' RETURNING VARCHAR ARRAY) AS \"m\" FROM products AS c) AS t WHERE ("
                + string.Join(" OR ", Enumerable.Range(1, 60).Select(i => $"t.\"a\" = 'w{i}'")) + ") AND INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'";
            var best = PlanToClrWithHostRules(sql);
            var sent = Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which;

            (await Execute(best)).Should().BeEmpty("Calcite drops a document with no name on the case fold without reading the view's array: {0}", sent);
        }

        /// <summary>
        /// A weakening over a projection's typed or text column, a view's or the query's own select list, is pushed once,
        /// where a filter beneath the projection applies it already.
        /// </summary>
        /// <remarks>
        /// The transpose or the residual projection carries the filter beneath the projection, where the split pushes its
        /// weakening, and the filter left over the projection asks the same weakening over the projection's column. What the
        /// filter beneath applies is also read over the projection's own columns, where they carry everything it reads, so the
        /// column is one term above and beneath it: read only over the scan, a <c>RETURNING DOUBLE</c> or a text accessor is
        /// its accessor there and a field of the projection here, and the weakening was sent in two filters.
        /// </remarks>
        [Theory]
        [InlineData(false, "SELECT t.\"DOC\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING DOUBLE) AS \"n\" FROM products AS c) AS t WHERE NOT (t.\"n\" > 10) OR INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.name)")]
        [InlineData(true, "SELECT t.\"DOC\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.n' RETURNING DOUBLE) AS \"n\" FROM products AS c) AS t WHERE NOT (t.\"n\" > 10) OR INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.name)")]
        [InlineData(false, "SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.price') AS \"p\" FROM products AS c WHERE JSON_VALUE(c.\"DOC\", '$.price') = '30' AND INITCAP(JSON_VALUE(c.\"DOC\", '$.region')) = 'North'",
            "IS_NUMBER(c.price)")]
        public void ShouldPushAWeakeningOverAViewsTypedColumnOnce(bool trimmed, string sql, string once)
        {
            var best = trimmed ? PlanToClrAsAHostDoes(sql) : PlanToClrWithHostRules(sql);
            var plan = RelOptUtil.toString(best);

            System.Text.RegularExpressions.Regex.Matches(plan, "CosmosFilter").Count.Should().Be(1, "the weakening is pushed in one filter: " + plan);

            var sent = Sent(best).Should().ContainSingle(plan).Which;
            System.Text.RegularExpressions.Regex.Matches(sent, System.Text.RegularExpressions.Regex.Escape(once)).Count.Should().Be(1, "and sent once: {0}", sent);
        }

        /// <summary>
        /// A filter over a view that renames the document sends the weakening of the condition it keeps in process.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ShouldPushAWeakeningOverAViewThatRenamesTheDocument(bool trimmed)
        {
            const string sql = "SELECT t.\"d\" FROM (SELECT c.\"DOC\" AS \"d\" FROM products AS c) AS t WHERE INITCAP(JSON_VALUE(t.\"d\", '$.name')) = 'Steel'";
            var best = trimmed ? PlanToClrAsAHostDoes(sql) : PlanToClrWithHostRules(sql);

            Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which
                .Should().Be("SELECT VALUE { \"d\": c } FROM products c WHERE IS_DEFINED(c.name)", "the case fold of the name weakens to its definedness");
        }

        /// <summary>
        /// A filter over an item of a view's <c>RETURNING VARIANT</c> column, where the transpose will not carry it beneath the
        /// view, sends no test of the item's path.
        /// </summary>
        /// <remarks>
        /// Sixty <c>LIKE</c>s of a case fold of the view's text column, read through the view's projection, grow past the
        /// transpose's bloat, so the split weakens the filter over the view, through the binding of its columns. Bound to
        /// <c>c.x.a</c> and read as the raw value there, the item's null test was weakened to <c>IS_NULL(c.x.a) OR … OR NOT
        /// IS_DEFINED(c.x.a)</c>, which drops <c>{"a":"w1","x":{"a":1}}</c> where Calcite keeps it: its item is null, because
        /// the accessor is null over an object. No path is resolved through a <c>RETURNING VARIANT</c>
        /// (<c>CosmosRexShapes.IsJsonAccessor</c>), so a projection binds none for the item and the split sends what it derives
        /// of the rest.
        /// </remarks>
        [Theory]
        [InlineData(false, "t.\"e\" IS NULL OR INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.a)")]
        [InlineData(true, "t.\"e\" IS NULL OR INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel'",
            "IS_DEFINED(c.a)")]
        [InlineData(false, "CASE WHEN t.\"e\" IS NULL THEN INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel' ELSE FALSE END",
            "(IS_DEFINED(c.name) AND IS_DEFINED(c.a))")]
        [InlineData(true, "CASE WHEN t.\"e\" IS NULL THEN INITCAP(JSON_VALUE(t.\"DOC\", '$.name')) = 'Steel' ELSE FALSE END",
            "(IS_DEFINED(c.name) AND IS_DEFINED(c.a))")]
        public void ShouldSendNoTestOfAnItemOfAViewsReturningVariantColumnWhereTheTransposeWillNotCarryTheFilter(bool trimmed, string condition, string where)
        {
            var likes = "(" + string.Join(" OR ", Enumerable.Range(1, 60).Select(i => $"INITCAP(t.\"a\") LIKE 'W{i}%'")) + ")";
            var sql = "SELECT t.\"DOC\" FROM (SELECT c.\"DOC\", JSON_VALUE(c.\"DOC\", '$.a') AS \"a\", JSON_VALUE(c.\"DOC\", '$.x' RETURNING VARIANT)['a'] AS \"e\", "
                + "INITCAP(JSON_VALUE(c.\"DOC\", '$.region')) AS \"ic\" FROM products AS c) AS t WHERE (" + condition + ") AND " + likes;
            var best = trimmed ? PlanToClrAsAHostDoes(sql) : PlanToClrWithHostRules(sql);
            var sent = Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which;

            var conditions = sent.IndexOf(" WHERE ", StringComparison.Ordinal);
            conditions.Should().BePositive("the split over the view sends what it derives: {0}", sent);
            sent[(conditions + " WHERE ".Length)..].Should().Be(where, "no test of the item's path is sent: {0}", sent);
        }

        /// <summary>
        /// A filter over a view that tests an item of a <c>RETURNING VARIANT</c>, or an accessor over one, read from the
        /// document the view passes up or renames, sends no test of the path beneath it where the transpose will not carry the
        /// filter beneath the view.
        /// </summary>
        /// <remarks>
        /// Sixty <c>LIKE</c>s of a case fold of the view's text column grow past the transpose's bloat, so the split weakens the
        /// filter over the view, through the binding of its columns, where the document is bound to the root. Resolved to
        /// <c>c.x.a</c> and read as the raw value there, the item's null test was weakened to <c>IS_NULL(c.x.a) OR … OR NOT
        /// IS_DEFINED(c.x.a)</c>, which drops <c>{"a":"w1","x":{"a":1}}</c> where Calcite keeps it: its item is null, because
        /// the accessor is null over an object. No path is resolved through a <c>RETURNING VARIANT</c>
        /// (<c>CosmosRexShapes.IsJsonAccessor</c>), so the split sends what it derives of the rest.
        /// </remarks>
        [Theory]
        [InlineData(false, "DOC", "{0}['a'] IS NULL OR {1}", "IS_DEFINED(c.a)")]
        [InlineData(true, "DOC", "{0}['a'] IS NULL OR {1}", "IS_DEFINED(c.a)")]
        [InlineData(false, "DOC", "CASE WHEN {0}['a'] IS NULL THEN {1} ELSE FALSE END", "(IS_DEFINED(c.name) AND IS_DEFINED(c.a))")]
        [InlineData(true, "DOC", "CASE WHEN {0}['a'] IS NULL THEN {1} ELSE FALSE END", "(IS_DEFINED(c.name) AND IS_DEFINED(c.a))")]
        [InlineData(false, "d", "{0}['a'] IS NULL OR {1}", "IS_DEFINED(c.a)")]
        [InlineData(true, "d", "{0}['a'] IS NULL OR {1}", "IS_DEFINED(c.a)")]
        [InlineData(false, "DOC", "JSON_VALUE({0}, '$.a') IS NULL OR {1}", "IS_DEFINED(c.a)")]
        [InlineData(true, "DOC", "JSON_VALUE({0}, '$.a') IS NULL OR {1}", "IS_DEFINED(c.a)")]
        public void ShouldSendNoTestThroughAReturningVariantReadFromAViewsDocumentWhereTheTransposeWillNotCarryTheFilter(bool trimmed, string document, string condition, string where)
        {
            var likes = "(" + string.Join(" OR ", Enumerable.Range(1, 60).Select(i => $"INITCAP(t.\"a\") LIKE 'W{i}%'")) + ")";
            var tested = string.Format(condition, $"JSON_VALUE(t.\"{document}\", '$.x' RETURNING VARIANT)", $"INITCAP(JSON_VALUE(t.\"{document}\", '$.name')) = 'Steel'");
            var sql = $"SELECT t.\"{document}\" FROM (SELECT c.\"DOC\" AS \"{document}\", JSON_VALUE(c.\"DOC\", '$.a') AS \"a\", "
                + "INITCAP(JSON_VALUE(c.\"DOC\", '$.region')) AS \"ic\" FROM products AS c) AS t WHERE (" + tested + ") AND " + likes;
            var best = trimmed ? PlanToClrAsAHostDoes(sql) : PlanToClrWithHostRules(sql);
            var sent = Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which;

            var conditions = sent.IndexOf(" WHERE ", StringComparison.Ordinal);
            conditions.Should().BePositive("the split over the view sends what it derives: {0}", sent);
            sent[(conditions + " WHERE ".Length)..].Should().Be(where, "no test of the path beneath the RETURNING VARIANT is sent: {0}", sent);
        }

        const string ReturningVariant = "JSON_VALUE(c.\"DOC\", '$.x' RETURNING VARIANT)";

        /// <summary>
        /// Where a plan would select a <c>RETURNING VARIANT</c> itself, the statement answers as Calcite does over an array and
        /// an object at the path, bare and as a host plans: a filter over an item of it beside a computed column, a view's
        /// column holding it or an item of it, and a select list holding it or an accessor over it.
        /// </summary>
        /// <remarks>
        /// Calcite holds what <c>RETURNING VARIANT</c> reads in no box, and it is null over an array or an object, so an item
        /// of it or an accessor over it is null there too. Written as the bare path, <c>c.x</c>, and read back as the raw
        /// value, the column held the array or the object, and what Calcite computed over it failed over <c>[1]</c> and
        /// read 1 over <c>{"a":1}</c>, so the null test dropped the document: wherever the residual projection carried the
        /// accessor beneath the filter, a view or a select list held it, or the planner split a view's item column into the
        /// accessor beneath an item computed in process. No clause writes it as a path now
        /// (<c>CosmosRexShapes.IsJsonAccessor</c>), as none writes <c>RETURNING DECIMAL</c>, so Calcite computes it from the
        /// document. The stub serves each document as the statement's select list selects it, whatever its <c>WHERE</c>; the
        /// statement sends none, every condition reading the <c>RETURNING VARIANT</c>.
        /// </remarks>
        [Theory]
        [InlineData(false, "SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.name') AS \"n\" FROM products AS c WHERE " + ReturningVariant + "['a'] IS NULL", "x1|Steel")]
        [InlineData(true, "SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.name') AS \"n\" FROM products AS c WHERE " + ReturningVariant + "['a'] IS NULL", "x1|Steel")]
        [InlineData(false, "SELECT t.\"id\" FROM (SELECT c.\"id\", " + ReturningVariant + " AS \"v\" FROM products AS c) AS t WHERE t.\"v\"['a'] IS NULL", "x1")]
        [InlineData(true, "SELECT t.\"id\" FROM (SELECT c.\"id\", " + ReturningVariant + " AS \"v\" FROM products AS c) AS t WHERE t.\"v\"['a'] IS NULL", "x1")]
        [InlineData(false, "SELECT t.\"id\" FROM (SELECT c.\"id\", " + ReturningVariant + "['a'] AS \"e\" FROM products AS c) AS t WHERE t.\"e\" IS NULL", "x1")]
        [InlineData(true, "SELECT t.\"id\" FROM (SELECT c.\"id\", " + ReturningVariant + "['a'] AS \"e\" FROM products AS c) AS t WHERE t.\"e\" IS NULL", "x1")]
        [InlineData(false, "SELECT c.\"id\", " + ReturningVariant + " AS \"v\" FROM products AS c WHERE " + ReturningVariant + "['a'] IS NULL", "x1|null")]
        [InlineData(true, "SELECT c.\"id\", " + ReturningVariant + " AS \"v\" FROM products AS c WHERE " + ReturningVariant + "['a'] IS NULL", "x1|null")]
        [InlineData(false, "SELECT c.\"id\", JSON_VALUE(" + ReturningVariant + ", '$.a') AS \"e\" FROM products AS c", "x1|null")]
        [InlineData(true, "SELECT c.\"id\", JSON_VALUE(" + ReturningVariant + ", '$.a') AS \"e\" FROM products AS c", "x1|null")]
        public async Task ShouldAnswerAsCalciteWhereAPlanWouldSelectAReturningVariant(bool trimmed, string sql, string row)
        {
            foreach (var document in new[] { "{ \"id\": \"x1\", \"name\": \"Steel\", \"x\": [1] }", "{ \"id\": \"x1\", \"name\": \"Steel\", \"x\": { \"a\": 1 } }" })
            {
                Register(new CosmosTable(Products, new SelectingExecutor(document)));

                var best = trimmed ? Calcs(PlanToClrAsAHostDoes(sql)) : PlanToClrWithHostRules(sql);
                var sent = Sent(best).Should().ContainSingle(RelOptUtil.toString(best)).Which;
                sent.Should().NotContain(" WHERE ", "every condition reads the RETURNING VARIANT, which is Calcite's: {0}", sent);

                (await Execute(best)).Select(Row).Should().Equal([row], "Calcite's RETURNING VARIANT is null over {0}: {1}", document, sent);
            }
        }

        /// <summary>
        /// Writes a row as its columns' values joined by a bar, a null as <c>null</c>.
        /// </summary>
        static string Row(object? row)
        {
            return string.Join("|", (row as object?[] ?? [row]).Select(v => v is null ? "null" : System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Renders the statement each pushed subtree of a plan sends.
        /// </summary>
        List<string> Sent(RelNode best)
        {
            var sent = new List<string>();
            void Walk(RelNode node)
            {
                if (ReferenceEquals(node.getConvention(), _table.Convention))
                {
                    var implementor = new CosmosImplementor(node.getCluster().getRexBuilder(), Products);
                    implementor.Visit(node);
                    sent.Add(implementor.Build().Sql);
                    return;
                }

                var inputs = node.getInputs();
                for (var i = 0; i < inputs.size(); i++)
                    Walk((RelNode)inputs.get(i));
            }

            Walk(best);
            return sent;
        }

        /// <summary>
        /// Returns the top-level conjuncts of a <c>WHERE</c> as the statement writes it: within the parentheses that wrap the
        /// whole, the parts joined by <c>AND</c> outside any other.
        /// </summary>
        static List<string> ConjunctsOf(string where)
        {
            int Closes(int open)
            {
                var depth = 0;
                for (var i = open; i < where.Length; i++)
                    if (where[i] == '(')
                        depth++;
                    else if (where[i] == ')' && --depth == 0)
                        return i;

                return -1;
            }

            if (where.StartsWith('(') && Closes(0) == where.Length - 1)
                where = where[1..^1];

            var conjuncts = new List<string>();
            var (level, start) = (0, 0);
            for (var i = 0; i < where.Length; i++)
            {
                if (where[i] == '(')
                    level++;
                else if (where[i] == ')')
                    level--;
                else if (level == 0 && string.CompareOrdinal(where, i, " AND ", 0, 5) == 0)
                {
                    conjuncts.Add(where[start..i]);
                    start = i + 5;
                }
            }

            conjuncts.Add(where[start..]);
            return conjuncts;
        }

        // ── What a point read can read ────────────────────────────────────────────

        /// <remarks>
        /// The read the predicate allows, taken: every column is a path read as its declared type, so
        /// the document the read returns answers each by walking to it. The stub hands back the
        /// document, as the service's read would. The service's own <c>id</c> and <c>_ts</c>, which are
        /// the value at the path in every document; a typed accessor is not, and withdraws the read —
        /// see below.
        /// </remarks>
        [Fact]
        public async Task APointReadReadsTypedPathsOutOfTheDocument()
        {
            Given("""{ "id": "1", "category": "bikes", "_ts": 17, "price": 120 }""");

            var rows = await Execute(PlanToClr("SELECT c.\"id\", c.\"_ts\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1'"));

            _executor.Executed!.Value.PointReadId.Should().Be("1");
            rows.Should().HaveCount(1);
            ((object[])rows[0]).Should().Equal("1", java.lang.Long.valueOf(17L));
        }

        /// <remarks>
        /// <para>
        /// A column the query reads other than as the path's value as its declared type withdraws the
        /// read, and the statement runs as the query, still routed. Each of these took the read and
        /// failed reading <c>VARCHAR</c> over what the path held — the document column over the object,
        /// the text accessor over the number, the fragment over the object (review probes H4l, H4h and
        /// H4f, measured on the emulator). The stub hands back the projected row, as the service's
        /// query would.
        /// </para>
        /// <para>
        /// The document column is not served from the read even read as JSON: the service returns a
        /// read's document in another key order and with <c>_self</c>, so <c>DOC</c> would be another
        /// string. See <c>CosmosConverters.DocumentRowBuilder</c>.
        /// </para>
        /// <para>
        /// A typed accessor joined them when its column was written through its type's test: the read
        /// walked to the object a document held at the path and failed reading it as the integer the
        /// plan declared, where the query's guard reads null as Calcite does — measured on the emulator
        /// over the differential suite's typed container, by id and by a batch of ids. The read is
        /// withdrawn by what the projection rendered, which is no longer the path.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("SELECT * FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1'", """{ "DOC": { "id": "1", "category": "bikes" }, "id": "1", "$.category": "bikes" }""")]
        [InlineData("SELECT c.\"id\", c.\"DOC\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')", """{ "id": "1", "DOC": { "id": "1", "category": "bikes" } }""")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.price') AS \"p\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1'", """{ "p": 120 }""")]
        [InlineData("SELECT JSON_QUERY(c.\"DOC\", '$.metadata') AS \"m\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '2'", """{ "m": { "sku": "B-2" } }""")]
        [InlineData("SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" = '1'", """{ "id": "1", "p": 120 }""")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.flag' RETURNING BOOLEAN) AS \"f\" FROM products AS c WHERE c.\"$.category\" = 'bikes' AND c.\"id\" IN ('1', '2')", """{ "f": true }""")]
        public async Task AColumnTheReadCannotProduceRunsTheLookupAsAQuery(string sql, string row)
        {
            Given(row);

            var rows = await Execute(PlanToClr(sql));

            _executor.Executed!.Value.PointReadId.Should().BeNull("the read cannot produce every column: " + _executor.Executed!.Value.Sql);
            _executor.Executed!.Value.PointReadIds.Should().BeNull("nor can a batch of them");
            _executor.Executed!.Value.PartitionKeyIsComplete.Should().BeTrue("the query is still routed to the partition");
            rows.Should().ContainSingle();
        }


    }

}
