using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using Apache.Calcite.Extensions.Adapter.Cursor;

using FluentAssertions;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;
using org.apache.calcite.util;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel.Convert
{

    /// <summary>
    /// Covers whether the planner actually chooses to fetch by key rather than read the container.
    /// </summary>
    /// <remarks>
    /// The rule and the node can both be right and the feature still not happen, because a rule that is
    /// never chosen is a rule that does nothing. These are the tests that say it is chosen — and, as
    /// importantly, that it is not chosen where fetching per batch would answer a different question.
    /// </remarks>
    public class CosmosLookupJoinRuleTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });
        static readonly CosmosContainerMetadata Orders = new("orders", new[] { "/customer" });
        static readonly CosmosContainerMetadata Archive = new("archive", new[] { "/category" });

        /// <summary>
        /// A container whose schema gives <c>data.id</c> a canonical lowercase UUID form.
        /// </summary>
        static readonly CosmosContainerMetadata Parks = new CosmosContainerMetadata("parks", new[] { "/id" })
            .WithFacts(CosmosSchemaRecognition.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree("""
                { "type": "object",
                  "properties": {
                    "data": { "type": "object",
                      "properties": {
                        "id": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" } } } } }
                """)));

        CosmosTable _products = null!;
        CosmosTable _orders = null!;
        CosmosTable _archive = null!;
        CosmosTable _parks = null!;

        public CosmosLookupJoinRuleTests()
        {
            _products = new CosmosTable(Products);
            _orders = new CosmosTable(Orders);
            _archive = new CosmosTable(Archive);
            _parks = new CosmosTable(Parks);
        }

        RelNode PlanLogical(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _products);
            rootSchema.add("orders", _orders);
            rootSchema.add("archive", _archive);
            rootSchema.add("parks", _parks);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                org.apache.calcite.sql.fun.SqlStdOperatorTable.instance(),
                Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance);

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            // The CLR join rules include a merge join, which asks for the collation of its inputs and
            // fails outright where the trait is not registered. Nothing in this adapter produces a
            // collation, but the rule still has to be able to ask.
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).project();
        }

        /// <summary>
        /// Plans for the CLR convention, with every container's rules and the CLR ones.
        /// </summary>
        /// <remarks>
        /// All three containers are registered for every query, whether or not the query names them,
        /// because a rule that is only correct when its container's rules were registered last is not
        /// correct. See <see cref="EveryContainerInAQueryCanBeOnTheProbeSide"/>.
        /// </remarks>
        RelNode Plan(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_products.Convention))
                planner.addRule(rule);

            foreach (var rule in CosmosRules.GetRules(_orders.Convention))
                planner.addRule(rule);

            foreach (var rule in CosmosRules.GetRules(_archive.Convention))
                planner.addRule(rule);

            foreach (var rule in CosmosRules.GetRules(_parks.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        static string Text(RelNode rel) => RelOptUtil.toString(rel).Trim().Replace("\r\n", "\n");

        static bool Contains<T>(RelNode rel) where T : class, RelNode => Find<T>(rel) is not null;

        static T? Find<T>(RelNode rel) where T : class
        {
            if (rel is T found)
                return found;

            var inputs = rel.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (Find<T>((RelNode)inputs.get(i)) is T inner)
                    return inner;

            return null;
        }

        static List<T> FindAll<T>(RelNode rel) where T : class
        {
            var found = new List<T>();

            void Walk(RelNode node)
            {
                if (node is T match)
                    found.Add(match);

                var inputs = node.getInputs();
                for (var i = 0; i < inputs.size(); i++)
                    Walk((RelNode)inputs.get(i));
            }

            Walk(rel);
            return found;
        }

        // ── Chosen ────────────────────────────────────────────────────────────────

        /// <remarks>
        /// The whole feature in one query: two containers joined on a key both address, where without
        /// this the plan reads every document of both.
        /// </remarks>
        [Fact]
        public void AnEquiJoinOnAnAddressableKeyBecomesALookup()
        {
            var plan = Plan("SELECT * FROM orders o JOIN products p ON o.id = p.id");

            var lookup = Find<CosmosLookupJoin>(plan);
            lookup.Should().NotBeNull("the plan should fetch by key rather than read the container:\n" + Text(plan));

            // The probe side is the container, and the build side is not — a lookup whose sides were
            // the other way round would plan, run, and read the container whole.
            lookup!.getRight().getConvention().Should().BeSameAs(_products.Convention);
        }

        /// <summary>
        /// Which container is on the probe side must not decide whether the lookup happens.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rule was originally created once per convention, and checked that the probe side's
        /// container was the one its instance had been given. That check cannot work, and the reason is
        /// the one <see cref="Apache.Calcite.Cosmos.Adapter.Rel.Convert.CosmosTableModifyRule"/> carries:
        /// a converter rule's description is derived from the traits it converts between, this one
        /// converts <c>NONE</c> to <c>CLR_ASYNC_ENUMERABLE</c>, and neither names a container — so every
        /// instance is <c>CosmosLookupJoinRule(in:NONE,out:CLR_ASYNC_ENUMERABLE)</c> and a planner cannot
        /// tell them apart.
        /// </para>
        /// <para>
        /// Two containers did not show it, which is why this test is a join of three. Measured:
        /// <c>addRule</c> does reject the later duplicates, but rejection does not decide matching — in
        /// every measured plan the predicate that ran belonged to an instance <c>addRule</c> had
        /// rejected, the one most recently constructed at the rule's <em>first</em> firing, and that
        /// instance stayed bound for the rest of the run. <see cref="CosmosConvention.register"/>
        /// constructs a fresh rule set as each new convention joins the planner, and Calcite registers a
        /// join's inputs left before right, so at a lone join's first match the freshest instance is its
        /// own probe side's. Two containers therefore worked in either orientation, by an accident of
        /// registration order that a third container ends: the instance bound at the first join judged
        /// the second join too, declined <c>archive</c>, and the plan read the whole of it through a
        /// hash join.
        /// </para>
        /// <para>
        /// So both halves are asserted — the orientations that always passed, and the third container
        /// that did not.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryContainerInAQueryCanBeOnTheProbeSide()
        {
            var onProducts = Find<CosmosLookupJoin>(Plan("SELECT * FROM orders o JOIN products p ON o.id = p.id"));
            onProducts.Should().NotBeNull();
            onProducts!.getRight().getConvention().Should().BeSameAs(_products.Convention);

            var onOrders = Find<CosmosLookupJoin>(Plan("SELECT * FROM products p JOIN orders o ON p.id = o.id"));
            onOrders.Should().NotBeNull();
            onOrders!.getRight().getConvention().Should().BeSameAs(_orders.Convention);

            var plan = Plan("SELECT * FROM products p JOIN orders o ON p.id = o.id JOIN archive a ON p.id = a.id");
            var lookups = FindAll<CosmosLookupJoin>(plan);

            lookups.Should().HaveCount(2, "each join has a container on its probe side:\n" + Text(plan));
            lookups.Should().Contain(j => ReferenceEquals(j.getRight().getConvention(), _orders.Convention));
            lookups.Should().Contain(j => ReferenceEquals(j.getRight().getConvention(), _archive.Convention));
        }

        /// <summary>
        /// A left join keeps a build row nothing matched, with nulls for the container's columns — which a
        /// batch answers as well as it answers an inner join's drop. #193.
        /// </summary>
        [Fact]
        public void ALeftJoinIsALookup()
        {
            var plan = Plan("SELECT * FROM orders o LEFT JOIN products p ON o.id = p.id");

            var lookup = Find<CosmosLookupJoin>(plan);
            lookup.Should().NotBeNull("the products are fetched by the orders' keys:\n" + Text(plan));
            lookup!.getJoinType().Should().Be(org.apache.calcite.rel.core.JoinRelType.LEFT);
            lookup.getRight().getConvention().Should().BeSameAs(_products.Convention);
        }

        const string ParkIds = """
            SELECT o."id", p."Id"
            FROM (SELECT CAST(JSON_VALUE(o."DOC", '$.parkId') AS UUID) AS "ParkId", o."id" FROM orders AS o) AS o
            LEFT JOIN (SELECT CAST(JSON_VALUE(c."DOC", '$.data.id') AS UUID) AS "Id" FROM {0} AS c) AS p
              ON o."ParkId" = p."Id"
            """;

        /// <summary>
        /// A <c>UUID</c> key binds to no path — the column carries a cast — but where the container holds
        /// one spelling per value at the path it casts, the key is restricted there, in that spelling. #193.
        /// </summary>
        [Fact]
        public void AUuidKeyOverACanonicalFormIsALookup()
        {
            var plan = Plan(string.Format(ParkIds, "parks"));

            var lookup = Find<CosmosLookupJoin>(plan);
            lookup.Should().NotBeNull("the parks are fetched by the orders' keys:\n" + Text(plan));
            lookup!.getRight().getConvention().Should().BeSameAs(_parks.Convention);
        }

        /// <summary>
        /// Without the form, <c>ABC…</c> and <c>abc…</c> cast to one key, and no spelling of the key names
        /// both.
        /// </summary>
        [Fact]
        public void AUuidKeyWithoutACanonicalFormIsNotALookup()
        {
            var plan = Plan(string.Format(ParkIds, "archive"));

            FindAll<CosmosLookupJoin>(plan).Should().NotContain(j => ReferenceEquals(j.getRight().getConvention(), _archive.Convention),
                "a key with more than one spelling cannot be written as one:\n" + Text(plan));
        }

        // ── Not chosen, and each for a reason ─────────────────────────────────────

        /// <remarks>
        /// <c>TOP 5</c> of the container is not <c>TOP 5</c> of each batch. Running it per batch would
        /// return rows the plan never asked for, so the restriction is refused and both sides are read.
        /// </remarks>
        [Fact]
        public void AJoinAgainstALimitedSubqueryIsNotALookup()
        {
            var plan = Plan("SELECT * FROM orders o JOIN (SELECT * FROM products FETCH FIRST 5 ROWS ONLY) p ON o.id = p.id");

            Contains<CosmosLookupJoin>(plan).Should().BeFalse("a limit is defined over the container, not over a batch:\n" + Text(plan));
        }

        /// <remarks>
        /// A right or full join has to preserve the container's rows with no match, and a fetch by the
        /// other side's keys never reads them.
        /// </remarks>
        [Theory]
        [InlineData("RIGHT")]
        [InlineData("FULL")]
        public void AJoinPreservingTheContainerIsNotALookup(string join)
        {
            var plan = Plan($"SELECT * FROM orders o {join} JOIN products p ON o.id = p.id");

            FindAll<CosmosLookupJoin>(plan).Should().NotContain(j => ReferenceEquals(j.getRight().getConvention(), _products.Convention),
                "the products nothing matched would never be read:\n" + Text(plan));
        }

        /// <remarks>
        /// A partition key column is typed <c>ANY</c>, which says nothing about what would be bound as a
        /// parameter or compared once fetched.
        /// </remarks>
        [Fact]
        public void AJoinOnAnUntypedColumnIsNotALookup()
        {
            var plan = Plan("SELECT * FROM orders o JOIN products p ON o.\"$.customer\" = p.\"$.category\"");

            Contains<CosmosLookupJoin>(plan).Should().BeFalse("an ANY key cannot be bound as a parameter:\n" + Text(plan));
        }

        /// <remarks>
        /// A non-equality is not a key. There is nothing to put in an <c>IN</c>.
        /// </remarks>
        [Fact]
        public void ANonEquiJoinIsNotALookup()
        {
            var plan = Plan("SELECT * FROM orders o JOIN products p ON o.id < p.id");

            Contains<CosmosLookupJoin>(plan).Should().BeFalse("there is no key to fetch by:\n" + Text(plan));
        }

    }

}
