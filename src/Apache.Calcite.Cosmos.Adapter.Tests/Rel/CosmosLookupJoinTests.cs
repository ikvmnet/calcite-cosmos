using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
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


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Runs a lookup join: a real plan, compiled, against stub executors.
    /// </summary>
    /// <remarks>
    /// The planner tests say the shape is chosen and the runtime tests say the batching is right. This
    /// is what says the two are connected — that the statement the rule's node renders is the statement
    /// the service is given, carrying the keys the build side actually had.
    /// </remarks>
    public class CosmosLookupJoinTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });
        static readonly CosmosContainerMetadata Orders = new("orders", new[] { "/customer" });

        /// <summary>
        /// Answers with documents and records every statement it was given.
        /// </summary>
        sealed class StubExecutor : ICosmosQueryExecutor
        {

            readonly string[] _documents;

            public StubExecutor(params string[] documents)
            {
                _documents = documents;
            }

            public List<CosmosQuery> Executed { get; } = new();

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                Executed.Add(query);

                await Task.Yield();
                return ListCursor.Documents(_documents);
            }

        }

        sealed class TestDataContext : DataContext
        {

            readonly SchemaPlus _rootSchema;
            readonly JavaTypeFactory _typeFactory;
            readonly IReadOnlyDictionary<string, object> _values;

            public TestDataContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory, IReadOnlyDictionary<string, object>? values = null)
            {
                _rootSchema = rootSchema;
                _typeFactory = typeFactory;
                _values = values ?? new Dictionary<string, object>();
            }

            public SchemaPlus getRootSchema() => _rootSchema;

            public JavaTypeFactory getTypeFactory() => _typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => _values.TryGetValue(name, out var value) ? value : null!;

        }

        CosmosTable _products = null!;
        CosmosTable _orders = null!;
        StubExecutor _productsExecutor = null!;
        StubExecutor _ordersExecutor = null!;
        CalciteSchema _rootSchema = null!;
        JavaTypeFactoryImpl _typeFactory = null!;

        void Given(string[] orders, string[] products, CosmosContainerMetadata? declared = null)
        {
            _typeFactory = new JavaTypeFactoryImpl();

            _ordersExecutor = new StubExecutor(orders);
            _productsExecutor = new StubExecutor(products);

            _orders = new CosmosTable(Orders, _ordersExecutor);
            _products = new CosmosTable(declared ?? Products, _productsExecutor);

            _rootSchema = CalciteSchema.createRootSchema(false);
            _rootSchema.add("orders", _orders);
            _rootSchema.add("products", _products);
        }

        RelNode Plan(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_orders.Convention))
                planner.addRule(rule);

            foreach (var rule in CosmosRules.GetRules(_products.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return ToCalc(planner.findBestExp());
        }

        RelNode PlanLogical(string sql)
        {
            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(_rootSchema, java.util.Collections.emptyList(), _typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var validator = SqlValidatorUtil.newValidator(
                org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance), catalogReader, _typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(org.apache.calcite.rel.RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(_typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            return converter.convertQuery(validator.validate(parsed), false, true).project();
        }

        /// <summary>
        /// Turns whatever projections and filters survived the cost-based phase into calcs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is Calcite's <c>Programs.CALC_PROGRAM</c>, and it is a pass <em>after</em> the planner
        /// rather than rules given to it. That distinction is the whole of it:
        /// <c>ClrCursorProject</c> throws when implemented — as
        /// <c>EnumerableProject.implement()</c> does upstream, saying "EnumerableCalcRel is always
        /// better" — and it is also the cheaper node, since <c>Calc</c>'s inherited cost counts one
        /// unit per expression and <c>Project</c>'s does not. Handed to Volcano, the two compete and
        /// the throwing one wins on cost. Run afterwards, there is no competition: every projection
        /// left standing becomes a calc.
        /// </para>
        /// <para>
        /// Invisible until there is a join, because until then every projection is pushed into the
        /// container and none survives to be implemented.
        /// </para>
        /// </remarks>
        static RelNode ToCalc(RelNode rel)
        {
            var program = new org.apache.calcite.plan.hep.HepProgramBuilder();

            foreach (var rule in ClrCursorRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
            hep.setRoot(rel);

            return hep.findBestExp();
        }

        static ClrCursorFactory Implement(RelNode rel)
        {
            var implementor = new ClrCursorRelImplementor(rel.getCluster().getRexBuilder(), new java.util.HashMap());
            return implementor.ImplementRoot((ClrCursorRel)rel, ClrCursorPrefer.Array);
        }

        async Task<List<object>> Execute(RelNode rel, IReadOnlyDictionary<string, object>? values = null)
        {
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory, values);

            var rows = new List<object>();

            await using (var cursor = await Implement(rel).OpenAsync(context, CancellationToken.None))
                while (await cursor.ReadAsync(CancellationToken.None))
                    rows.Add(cursor.Current!);

            return rows;
        }

        /// <summary>
        /// Opens the plan synchronously and reads it with synchronous advances, which is the join's other
        /// body: the build side read with <c>Read</c>, and each batch's fetch waited for.
        /// </summary>
        List<object> ExecutePulled(RelNode rel)
        {
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            var rows = new List<object>();

            using (var cursor = Implement(rel).Open(context))
                while (cursor.Read())
                    rows.Add(cursor.Current!);

            return rows;
        }

        static object?[] Keys(CosmosQuery query) =>
            query.Parameters.Where(p => p.Name.StartsWith(CosmosLookupJoin.KeyPrefix)).Select(p => p.Value).ToArray();

        // ── The whole path ────────────────────────────────────────────────────────

        /// <remarks>
        /// Three orders and three products, matching on two of them. What is being checked is not only
        /// that two rows come back, but that the container was asked for the two keys the orders had —
        /// which is the difference between this feature working and it being an expensive no-op.
        /// </remarks>
        [Fact]
        public async Task AJoinFetchesOnlyTheKeysTheOtherSideHas()
        {
            Given(
                orders: new[]
                {
                    """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","customer":"c1"}""",
                    """{"DOC":{"id":"b"},"id":"b","_ts":1,"_etag":"e","customer":"c2"}""",
                },
                products: new[]
                {
                    """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","category":"bikes"}""",
                    """{"DOC":{"id":"b"},"id":"b","_ts":1,"_etag":"e","category":"shoes"}""",
                });

            var plan = Plan("SELECT o.id, p.\"$.category\" FROM orders o JOIN products p ON o.id = p.id");

            var rows = await Execute(plan);
            rows.Should().HaveCount(2);

            // The orders side is read whole, as the build side must be.
            _ordersExecutor.Executed.Should().ContainSingle();

            // The products side is asked once, for exactly the keys the orders had — padded to the
            // statement's fixed parameter count with a key it already carries.
            _productsExecutor.Executed.Should().ContainSingle();

            var statement = _productsExecutor.Executed[0];
            statement.Sql.Should().Contain($"IN ({CosmosLookupJoin.KeyPrefix}0, ");

            var keys = Keys(statement);
            keys.Should().HaveCount(CosmosLookupJoin.DefaultBatchSize);
            keys.Distinct().Should().BeEquivalentTo(new object?[] { "a", "b" });
        }

        /// <remarks>
        /// A filter on the container's side compares with a parameter, and the statement the lookup sends
        /// carries the parameter's value rather than the slot the plan left for it. The slot was sent as
        /// itself, an object no path is greater than, and the join returned nothing without saying so
        /// (#219).
        /// </remarks>
        [Fact]
        public async Task AParameterInTheLookedUpSidesFilterIsBound()
        {
            Given(
                orders: new[]
                {
                    """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","customer":"c1"}""",
                    """{"DOC":{"id":"b"},"id":"b","_ts":1,"_etag":"e","customer":"c2"}""",
                },
                products: new[]
                {
                    """{"DOC":{"id":"a"},"id":"a","_ts":5,"_etag":"e","category":"bikes"}""",
                    """{"DOC":{"id":"b"},"id":"b","_ts":5,"_etag":"e","category":"shoes"}""",
                });

            var plan = Plan("SELECT o.id, p.\"$.category\" FROM orders o JOIN (SELECT * FROM products WHERE \"_ts\" > ?) p ON o.id = p.id");

            var rows = await Execute(plan, new Dictionary<string, object> { ["?0"] = java.lang.Long.valueOf(2) });
            rows.Should().HaveCount(2);

            _productsExecutor.Executed.Should().ContainSingle();

            var statement = _productsExecutor.Executed[0];
            statement.Parameters.Should().NotContain(p => p.Value is Apache.Calcite.Cosmos.Adapter.Sql.CosmosDynamicValue, "every slot is closed before the statement is sent");
            statement.Parameters.Where(p => p.Name.StartsWith(CosmosLookupJoin.KeyPrefix) == false).Select(p => p.Value).Should().Contain(2L);
        }

        /// <remarks>
        /// Two statements, the build side's and the lookup's, and each reaches <c>Hook.QUERY_PLAN</c>
        /// once though the plan is implemented for both ways of opening it (#211).
        /// </remarks>
        [Fact]
        public void EachStatementIsAnnouncedOncePerPlan()
        {
            Given(orders: System.Array.Empty<string>(), products: System.Array.Empty<string>());

            var plan = Plan("SELECT o.id, p.\"$.category\" FROM orders o JOIN products p ON o.id = p.id");

            var announced = new List<string>();
            using (org.apache.calcite.runtime.Hook.QUERY_PLAN.addThread(new Collect(announced)))
                Implement(plan);

            announced.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            announced.Should().ContainSingle(x => x.Contains($"IN ({CosmosLookupJoin.KeyPrefix}0, "));
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
        /// The join's synchronous body is its own code rather than a wait on the awaiting one: it reads the
        /// build side with <c>Read</c> and waits only for each batch's fetch. So it is held to the same
        /// answer, and to the same single request carrying the same keys.
        /// </remarks>
        [Fact]
        public void AJoinReadSynchronouslyFetchesTheSameKeysAndPairsTheSameRows()
        {
            Given(
                orders: new[]
                {
                    """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","customer":"c1"}""",
                    """{"DOC":{"id":"b"},"id":"b","_ts":1,"_etag":"e","customer":"c2"}""",
                },
                products: new[]
                {
                    """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","category":"bikes"}""",
                    """{"DOC":{"id":"b"},"id":"b","_ts":1,"_etag":"e","category":"shoes"}""",
                });

            var rows = ExecutePulled(Plan("SELECT o.id, p.\"$.category\" FROM orders o JOIN products p ON o.id = p.id"));

            rows.Should().HaveCount(2);
            _productsExecutor.Executed.Should().ContainSingle();
            Keys(_productsExecutor.Executed[0]).Distinct().Should().BeEquivalentTo(new object?[] { "a", "b" });
        }

        /// <remarks>
        /// The join is still a join: a build row whose key no document has contributes nothing, and a
        /// document no build row asked for is not returned to begin with.
        /// </remarks>
        [Fact]
        public async Task OnlyMatchingPairsAreProduced()
        {
            Given(
                orders: new[]
                {
                    """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","customer":"c1"}""",
                    """{"DOC":{"id":"missing"},"id":"missing","_ts":1,"_etag":"e","customer":"c2"}""",
                },
                products: new[] { """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","category":"bikes"}""" });

            var rows = await Execute(Plan("SELECT o.id, p.\"$.category\" FROM orders o JOIN products p ON o.id = p.id"));

            rows.Should().ContainSingle();
        }

        /// <remarks>
        /// A build side with no rows never asks the container anything at all, which is the extreme case
        /// of the saving and the one a rendered predicate could not have reached.
        /// </remarks>
        [Fact]
        public async Task AnEmptyBuildSideAsksTheContainerNothing()
        {
            Given(orders: System.Array.Empty<string>(), products: new[] { """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","category":"bikes"}""" });

            var rows = await Execute(Plan("SELECT o.id, p.\"$.category\" FROM orders o JOIN products p ON o.id = p.id"));

            rows.Should().BeEmpty();
            _productsExecutor.Executed.Should().BeEmpty();
        }

        /// <remarks>
        /// A numeric key reaches the batch in the box Calcite holds it in — a container row is read as
        /// a <see cref="java.lang.Integer"/> — and was bound as that box, which the SDK writes as an
        /// object the service matches nothing against; nor did it equal the build side's key of
        /// another width in process. Measured on the emulator, this join returned no rows where the
        /// in-process join returned two (review probe I4). The keys go down as numbers now, and the
        /// rows fetched are paired.
        /// </remarks>
        [Fact]
        public async Task ANumericKeyIsBoundAsANumberAndPairsWhatItFetched()
        {
            Given(
                orders: System.Array.Empty<string>(),
                products: new[]
                {
                    """{"id":"1","p":120}""",
                    """{"id":"3","p":80}""",
                });

            var plan = Plan("SELECT t.\"id\" FROM (VALUES (120), (80)) AS v(k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS \"p\" FROM products AS c) AS t ON v.k = t.\"p\"");

            var text = RelOptUtil.toString(plan);
            text.Should().Contain("CosmosLookupJoin", "the container side is fetched by key: " + text);

            var rows = await Execute(plan);
            rows.Should().BeEquivalentTo(new object[] { "1", "3" });

            var keys = Keys(_productsExecutor.Executed.Should().ContainSingle().Which);
            keys.Should().AllBeOfType<double>("a key is bound as the number it is, which the SDK writes as a JSON number");
            keys.Distinct().Should().BeEquivalentTo(new object?[] { 120d, 80d });
        }

        // ── Over a traversal ──────────────────────────────────────────────────────

        /// <summary>
        /// A lookup whose container side traverses an array restricts the documents and keeps every
        /// element of each.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rule asks the logical subtree, where the traversal is a <c>Correlate</c> the binding walk
        /// knows; the node asks again at implementation, where it is a <see cref="CosmosUnnest"/>, which
        /// is a <c>SingleRel</c> and not a <c>Correlate</c>. The walk had no case for it, so the rule
        /// fired and <c>Prepare</c> threw "The lookup key can no longer be restricted." — after the
        /// planner had chosen the plan (soundness review, cause K).
        /// </para>
        /// <para>
        /// Restricting the document is sound: the <c>WHERE</c> runs after the <c>JOIN</c> and names a
        /// property of the document, so it keeps or drops every element of a document together, which
        /// is what the join on the document's key does.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ALookupOverATraversalRestrictsTheDocumentAndKeepsItsElements()
        {
            Given(
                orders: new[]
                {
                    """{"DOC":{"id":"a"},"id":"a","_ts":1,"_etag":"e","customer":"c1"}""",
                },
                products: new[]
                {
                    """{"id":"a","name":"Trail Blazer"}""",
                    """{"id":"a","name":"Trail Blazer"}""",
                });

            // The container side projects document columns only. The element binds no path where a
            // rule decides (TODO.md, section 5), so a projection naming it is not pushed and the
            // container side is then not a statement to restrict. And no column list on the alias:
            // one puts a projection between the correlate and the uncollect, which is not the
            // traversal shape, and the array is unnested in process.
            var plan = Plan("SELECT o.id, p.\"name\" FROM orders o JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.name') AS \"name\" FROM products AS c, UNNEST(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS t) p ON o.id = p.\"id\"");

            var text = RelOptUtil.toString(plan);
            text.Should().Contain("CosmosLookupJoin", "the container side is fetched by key: " + text);
            text.Should().Contain("CosmosUnnest", "and traversed at the service: " + text);

            var rows = await Execute(plan);
            rows.Should().HaveCount(2, "one row per element of the one document the key names");

            _productsExecutor.Executed.Should().ContainSingle();

            var statement = _productsExecutor.Executed[0];
            statement.Sql.Should().Contain("JOIN t0 IN c.tags");
            statement.Sql.Should().Contain($"c.id IN ({CosmosLookupJoin.KeyPrefix}0, ");
        }

        /// <summary>
        /// A container declaring <c>label</c> a string: the text key over it is the stored value.
        /// </summary>
        static readonly CosmosContainerMetadata Labelled = Products.WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse("""
            { "type": "object", "properties": { "label": { "type": "string" } } }
            """)!));

        /// <summary>
        /// A container declaring <c>data.id</c> a canonical UUID: one spelling per value.
        /// </summary>
        static readonly CosmosContainerMetadata Parks = Products.WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse("""
            { "type": "object",
              "properties": {
                "data": { "type": "object",
                  "properties": {
                    "id": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" } } } } }
            """)!));

        /// <summary>
        /// A key whose restriction depends on what the container declares, over a traversal: a text
        /// key over a declared string, and a <c>UUID</c> key over a declared form.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Cause K, a second time.</b> The binding walk learned the converted traversal, and the
        /// lookup join rule's own walks did not: the container whose facts decide a text key, and the
        /// cast a <c>UUID</c> key is restricted through, were found by walks that knew the
        /// <c>Correlate</c> and not the <see cref="CosmosUnnest"/>. So the rule fired on the logical
        /// subtree and <c>Prepare</c> threw "The lookup key can no longer be restricted." on the
        /// converted one (review of the K fix, reproduced on the stub executor). A key on
        /// <c>id</c>, which needs neither, is the case the first fix covered.
        /// </para>
        /// <para>
        /// The rows are canned in the shape the pushed statement returns; what is asserted is that the
        /// plan implements and restricts, and that the rule and the node answer the key alike.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("text", "SELECT v.k, p.\"id\" FROM (VALUES ('x'), ('y')) AS v(k) JOIN (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM products AS c, UNNEST(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS t) AS p ON v.k = p.\"Label\"", "c.label IN ")]
        [InlineData("uuid", "SELECT o.\"id\", p.\"Id\" FROM (SELECT CAST(JSON_VALUE(o.\"DOC\", '$.parkId') AS UUID) AS \"ParkId\", o.\"id\" FROM orders AS o) AS o JOIN (SELECT CAST(JSON_VALUE(c.\"DOC\", '$.data.id') AS UUID) AS \"Id\" FROM products AS c, UNNEST(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS t) AS p ON o.\"ParkId\" = p.\"Id\"", "c.data.id IN ")]
        public async Task ADeclaredKeyOverATraversalIsRestrictedAsTheRuleDecided(string kind, string sql, string restriction)
        {
            const string uuid = "0f8fad5b-d9cb-469f-a165-70867728950e";

            if (kind == "text")
                Given(
                    orders: System.Array.Empty<string>(),
                    products: new[] { """{"id":"1","Label":"x"}""", """{"id":"1","Label":"x"}""" },
                    declared: Labelled);
            else
                Given(
                    orders: new[] { """{"$f0":"o1","$f1":"0F8FAD5B-D9CB-469F-A165-70867728950E","id":"o1","ParkId":"0F8FAD5B-D9CB-469F-A165-70867728950E"}""" },
                    products: new[] { $$"""{"Id":"{{uuid}}"}""", $$"""{"Id":"{{uuid}}"}""" },
                    declared: Parks);

            var plan = Plan(sql);

            var text = RelOptUtil.toString(plan);
            text.Should().Contain("CosmosLookupJoin", "the container side is fetched by key: " + text);
            text.Should().Contain("CosmosUnnest", "and traversed at the service: " + text);

            // The rule asked the logical subtree; Prepare asks the converted one. Both must say the key
            // is restricted, and restricted the same way.
            var logical = Find<org.apache.calcite.rel.core.Join>(PlanLogical(sql))!;
            var probe = ((java.lang.Integer)logical.analyzeCondition().rightKeys.get(0)).intValue();
            Apache.Calcite.Cosmos.Adapter.Rel.Convert.CosmosLookupJoinRule.TryProbeKey(logical.getRight(), probe, out var ruleKey).Should().BeTrue("the rule restricts the logical subtree");

            var lookup = Find<CosmosLookupJoin>(plan)!;
            Apache.Calcite.Cosmos.Adapter.Rel.Convert.CosmosLookupJoinRule.TryProbeKey(lookup.getRight(), lookup.ProbeKey, out var nodeKey).Should().BeTrue("the node restricts the converted one: " + RelOptUtil.toString(lookup.getRight()));
            nodeKey.Should().Be(ruleKey);

            var rows = await Execute(plan);
            rows.Should().HaveCount(2, "one row per element of the one document the key names");

            var statement = _productsExecutor.Executed.Should().ContainSingle().Which;
            statement.Sql.Should().Contain("JOIN t0 IN c.tags");
            statement.Sql.Should().Contain(restriction + "(" + CosmosLookupJoin.KeyPrefix + "0, ");
        }

        /// <summary>
        /// Finds the first node of a type in a tree, looking through a Volcano subset to the node it
        /// was built from, or <c>null</c> where there is none.
        /// </summary>
        static T? Find<T>(RelNode node) where T : class
        {
            if (node is RelSubset subset)
                node = subset.getOriginal() ?? subset.getBest();

            if (node is T found)
                return found;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (Find<T>((RelNode)inputs.get(i)) is T inner)
                    return inner;

            return null;
        }

    }

}
