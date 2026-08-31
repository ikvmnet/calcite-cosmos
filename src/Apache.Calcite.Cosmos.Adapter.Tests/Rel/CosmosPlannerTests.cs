using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Drives the Volcano planner over real SQL with the Cosmos rule set registered, asserting that
    /// the planner actually selects the Cosmos nodes.
    /// </summary>
    /// <remarks>
    /// The rule predicates and each node's rendering are covered elsewhere. What is checked here is
    /// the step between them: that the planner reaches a plan wholly in the Cosmos convention, and
    /// that the plan renders to the expected statement.
    /// </remarks>
    [TestClass]
    public class CosmosPlannerTests
    {

        /// <remarks>
        /// The full text and vector paths are declared because the functions over them are gated on
        /// the declaration: a container that says nothing about a path is one whose full text
        /// predicate the service refuses, so the rules decline it. Every statement here that names
        /// one names a declared path, and <see cref="AFullTextPredicateOverAnUndeclaredPathIsNotPushedDown"/>
        /// is the other half.
        /// </remarks>
        static readonly CosmosContainerMetadata Products = new(
            "products",
            new[] { "/category" },
            new[]
            {
                new CosmosCompositeIndex(new[]
                {
                    new CosmosCompositeIndexPath("/id", false),
                    new CosmosCompositeIndexPath("/_ts", false),
                }),
            },
            fullTextPaths: new[] { "/name", "/tags" },
            vectorPaths: new[] { "/a" });

        CosmosTable _table = null!;

        [TestInitialize]
        public void Initialize()
        {
            _table = new CosmosTable(Products);
        }

        RelNode PlanLogical(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            // Chained so that a query can name the adapter's own functions. Calcite's standard table has
            // nothing to resolve IS_DEFINED or FULLTEXTCONTAINS to, and this is the seam a caller wires
            // the same way.
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(), Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance);

            var validator = SqlValidatorUtil.newValidator(
                operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            // project(), not rel. Ordering by an expression outside the select list makes
            // SqlToRelConverter carry it as an extra column and record in the RelRoot that it is not
            // output; project() is what applies that, and is what a real consumer uses. Taking rel
            // leaves the column at the root, which for a scoring function is the difference between a
            // plan that can be implemented and one that cannot — Cosmos will not project a score.
            // It is a no-op wherever the mapping is trivial, which is every other query here.
            return converter.convertQuery(validator.validate(parsed), false, true).project();
        }

        /// <summary>
        /// Plans a statement and asks the planner for the best plan wholly in the Cosmos convention.
        /// </summary>
        RelNode PlanToCosmos(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(_table.Convention).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// Renders a planned tree to the statement it would execute.
        /// </summary>
        string Render(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), Products);
            implementor.Visit(rel);
            return implementor.Build().Sql;
        }

        static string Plan(RelNode rel) => RelOptUtil.toString(rel).Trim().Replace("\r\n", "\n");

        /// <summary>
        /// Renders a planned tree to the full query, including anything recovered about execution.
        /// </summary>
        CosmosQuery Query(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), Products);
            implementor.Visit(rel);
            return implementor.Build();
        }

        // ── Through a view's cast to text ─────────────────────────────────────────

        /// <summary>
        /// The whole point of the exercise, end to end: a view exposing the partition key as text runs
        /// against one partition rather than every one.
        /// </summary>
        /// <remarks>
        /// The predicate is in the statement as well. Routing chooses which partitions are visited and
        /// filters nothing, so the rows are decided by the same comparison either way — which is why
        /// this is a cost change and not a behaviour change.
        /// </remarks>
        [TestMethod]
        public void ACastToTextOverThePartitionKeyConfinesExecution()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(c.\"category\" AS VARCHAR) = 'bikes'"));

            query.PartitionKeyValues.Should().Equal("bikes");
            query.Sql.Should().Contain("WHERE (c.category = @p0)");
        }

        [TestMethod]
        public void ACastToTextOverAnOrdinaryPathPushesAsAComparison()
        {
            Render(PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(c.\"_MAP\"['label'] AS VARCHAR) = 'bikes'"))
                .Should().Contain("WHERE (c.label = @p0)");
        }

        /// <remarks>
        /// Text a stored number renders as, so a document Calcite matches could be in another partition
        /// — and the comparison itself would select differently at the service. Neither the predicate
        /// nor the routing is taken, which is the container read whole, exactly as before.
        /// </remarks>
        [TestMethod]
        public void ACastAgainstTextANumberRendersAsIsNotTaken()
        {
            // No plan wholly in the convention exists, which is this harness's way of saying the filter
            // declined: it stays above, and Calcite applies it to the whole container.
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(c.\"category\" AS VARCHAR) = '30'");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        /// <remarks>
        /// A cast to a number converts, and no Cosmos comparison reproduces that — so it is declined
        /// and Calcite answers it over the whole container. Slower, and the rows SQL says.
        /// </remarks>
        [TestMethod]
        public void ACastToANumberIsNotTaken()
        {
            var plan = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) = 30");

            plan.Should().Throw<java.lang.RuntimeException>();
        }

        // ── A bound on what a numeric cast reads ──────────────────────────────────

        /// <summary>
        /// A comparison through a cast to a number has no Cosmos form, and still says something the
        /// service can apply.
        /// </summary>
        /// <remarks>
        /// Converting a number to a number moves it by less than one, so a document whose converted
        /// value is 30 has a raw value strictly between 29 and 31. The predicate itself stays above and
        /// decides the rows; this only decides which documents cross the wire.
        /// </remarks>
        [TestMethod]
        public void AComparisonThroughANumericCastPushesABoundOnTheRawValue()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) = 30")));

            query.Sql.Should().Contain("IS_DEFINED(c.price)");
            query.Sql.Should().Contain("(NOT IS_NUMBER(c.price))");
            query.Sql.Should().Contain("(c.price > @p0)");
            query.Sql.Should().Contain("(c.price < @p1)");

            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("29", "31");
        }

        /// <summary>
        /// The type test lets non-numbers through rather than filtering to numbers.
        /// </summary>
        /// <remarks>
        /// This is the whole soundness of it, and the direction is the opposite of the obvious one.
        /// Calcite's cast converts a stored <em>string</em> too — measured, <c>= 30</c> keeps a document
        /// storing <c>"30"</c> — so a filter that kept only numbers would lose it. Anything that is not
        /// a number passes untouched and is decided above.
        /// </remarks>
        [TestMethod]
        public void TheTypeTestAdmitsNonNumbersRatherThanExcludingThem()
        {
            var sql = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) = 30"))).Sql;

            sql.Should().Contain("(NOT IS_NUMBER(c.price)) OR");
            sql.Should().NotContain("IS_NUMBER(c.price) AND");
        }

        [TestMethod]
        public void AnInequalityPushesTheBoundOnOneSideOnly()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) > 10")));

            query.Sql.Should().Contain("(c.price > @p0)");
            query.Sql.Should().NotContain("@p1");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("9");
        }

        /// <remarks>
        /// The bound is on the side the cast is, so a comparison written the other way round is the
        /// mirrored operator over the same bound.
        /// </remarks>
        [TestMethod]
        public void TheBoundIsTheSameWithTheOperandsTheOtherWayRound()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE 10 < CAST(c.\"_MAP\"['price'] AS INTEGER)")));

            query.Sql.Should().Contain("(c.price > @p0)");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("9");
        }

        /// <remarks>
        /// Calcite widens a literal to the type it is compared against, so the bound arrives wrapped in
        /// a cast of its own. A cast of a constant to a number is that constant.
        /// </remarks>
        [TestMethod]
        public void ABoundWrappedInItsOwnCastIsStillRead()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(c.\"_MAP\"['price'] AS DOUBLE) <= 30.5")));

            query.Sql.Should().Contain("(c.price < @p0)");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("31.5");
        }

        /// <summary>
        /// At the limit the conversion saturates to, the bound on that side is not stated.
        /// </summary>
        /// <remarks>
        /// A stored value far past what the target can hold converts to the limit — measured,
        /// <c>toInt(1e30)</c> is <c>2147483647</c> — so <c>= 2147483647</c> is true of a document
        /// storing <c>1e30</c>, and a window around the limit would exclude exactly that document. It
        /// did, and the differential corpus caught it as a lost row. Only equality is affected: the
        /// inequalities already admit everything past the limit.
        /// </remarks>
        [TestMethod]
        public void AComparisonAtTheSaturationLimitDoesNotBoundThatSide()
        {
            var query = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) = 2147483647")));

            query.Sql.Should().Contain("(c.price > @p0)");
            query.Sql.Should().NotContain("@p1");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("2147483646");
        }

        /// <summary>
        /// The targets whose conversion does not stay within one of the stored value state no bound.
        /// </summary>
        /// <remarks>
        /// Each was measured against Calcite's own runtime, and measuring is what ruled them out.
        /// <c>SMALLINT</c> and <c>TINYINT</c> wrap rather than saturate — <c>toShort(1e30)</c> is
        /// <c>-1</c> and <c>toByte(1e30)</c> is <c>255</c>, which bear no relation to the stored value.
        /// <c>FLOAT</c> and <c>REAL</c> round to float precision, and <c>float(1e30)</c> is 1.5e22 away
        /// from <c>1e30</c>. <c>DECIMAL</c> raises where the value does not fit its declared precision,
        /// and excluding the document would turn a failing query into a passing one.
        /// </remarks>
        [TestMethod]
        public void ATargetThatWrapsOrRoundsOrRaisesStatesNoBound()
        {
            foreach (var type in new[] { "SMALLINT", "TINYINT", "REAL", "FLOAT", "DECIMAL(10, 2)" })
            {
                var sql = Query(FindCosmos(PlanToAsync(
                    $"SELECT * FROM products AS c WHERE CAST(c.\"_MAP\"['price'] AS {type}) = 30"))).Sql;

                sql.Should().Contain("IS_DEFINED(c.price)", "a comparison still implies the path is defined");
                sql.Should().NotContain("IS_NUMBER", "no bound is sound for {0}", type);
            }
        }

        /// <remarks>
        /// Nothing is known about where converting to a date lands, so there is no bound to state — and
        /// the definedness the comparison implies is still worth pushing.
        /// </remarks>
        [TestMethod]
        public void ACastWithNoBoundToStateStillPushesDefinedness()
        {
            var sql = Query(FindCosmos(PlanToAsync(
                "SELECT * FROM products AS c WHERE CAST(c.\"_MAP\"['when'] AS DATE) = DATE '2020-01-01'"))).Sql;

            sql.Should().Contain("IS_DEFINED(c.when)");
            sql.Should().NotContain("IS_NUMBER");
        }

        // ── Partition key recovery ────────────────────────────────────────────────

        /// <remarks>
        /// Naming the partition key confines execution to one physical partition rather than
        /// fanning out across every one. It changes nothing about the statement itself.
        /// </remarks>
        [TestMethod]
        public void PredicateOnThePartitionKeyIsRecovered()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes'"));

            query.PartitionKeyValues.Should().Equal("bikes");
            query.Sql.Should().Contain("WHERE (c.category = @p0)");
        }

        /// <remarks>
        /// A single trailing wildcard is a prefix match, which the index serves as
        /// <c>STARTSWITH</c> where <c>LIKE</c> is a scan.
        /// </remarks>
        [TestMethod]
        public void PrefixLikeIsPushedAsStartsWith()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" LIKE 'bi%'"));

            query.Sql.Should().Contain("STARTSWITH(c.category, @p0)");
        }

        /// <remarks>
        /// The batch counterpart of the point read: <c>pk = … AND id IN (…)</c> is a set of
        /// documents, which <c>ReadManyItemsAsync</c> answers charged as point reads. The <c>IN</c>
        /// arrives from the planner as a <c>SEARCH</c>, which is why this is asserted from real SQL
        /// rather than a hand-built predicate.
        /// </remarks>
        [TestMethod]
        public void ASetOfIdsWithThePartitionKeyIsRecoveredAsABatchOfPointReads()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND c.\"id\" IN ('a', 'b')"));

            query.PointReadIds.Should().Equal("a", "b");
            query.PartitionKeyValues.Should().Equal("bikes");
            query.PartitionKeyIsComplete.Should().BeTrue();
        }

        /// <remarks>
        /// The reads are blind, so anything beyond the pinned predicate withdraws them and the
        /// statement runs as the query it already is.
        /// </remarks>
        [TestMethod]
        public void AResidualPredicateWithdrawsTheBatchOfPointReads()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND c.\"id\" IN ('a', 'b') AND c.\"_ts\" > 5"));

            query.PointReadIds.Should().BeNull();
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        [TestMethod]
        public void PredicateOnANonPartitionKeyRecoversNothing()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'")).PartitionKeyValues.Should().BeNull();
        }

        [TestMethod]
        public void QueryWithoutAPredicateRecoversNothing()
        {
            Query(PlanToCosmos("SELECT * FROM products")).PartitionKeyValues.Should().BeNull();
        }

        // ── The planner selects Cosmos nodes ──────────────────────────────────────

        [TestMethod]
        public void ScanAlonePlansInTheCosmosConvention()
        {
            var best = PlanToCosmos("SELECT * FROM products");

            Plan(best).Should().Contain("CosmosTableScan");
            best.getConvention().Should().BeSameAs(_table.Convention);
        }

        [TestMethod]
        public void FilterIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'");

            Plan(best).Should().Contain("CosmosFilter");
            Render(best).Should().Contain("WHERE (c.id = @p0)");
        }

        [TestMethod]
        public void ProjectIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c");

            Plan(best).Should().Contain("CosmosProject");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c");
        }

        [TestMethod]
        public void FilterAndProjectPlanTogether()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" = 'bikes'");
            var sql = Render(best);

            sql.Should().StartWith("SELECT VALUE { \"id\": c.id } FROM products c WHERE ");
            sql.Should().Contain("c.category = @p0");
        }

        /// <remarks>
        /// The container declares a composite index over (/id, /_ts), so this multi-key sort is
        /// legal and the rule may fire.
        /// </remarks>
        [TestMethod]
        public void SortIsSelectedWhenTheCompositeIndexPermitsIt()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c ORDER BY c.\"id\", c.\"_ts\"");

            Plan(best).Should().Contain("CosmosSort");
            Render(best).Should().Contain("ORDER BY c.id ASC, c._ts ASC");
        }

        /// <summary>
        /// The end of the chain: an array traversal planned from SQL, selected by the planner, and
        /// rendered to Cosmos SQL.
        /// </summary>
        [TestMethod]
        public void UnnestIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(c.\"_MAP\"['tags']) AS t");

            Plan(best).Should().Contain("CosmosUnnest");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c JOIN t0 IN c.tags");
        }

        /// <summary>
        /// The traversal a host actually plans: the array reached through a projection below the
        /// correlate rather than off the scan.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The test above plans the array expression straight off the scan, which is what a bare
        /// Volcano planner produces and not what a host does. Calcite's own rule set hoists the
        /// traversed array into a projection on the correlate's left, and the traversal is then above
        /// a projection — which the statement can express, because Cosmos evaluates <c>SELECT</c>
        /// after <c>JOIN</c>, as long as the element is added to the object being constructed.
        /// </para>
        /// <para>
        /// A sub-select is how that shape is reached here without borrowing the host's rules. It is
        /// the same shape and it failed the same way: while the traversal refused to sit above a
        /// projection, no consumer could unnest an array at all, whatever the SQL said. See
        /// ikvmnet/calcite-cosmos#36.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void UnnestOverAHoistedArrayCarriesTheElement()
        {
            var best = PlanToAsync("SELECT c.\"id\", CAST(t AS VARCHAR) FROM (SELECT p.\"id\", p.\"_MAP\" FROM products AS p) AS c, UNNEST(c.\"_MAP\"['tags']) AS t");

            Plan(best).Should().Contain("CosmosUnnest");

            // The element is the last property, and it is the whole point: without it the statement
            // returns the projected object the traversal was written under, one column short.
            Render(FindCosmos(best)).Should().Be("SELECT VALUE { \"id\": c.id, \"_MAP\": c, \"EXPR$0\": t0 } FROM products c JOIN t0 IN c.tags");
        }

        /// <summary>
        /// A predicate over the traversed element is answered by the service rather than by the plan.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cosmos evaluates <c>WHERE</c> after <c>JOIN</c>, so a predicate over the element is an
        /// ordinary <c>WHERE</c> over the traversal alias. Reaching it takes two steps: a query writes
        /// the predicate above the correlate, <c>FILTER_CORRELATE</c> pushes it inside, and
        /// <c>CosmosUnnestRule</c> reads it back out as a <c>CosmosFilter</c> over the traversal.
        /// </para>
        /// <para>
        /// Without them the predicate stayed outside and every element of every document crossed the
        /// wire to be discarded here. See ikvmnet/calcite-cosmos#36.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void APredicateOverTheTraversedElementIsPushedAsAWhere()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(c.\"_MAP\"['tags']) AS t WHERE CAST(t AS VARCHAR) = 'steel'");

            Plan(best).Should().Contain("CosmosUnnest");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c JOIN t0 IN c.tags WHERE (t0 = @p0)");
        }

        /// <summary>
        /// An element predicate does not pin the partition key, however much it looks like one.
        /// </summary>
        /// <remarks>
        /// <c>products</c> is partitioned on <c>/category</c> and <c>t0</c> is not <c>c.category</c>,
        /// whatever the value compared to it. Routing on it would visit one partition and miss every
        /// document holding the tag elsewhere — a wrong answer rather than a slow one, and silent. The
        /// extractor refuses a path rooted at a traversal alias; this is that refusal reached from SQL.
        /// </remarks>
        [TestMethod]
        public void APredicateOverTheElementDoesNotPinThePartitionKey()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(c.\"_MAP\"['tags']) AS t WHERE CAST(t AS VARCHAR) = 'bikes'"));

            query.PartitionKeyValues.Should().BeNull();
            query.Sql.Should().Contain("WHERE (t0 = @p0)");
        }

        /// <summary>
        /// A predicate the service cannot express leaves the traversal pushed and stays above it.
        /// </summary>
        /// <remarks>
        /// The control for the two rules above, and the reason they are safe to register. A
        /// transformation adds an equivalence rather than replacing one, so the plan with the predicate
        /// still above the correlate survives — and where the predicate does not render, that is the
        /// plan the planner is left with. The traversal is not lost with it.
        /// </remarks>
        [TestMethod]
        public void AnUntranslatablePredicateOverTheElementLeavesTheTraversalPushed()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\" FROM products AS c, UNNEST(c.\"_MAP\"['tags']) AS t WHERE INITCAP(CAST(t AS VARCHAR)) = 'Steel'"));

            plan.Should().Contain("CosmosUnnest");
            plan.Should().Contain("INITCAP");
            plan.Should().NotContain("ClrAsyncEnumerableUncollect");
        }

        // ── Aggregation ───────────────────────────────────────────────────────────

        [TestMethod]
        public void GroupByWithCountIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY c.\"category\"");

            Plan(best).Should().Contain("CosmosAggregate");
            Render(best).Should().Contain("GROUP BY (IS_DEFINED(c.category) ? c.category : null)");
            Render(best).Should().Contain("COUNT(1)");
        }

        /// <remarks>
        /// <c>_ts</c> is service-guaranteed and therefore non-nullable, so Cosmos and SQL agree on
        /// the aggregate's value.
        /// </remarks>
        [TestMethod]
        public void AggregateOverANonNullableColumnIsSelected()
        {
            var best = PlanToCosmos("SELECT c.\"category\", MAX(c.\"_ts\") FROM products AS c GROUP BY c.\"category\"");

            Plan(best).Should().Contain("CosmosAggregate");
            Render(best).Should().Contain("MAX(c._ts)");
        }

        /// <remarks>
        /// The key is grouped and projected as the value the property has when it is there and as
        /// <c>null</c> when it is not, because SQL has one null where the service keeps an absent
        /// property apart from a present-and-null one. See <c>CosmosAggregate.GroupingKey</c>. The
        /// <c>WHERE</c> beside it still names the plain path, and so does the partition key it pins:
        /// nothing about a predicate needs the two brought together.
        /// </remarks>
        [TestMethod]
        public void GroupByRendersTheWholeStatement()
        {
            var sql = Render(PlanToCosmos("SELECT c.\"category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"category\""));

            // Flat rather than an object constructor: Cosmos rejects an aggregate inside one.
            sql.Should().Be("SELECT (IS_DEFINED(c.category) ? c.category : null) AS \"category\", COUNT(1) AS \"n\" FROM products c GROUP BY (IS_DEFINED(c.category) ? c.category : null)");
        }

        /// <remarks>
        /// A <c>HAVING</c> on a grouping key is a filter above the aggregate, which cannot bind —
        /// aggregate output has no document paths. <c>FILTER_AGGREGATE_TRANSPOSE</c> moves it below,
        /// where it is an ordinary <c>WHERE</c> the service applies before grouping, and where a
        /// predicate on the partition key confines execution the way it does anywhere else.
        /// </remarks>
        [TestMethod]
        public void HavingOnAGroupingKeyIsPushedAsAWhere()
        {
            var query = Query(PlanToCosmos(
                "SELECT c.\"category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"category\" HAVING c.\"category\" = 'bikes'"));

            query.Sql.Should().Be("SELECT (IS_DEFINED(c.category) ? c.category : null) AS \"category\", COUNT(1) AS \"n\" FROM products c WHERE (c.category = @p0) GROUP BY (IS_DEFINED(c.category) ? c.category : null)");
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// <para>
        /// A call-less aggregate is a <c>DISTINCT</c>, and emitting it as one is what lets the sort
        /// join it: <c>GROUP BY</c> and <c>ORDER BY</c> cannot appear together, <c>DISTINCT</c> and
        /// <c>ORDER BY</c> can — measured against a real account, not only the emulator.
        /// </para>
        /// <para>
        /// Over <c>_ts</c> rather than a user path, and that is the null-placement rule rather than
        /// anything to do with the distinct: Calcite's ascending means nulls last and Cosmos sorts
        /// them first, so a nullable key is refused whatever sits below it. Every promoted user
        /// column is nullable today, so this combination reaches only the service's own columns
        /// until a column can be declared non-nullable.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void DistinctAndOrderByPushAsOneStatement()
        {
            var sql = Render(PlanToCosmos("SELECT DISTINCT c.\"_ts\" FROM products AS c ORDER BY c.\"_ts\""));

            sql.Should().Be("SELECT DISTINCT VALUE { \"_ts\": c._ts } FROM products c ORDER BY c._ts ASC");
        }

        // ── The planner declines rather than guessing ─────────────────────────────

        /// <remarks>
        /// Measured on the emulator, Cosmos <c>COUNT(x)</c> counts a JSON null where SQL excludes
        /// it, so the two disagree on any nullable column.
        /// </remarks>
        [TestMethod]
        public void CountOfANullableColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT COUNT(c.\"category\") FROM products AS c");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// Not by any rule here: Calcite rewrites <c>COUNT(x)</c> over a non-nullable column to
        /// <c>COUNT(*)</c> before conversion, so what arrives is the argumentless form that is
        /// always safe. Pinned because it is why <see cref="CosmosAggregate.CanImplement"/> needs
        /// no non-nullable <c>COUNT(x)</c> case — that branch was probed and found dead.
        /// </remarks>
        [TestMethod]
        public void CountOfANonNullableColumnIsPushedDown()
        {
            var sql = Render(PlanToCosmos("SELECT COUNT(c.\"_ts\") AS n FROM products AS c"));

            sql.Should().Be("SELECT COUNT(1) AS \"n\" FROM products c");
        }

        /// <remarks>
        /// <c>SUM</c> over a set containing a JSON null returns undefined rather than ignoring it.
        /// </remarks>
        [TestMethod]
        public void SumOfANullableColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT SUM(c.\"category\") FROM products AS c");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// Not pushed <em>whole</em>: the registered expansion rewrites this into an aggregate over
        /// an aggregate whose inner <c>GROUP BY</c> is pushable, but the finishing count has no
        /// Cosmos rendering, and a planner asked for a wholly-Cosmos plan cannot produce one. The
        /// partial form is covered in <see cref="CosmosPartialAggregatePlannerTests"/>, where there
        /// is somewhere outside the convention for the count to live.
        /// </remarks>
        [TestMethod]
        public void DistinctAggregateIsNotPushedDownWhole()
        {
            var act = () => PlanToCosmos("SELECT COUNT(DISTINCT c.\"id\") FROM products AS c");

            act.Should().Throw<Exception>();
        }


        /// <remarks>
        /// UPPER has no Cosmos equivalent, so no rule converts the filter. With only Cosmos rules
        /// registered the planner cannot reach a plan at all, which is the correct outcome: in a
        /// real planning context the operator is left to Calcite's own runtime.
        /// </remarks>
        [TestMethod]
        public void UntranslatableFilterIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT * FROM products AS c WHERE INITCAP(c.\"id\") = 'X'");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// A multi-key sort with no matching composite index is rejected by the service, so the
        /// rule must not fire.
        /// </remarks>
        [TestMethod]
        public void SortWithoutAMatchingCompositeIndexIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT * FROM products AS c ORDER BY c.\"id\", c.\"category\"");

            act.Should().Throw<Exception>();
        }

        // ── A null placement the query itself has settled ─────────────────────────
        //
        // `category` is nullable and its placement conflicts with Cosmos in both directions, so
        // ordering by it is refused. A predicate that removes the nulls settles the conflict: with
        // none left there is nothing to place wrongly, whichever way each side would have placed
        // one. The predicate reaches the node through `RelMdPredicates`, so this is the planner
        // deciding rather than the renderer discovering.

        /// <remarks>
        /// The pair that carries the change. Both directions, both of Calcite's default placements,
        /// and both refused without the predicate — see
        /// <see cref="ANullableKeyIsStillRefusedWithoutTheGuarantee"/>.
        /// </remarks>
        [TestMethod]
        public void AnIsNotNullPredicateMakesANullableColumnASortKey()
        {
            Render(PlanToCosmos("SELECT c.\"id\", c.\"category\" FROM products AS c WHERE c.\"category\" IS NOT NULL ORDER BY c.\"category\""))
                .Should().Contain("ORDER BY c.category ASC");

            Render(PlanToCosmos("SELECT c.\"id\", c.\"category\" FROM products AS c WHERE c.\"category\" IS NOT NULL ORDER BY c.\"category\" DESC"))
                .Should().Contain("ORDER BY c.category DESC");
        }

        /// <summary>
        /// The predicate and the ordering leave as one statement, which is what makes the guarantee
        /// hold at the service rather than only in the plan.
        /// </summary>
        [TestMethod]
        public void ThePredicateAndTheOrderingPushAsOneStatement()
        {
            Render(PlanToCosmos("SELECT c.\"id\", c.\"category\" FROM products AS c WHERE c.\"category\" IS NOT NULL ORDER BY c.\"category\""))
                .Should().Be("SELECT VALUE { \"id\": c.id, \"category\": c.category } FROM products c WHERE (IS_DEFINED(c.category) AND NOT IS_NULL(c.category)) ORDER BY c.category ASC");
        }

        /// <summary>
        /// The row limit becomes pushable at the same moment the ordering does, a limit being sound
        /// only once the ordering above it is.
        /// </summary>
        [TestMethod]
        public void TheRowLimitRidesAlongWithTheOrdering()
        {
            Render(PlanToCosmos("SELECT c.\"id\", c.\"category\" FROM products AS c WHERE c.\"category\" IS NOT NULL ORDER BY c.\"category\" FETCH NEXT 10 ROWS ONLY"))
                .Should().Contain("ORDER BY c.category ASC OFFSET 0 LIMIT 10");
        }

        /// <remarks>
        /// The control. Without the predicate the same statement is refused, which is what says the
        /// tests above depend on the predicate rather than on anything else that changed.
        /// </remarks>
        [TestMethod]
        public void ANullableKeyIsStillRefusedWithoutTheGuarantee()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\", c.\"category\" FROM products AS c ORDER BY c.\"category\"");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// The guarantee has to be about the sort key. A predicate over a different column removes
        /// no null from the one being ordered by.
        /// </remarks>
        [TestMethod]
        public void APredicateOverAnotherColumnDoesNotUnlockTheSort()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\", c.\"category\" FROM products AS c WHERE c.\"id\" IS NOT NULL ORDER BY c.\"category\"");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// A path inside the map column is not reached, and the reason is structural: it projects as
        /// <c>ITEM($0, 'name')</c> rather than as a reference, and <c>RelMdPredicates</c> carries a
        /// predicate through a projection only where the projection is a reference. Recorded as the
        /// boundary of what this reaches — see <c>TODO.md</c> section 6, where the fix is a column.
        /// </remarks>
        [TestMethod]
        public void APathInsideTheMapColumnIsNotReached()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\", c.\"_MAP\"['name'] FROM products AS c WHERE c.\"_MAP\"['name'] IS NOT NULL ORDER BY c.\"_MAP\"['name']");

            act.Should().Throw<Exception>();
        }

        // ── Binding through a projection ────────────────────────

        /// <remarks>
        /// The control for the two below. The key is <c>id</c> rather than <c>category</c> because a
        /// nullable column is refused on its null placement, whatever the projection does.
        /// </remarks>
        [TestMethod]
        public void SortPushesPastAnAllPathProjection()
        {
            var best = PlanToCosmos("SELECT c.\"id\" AS \"i\" FROM products AS c ORDER BY c.\"id\"");

            Render(best).Should().Contain("ORDER BY c.id ASC");
        }

        /// <remarks>
        /// A computed column has no document path, and the columns beside it still do. Binding per
        /// ordinal is what lets this sort push: the key names <c>id</c>, a plain path, and never reads
        /// the computed one. Bound all-or-nothing, as it was, the whole sort was declined.
        /// </remarks>
        [TestMethod]
        public void SortOnAPlainColumnPushesPastAComputedProjection()
        {
            var best = PlanToCosmos("SELECT UPPER(c.\"id\") AS \"u\", c.\"id\" AS \"i\" FROM products AS c ORDER BY c.\"id\"");

            Render(best).Should().Contain("ORDER BY c.id ASC");
        }

        /// <remarks>
        /// The other half of the same rule: a sort that does read the computed column has nothing to
        /// order by, Cosmos being unable to address a projection alias, so it is refused — <b>at the
        /// rule</b>, which is what <c>CosmosConverterRule</c> requires. The rule derives its binding by
        /// walking the input rather than reading alias names off the input row type, so it declines here
        /// for the same reason implementation would, and no plan is produced to render.
        /// </remarks>
        [TestMethod]
        public void SortOnAComputedColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT UPPER(c.\"id\") AS \"u\", c.\"id\" AS \"i\" FROM products AS c ORDER BY UPPER(c.\"id\")");

            act.Should().Throw<Exception>();
        }


        // ── Ordering by a column the query does not select ────────────────────────
        //
        // Calcite answers this in three nodes. Ordering by a column outside the select list makes it
        // an output first, and `RelRoot.project()` adds a projection above the sort to drop it again,
        // leaving projection over sort over projection. Cosmos answers it in one statement, which
        // holds one SELECT — so neither projection converts while the sort sits between them, and
        // without a rewrite the whole query declines and the container is read to answer it.
        //
        // The shape stayed out of the suite because reaching it needs a sort that pushes, and a
        // nullable key is refused on its null placement before the question is asked. `id` is not
        // nullable, which is what exposes it.

        /// <summary>
        /// A query ordering by a column it does not select leaves as one statement.
        /// </summary>
        /// <remarks>
        /// The transpose lifts the inner projection above the sort and the merge folds the two into
        /// one, which is the shape that converts. Asserting the statement rather than the plan is
        /// the point: planning wholly in the convention and rendering are the two things this used
        /// to fail at, in that order.
        /// </remarks>
        [TestMethod]
        public void OrderingByAColumnOutsideTheSelectListPushesAsOneStatement()
        {
            var best = PlanToCosmos("SELECT c.\"category\" FROM products AS c ORDER BY c.\"id\"");

            Plan(best).Should().Be(
                "CosmosProject(category=[$4])\n" +
                "  CosmosSort(sort0=[$1], dir0=[ASC])\n" +
                "    CosmosTableScan(table=[[products]])",
                "the two projections fold into the one the statement has room for");

            Render(best).Should().Be("SELECT VALUE { \"category\": c.category } FROM products c ORDER BY c.id ASC");
        }


        // ── What the subtree below has already written ────────────────────────────
        //
        // A statement holds one of each clause, and the service applies them in its own order rather
        // than the plan's. An operator is therefore pushable only onto a subtree that has not written
        // the clause it needs — and, where two clauses reorder instead of colliding, only onto one
        // whose rows it would still be reading. Each node refuses the pairing it cannot render; what
        // these ask is that the rule refuse it first, which is the difference between running the
        // operator in process and failing the query after the planner has committed to the plan.
        //
        // The binding walk reports the clauses, so every rule reads the same answer the implementor
        // will. Each case below reached its node and threw, bar one, which rendered.

        /// <remarks>
        /// A statement has one ORDER BY. The inner sort takes it and its page, and the outer one runs
        /// over the rows that come back.
        /// </remarks>
        [TestMethod]
        public void ASortIsNotPushedOntoASubtreeThatHasAlreadySorted()
        {
            var best = PlanToAsync("SELECT * FROM (SELECT * FROM products AS c ORDER BY c.\"id\" FETCH NEXT 5 ROWS ONLY) AS x ORDER BY x.\"id\"");
            var plan = Plan(best);

            plan.Should().Contain("ClrAsyncEnumerableSort", "the second ordering stays in process: " + plan);
            Render(FindCosmos(best)).Should().Contain("ORDER BY c.id ASC OFFSET 0 LIMIT 5");
        }

        /// <summary>
        /// The one that did not throw: pushed onto a page, a sort renders into a statement that
        /// answers a different question.
        /// </summary>
        /// <remarks>
        /// Cosmos applies OFFSET/LIMIT after ORDER BY, so folding this sort in asks the service to
        /// order the container and then take five, where the plan asked for five rows in no
        /// particular order and an ordering of those. Different rows, and nothing anywhere to say so
        /// — which is why the answer has to be the rule's rather than the node's.
        /// </remarks>
        [TestMethod]
        public void ASortIsNotPushedOntoAPushedRowLimit()
        {
            var best = PlanToAsync("SELECT * FROM (SELECT * FROM products AS c FETCH NEXT 5 ROWS ONLY) AS x ORDER BY x.\"id\"");
            var sql = Render(FindCosmos(best));

            sql.Should().Contain("OFFSET 0 LIMIT 5");
            sql.Should().NotContain("ORDER BY", "the page is taken first, and ordering the container before it takes other rows: " + sql);
            Plan(best).Should().Contain("ClrAsyncEnumerableSort");
        }

        /// <remarks>
        /// WHERE is evaluated before OFFSET/LIMIT, so a predicate cannot join a statement that has
        /// taken its page: it would filter the container and page what survived.
        /// </remarks>
        [TestMethod]
        public void AFilterIsNotPushedOntoAPushedRowLimit()
        {
            var best = PlanToAsync("SELECT * FROM (SELECT * FROM products AS c ORDER BY c.\"id\" FETCH NEXT 5 ROWS ONLY) AS x WHERE x.\"category\" = 'bikes'");
            var sql = Render(FindCosmos(best));

            sql.Should().NotContain("WHERE", "the predicate reads the page, not the container: " + sql);
            Plan(best).Should().Contain("ClrAsyncEnumerableFilter");
        }

        /// <remarks>
        /// The service joins before it orders, pages or de-duplicates, so a traversal cannot join a
        /// statement that has done any of them — it would multiply the rows first and restrict after,
        /// where the plan asked for the restricted rows to be multiplied. A projection below is the
        /// pairing that is allowed, and is covered by
        /// <see cref="UnnestOverAHoistedArrayCarriesTheElement"/>.
        /// </remarks>
        [TestMethod]
        public void AnArrayTraversalIsNotPushedOntoAPagedOrDistinctSubtree()
        {
            var paged = PlanToAsync("SELECT c.\"id\" FROM (SELECT * FROM products AS p ORDER BY p.\"id\" FETCH NEXT 5 ROWS ONLY) AS c, UNNEST(c.\"_MAP\"['tags']) AS t");

            Plan(paged).Should().NotContain("CosmosUnnest", "the page is taken before the traversal: " + Plan(paged));
            Render(FindCosmos(paged)).Should().Contain("OFFSET 0 LIMIT 5");

            var distinct = PlanToAsync("SELECT c.\"id\" FROM (SELECT DISTINCT p.\"id\", p.\"_MAP\" FROM products AS p) AS c, UNNEST(c.\"_MAP\"['tags']) AS t");

            Plan(distinct).Should().NotContain("CosmosUnnest", "the de-duplication happens before the traversal: " + Plan(distinct));
            Render(FindCosmos(distinct)).Should().Contain("SELECT DISTINCT");
        }

        /// <remarks>
        /// ORDER BY RANK is the statement's one ordering and pairs with TOP alone, and the node writes
        /// the whole SELECT itself — so a subtree that has ordered, paged or projected leaves it
        /// nowhere to go, and the three nodes it would have collapsed stay as they are.
        /// </remarks>
        [TestMethod]
        public void AnOrderByRankIsNotPushedOntoASubtreeThatHasWrittenItsClauses()
        {
            var paged = PlanToAsync(
                "SELECT y.\"id\" FROM (SELECT c.\"id\", c.\"_MAP\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 20 ROWS ONLY) AS y " +
                "ORDER BY FULLTEXTSCORE(y.\"_MAP\"['name'], 'steel') FETCH FIRST 10 ROWS ONLY");

            Plan(paged).Should().NotContain("CosmosRank", "the statement has already ordered and paged: " + Plan(paged));

            var distinct = PlanToAsync(
                "SELECT y.\"id\" FROM (SELECT DISTINCT c.\"id\", c.\"_MAP\" FROM products AS c) AS y " +
                "ORDER BY FULLTEXTSCORE(y.\"_MAP\"['name'], 'steel') FETCH FIRST 10 ROWS ONLY");

            Plan(distinct).Should().NotContain("CosmosRank", "the statement has already written its SELECT: " + Plan(distinct));
        }


        // ── A cast to text is sent as the value and rendered on the way back ────

        /// <summary>
        /// A projection whose column is a cast to text pushes, the statement carrying the path.
        /// </summary>
        /// <remarks>
        /// Which is what a view is made of. The cast is not rendered at the service and does not need
        /// to be: Calcite's cast over an <c>ANY</c> value is Java's rendering of the box the reader
        /// already builds, so the value is sent as it stands and rendered as it is read — see
        /// <c>CosmosJson.GetText</c> and <c>DESIGN.md</c>, which keeps the measurement. What changes is
        /// that the statement stops carrying whole documents.
        /// </remarks>
        [TestMethod]
        public void ACastToTextInAProjectionPushes()
        {
            var best = PlanToAsync("SELECT CAST(c.\"_MAP\"['name'] AS VARCHAR) AS \"n\" FROM products AS c");
            var plan = Plan(best);

            plan.Should().Contain("CosmosProject", "the projection belongs at the service: " + plan);
            plan.Should().NotContain("ClrAsyncEnumerableProject", "and nothing should be left above it: " + plan);

            Render(FindCosmos(best)).Should().Contain("SELECT VALUE { \"n\": c.name }");
        }

        /// <summary>
        /// A width is a second conversion the reader does not perform, so a cast carrying one stays in
        /// process.
        /// </summary>
        /// <remarks>
        /// Measured against Calcite's own runtime: <c>VARCHAR(3)</c> truncates <c>'bikes'</c> to
        /// <c>'bik'</c> and <c>CHAR(8)</c> pads it to <c>'bikes&#160;&#160;&#160;'</c>. Rendering either as the bare value
        /// would return a different string, so both are refused.
        /// </remarks>
        [TestMethod]
        public void ACastToTextWithAWidthIsNotRendered()
        {
            Plan(PlanToAsync("SELECT CAST(c.\"_MAP\"['name'] AS VARCHAR(3)) AS \"n\" FROM products AS c"))
                .Should().Contain("ClrAsyncEnumerableProject");

            Plan(PlanToAsync("SELECT CAST(c.\"_MAP\"['name'] AS CHAR(8)) AS \"n\" FROM products AS c"))
                .Should().Contain("ClrAsyncEnumerableProject");
        }

        /// <remarks>
        /// A cast to a number converts rather than renders — <c>CAST(x AS INTEGER)</c> reads the stored
        /// string <c>"30"</c> as 30 and truncates 30.7 — and nothing the service returns reproduces that.
        /// It stays in process, as it did.
        /// </remarks>
        [TestMethod]
        public void ACastToANumberInAProjectionIsStillDeclined()
        {
            Plan(PlanToAsync("SELECT CAST(c.\"_MAP\"['price'] AS INTEGER) AS \"p\" FROM products AS c"))
                .Should().Contain("ClrAsyncEnumerableProject");
        }

        /// <summary>
        /// A rendered column addresses no document path, so a sort on it is not pushed.
        /// </summary>
        /// <remarks>
        /// The whole of what makes the rendering sound. The column carries text and the path carries the
        /// raw value, and the two do not order alike — as text <c>10</c> sorts before <c>9</c>, and
        /// Cosmos orders a boolean before either. Binding it to no path is what makes the sort decline,
        /// exactly as a computed column does.
        ///
        /// Stated <c>NULLS FIRST</c> deliberately: under Calcite's default placement the sort would be
        /// refused on its null placement instead, and the test would pass without saying anything.
        /// </remarks>
        [TestMethod]
        public void ASortOnARenderedCastColumnIsNotPushed()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\", CAST(c.\"_MAP\"['name'] AS VARCHAR) AS \"n\" FROM products AS c ORDER BY 2 NULLS FIRST FETCH NEXT 10 ROWS ONLY"));

            plan.Should().Contain("CosmosProject", "the projection still pushes: " + plan);
            plan.Should().NotContain("CosmosSort", "ordering by the rendering is not ordering by the path: " + plan);
        }

        /// <remarks>
        /// The same for a filter, and for the same reason: <c>= '30'</c> is true of the rendered number
        /// and false at the service.
        /// </remarks>
        [TestMethod]
        public void AFilterOnARenderedCastColumnIsNotPushed()
        {
            var plan = Plan(PlanToAsync(
                "SELECT * FROM (SELECT CAST(c.\"_MAP\"['name'] AS VARCHAR) AS \"n\" FROM products AS c) WHERE \"n\" = '30'"));

            plan.Should().NotContain("CosmosFilter", "the predicate reads a rendering, not a path: " + plan);
        }

        // ── Past a projection that cannot be pushed ──────────────────────────────
        //
        // A view gives a container a relational shape by casting, the row model typing every path
        // ANY. A cast to text is rendered by the reader and pushes; the rest — a width, a numeric
        // target — stays in process, and a sort and row limit above it used to stay with it, reading
        // the container whole to answer a bounded page. Transposed below the projection they push,
        // and the cast runs over the rows that come back.

        /// <summary>
        /// The sort and its row limit reach the statement even though the projection above them
        /// cannot be rendered.
        /// </summary>
        [TestMethod]
        public void ASortOnAnUncastColumnPushesPastAnUnrenderableProjection()
        {
            var best = PlanToAsync("SELECT c.\"id\", CAST(c.\"_MAP\"['price'] AS INTEGER) AS \"p\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 10 ROWS ONLY");
            var plan = Plan(best);

            plan.Should().Contain("CosmosSort", "the sort belongs at the service: " + plan);
            plan.Should().NotContain("ClrAsyncEnumerableSort", "and must not also remain in process: " + plan);
            plan.Should().Contain("ClrAsyncEnumerableProject", "the cast itself still runs in process: " + plan);

            Render(FindCosmos(best)).Should().Contain("ORDER BY c.id ASC OFFSET 0 LIMIT 10");
        }

        /// <remarks>
        /// The other half, and the reason this is sound. Ordering by the cast column is not ordering
        /// by the path underneath, so the sort must stay above the projection. Calcite maps the keys
        /// through and declines where any of them is not a plain reference, which is the whole guard.
        ///
        /// Stated <c>NULLS FIRST</c> deliberately: under Calcite's default placement the sort would
        /// be refused on its null placement instead, and the test would pass without saying anything
        /// about the transpose.
        /// </remarks>
        [TestMethod]
        public void ASortOnTheCastColumnItselfDoesNotTranspose()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\", CAST(c.\"_MAP\"['price'] AS INTEGER) AS \"p\" FROM products AS c ORDER BY 2 NULLS FIRST FETCH NEXT 10 ROWS ONLY"));

            plan.Should().NotContain("CosmosSort", "ordering by the cast is not ordering by the path: " + plan);
        }

        /// <summary>
        /// The shape a paged view has, end to end: a predicate, a cast projection, an ordering and a
        /// row limit. All of it belongs at the service.
        /// </summary>
        /// <remarks>
        /// This is the difference the item is about. Answering ten rows used to read every document
        /// the predicate matched; it now reads ten — and, since the cast to text is rendered rather
        /// than declined, nothing is left in process at all.
        /// </remarks>
        [TestMethod]
        public void APagedViewReadsAPageRatherThanTheMatchingDocuments()
        {
            var best = PlanToAsync(
                "SELECT c.\"id\", CAST(c.\"_MAP\"['name'] AS VARCHAR) AS \"n\" FROM products AS c " +
                "WHERE c.\"category\" = 'bikes' ORDER BY c.\"id\" FETCH NEXT 10 ROWS ONLY");

            var plan = Plan(best);
            plan.Should().NotContain("ClrAsyncEnumerableProject", "nothing is left for the plan to do: " + plan);

            var sql = Render(FindCosmos(best));

            sql.Should().Contain("WHERE (c.category = @p0)");
            sql.Should().Contain("ORDER BY c.id ASC OFFSET 0 LIMIT 10");
        }

        /// <remarks>
        /// The control for both: with no cast the projection pushes and the sort goes with it, which
        /// is the plan the transpose is trying to get back to the shape of.
        /// </remarks>
        [TestMethod]
        public void WithNoCastTheWholeStatementPushesAsBefore()
        {
            var plan = Plan(PlanToAsync("SELECT c.\"id\", c.\"_MAP\"['name'] AS \"n\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 10 ROWS ONLY"));

            plan.Should().Contain("CosmosSort");
            plan.Should().Contain("CosmosProject");
            plan.Should().NotContain("ClrAsyncEnumerableProject", "nothing is left for the plan to do: " + plan);
        }


        // ── Partial filter pushdown ───────────────────────────────

        /// <summary>
        /// Plans a statement, asking for the asynchronous convention so that a plan may legitimately
        /// keep some work in Calcite rather than having to be Cosmos throughout.
        /// </summary>
        RelNode PlanToAsync(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            foreach (var rule in Apache.Calcite.Extensions.Adapter.AsyncEnumerable.ClrAsyncEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(Apache.Calcite.Extensions.Adapter.AsyncEnumerable.ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <remarks>
        /// INITCAP has no Cosmos form, so the whole predicate used to be declined and every document
        /// crossed the wire. The renderable conjunct is pushed and the rest rechecked above it, which is
        /// sound because dropping a conjunct only ever weakens: the service discards nothing the full
        /// predicate would have kept.
        /// </remarks>
        [TestMethod]
        public void RenderablePartOfAPredicateIsPushedAndTheRestRechecked()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND INITCAP(c.\"id\") = 'X'");
            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter");
            plan.Should().Contain("INITCAP");
        }

        /// <summary>
        /// A disjunction whose branch cannot be rendered is pushed as what that branch implies.
        /// </summary>
        /// <remarks>
        /// Dropping a disjunct strengthens, so an <c>OR</c> with an untranslatable branch used to be
        /// declined whole and every document crossed the wire. Since a branch can only be true where
        /// the paths it reads are defined, <c>a OR b</c> pushes <c>a OR IS_DEFINED(…)</c> — implied by
        /// the original, so it discards nothing the original would have kept — and the original is
        /// rechecked above it.
        /// </remarks>
        [TestMethod]
        public void ADisjunctionWithAnUntranslatableBranchIsWeakened()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' OR INITCAP(c.\"_etag\") = 'X'");
            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter", "something should reach the service: " + plan);
            plan.Should().Contain("IS_DEFINED", "the untranslatable branch implies its path is defined: " + plan);
            plan.Should().Contain("INITCAP", "and the original is still rechecked: " + plan);
        }

        /// <summary>
        /// A branch that can observe absence implies nothing about definedness, and is not weakened.
        /// </summary>
        /// <remarks>
        /// Measured, and the reason the rule is about absence rather than about polarity:
        /// <c>NOT IS_DEFINED(c.x)</c> is true exactly where the path is missing, so a branch containing
        /// it cannot imply the path is there. Weakening it anyway would strengthen the predicate and
        /// lose rows — the failure this whole design is arranged to make impossible.
        /// </remarks>
        [TestMethod]
        public void ABranchThatObservesAbsenceIsNotWeakened()
        {
            var best = PlanToAsync(
                "SELECT * FROM products AS c " +
                "WHERE c.\"category\" = 'bikes' OR (NOT IS_DEFINED(c.\"_etag\") AND INITCAP(c.\"_etag\") = 'X')");

            var plan = Plan(best);

            plan.Should().NotContain("CosmosFilter",
                "nothing about this disjunction is safe to push: " + plan);
        }

        /// <summary>
        /// SQL's own null tests observe absence, and a branch using one is not weakened either.
        /// </summary>
        /// <remarks>
        /// <c>x IS NULL</c> renders as <c>(NOT IS_DEFINED(x) OR IS_NULL(x))</c>, so it is true where
        /// the path is missing — but its Rex operator belongs to the standard table rather than to the
        /// Cosmos family, so checking only the latter let it through. A branch containing one can be
        /// true with the path absent, and weakening it to <c>IS_DEFINED</c> would have discarded
        /// exactly those rows.
        /// </remarks>
        [TestMethod]
        public void ABranchUsingSqlNullTestsIsNotWeakened()
        {
            var best = PlanToAsync(
                "SELECT * FROM products AS c " +
                "WHERE c.\"id\" = 'x' OR (c.\"category\" IS NULL AND INITCAP(c.\"_etag\") = 'X')");

            var plan = Plan(best);

            plan.Should().NotContain("CosmosFilter",
                "a branch that can be true with the path absent implies nothing about definedness: " + plan);
        }

        /// <summary>
        /// A sort above a pushed aggregate stays in Calcite, and the aggregate still pushes.
        /// </summary>
        /// <remarks>
        /// Cosmos rejects <c>GROUP BY</c> and <c>ORDER BY</c> in one statement. <c>CosmosSort</c>
        /// refuses the combination when it renders, but refusing only there is too late — the rule
        /// would already have produced a node the planner cannot implement, which fails rather than
        /// planning something slower. Declining in the rule is also the better plan: the sort then runs
        /// over one row per group instead of over the container.
        /// </remarks>
        [TestMethod]
        public void ASortAboveAPushedAggregateStaysInCalcite()
        {
            var best = PlanToAsync("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY c.\"category\" ORDER BY c.\"category\"");
            var plan = Plan(best);

            plan.Should().Contain("CosmosAggregate", "the grouping is still worth pushing: " + plan);
            plan.Should().NotContain("CosmosSort", "Cosmos will not take ORDER BY alongside GROUP BY: " + plan);
        }

        /// <summary>
        /// The same split applies to a filter sitting above a projection.
        /// </summary>
        /// <remarks>
        /// The argument does not depend on what the filter sits on — dropping a conjunct only ever
        /// weakens, so the service discards nothing the full predicate would have kept. The rule used
        /// to match a filter directly over the scan and nothing else, which meant a projection between
        /// the two cost the whole pushdown rather than the untranslatable half of it.
        /// </remarks>
        [TestMethod]
        public void APredicateAboveAProjectionIsSplitToo()
        {
            var best = PlanToAsync(
                "SELECT * FROM (SELECT c.\"category\" AS cat, c.\"id\" AS ident FROM products AS c) AS t " +
                "WHERE t.cat = 'bikes' AND INITCAP(t.ident) = 'X'");

            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter");
            plan.Should().Contain("INITCAP");
        }

        /// <remarks>
        /// The pushed half carries the renderable conjunct and whatever the other one implies, never
        /// the other one itself. <c>INITCAP</c> has no Cosmos form, so it stays above and is rechecked;
        /// that it is a comparison at all says the path it reads is defined, and that much the service
        /// can apply. The partition key is still recovered from the conjunct that pins it.
        /// </remarks>
        [TestMethod]
        public void ThePushedHalfCarriesTheRenderableConjunctAndWhatTheOtherImplies()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND INITCAP(c.\"id\") = 'X'");
            var cosmos = FindCosmos(best);

            var query = Query(cosmos);
            query.Sql.Should().Contain("(c.category = @p0)");
            query.Sql.Should().Contain("IS_DEFINED(c.id)");
            query.Sql.Should().NotContain("INITCAP");
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// A wholly renderable predicate is not split; there is nothing to leave behind.
        /// </remarks>
        [TestMethod]
        public void AWhollyRenderablePredicateIsNotSplit()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND c.\"id\" = 'x'");

            Plan(best).Split("CosmosFilter").Length.Should().Be(2);
        }

        /// <summary>
        /// Returns the root of the pushed-down Cosmos subtree — the highest node in the convention,
        /// which is the one that renders the whole statement.
        /// </summary>
        static RelNode FindCosmos(RelNode node)
        {
            if (node is CosmosRel)
                return node;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (FindCosmos((RelNode)inputs.get(i)) is RelNode found)
                    return found;

            return null!;
        }


        // ── Navigating the document ───────────────────────────────────────────────

        /// <remarks>
        /// The map column's type is one level — MAP&lt;VARCHAR, ANY&gt; — and the document beneath it is
        /// not. Depth still resolves because ITEM over ANY is ANY, so the chain type-checks, and the
        /// translator folds the whole chain into one path rather than nesting accessors.
        /// </remarks>
        [TestMethod]
        public void NestedPropertiesResolveToASinglePath()
        {
            var best = PlanToCosmos("SELECT c.\"_MAP\"['metadata']['sku'] AS \"sku\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"sku\": c.metadata.sku } FROM products c");
        }

        [TestMethod]
        public void ThreeLevelsResolveJustAsFar()
        {
            var best = PlanToCosmos("SELECT c.\"_MAP\"['a']['b']['c'] AS \"deep\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"deep\": c.a.b.c } FROM products c");
        }

        /// <remarks>
        /// An array index is a path segment like any other.
        /// </remarks>
        [TestMethod]
        public void ArrayIndexingIsPartOfThePath()
        {
            // Subscripted from one in SQL and from zero at the service, so the first element is
            // [1] here and c.tags[0] there.
            var best = PlanToCosmos("SELECT c.\"_MAP\"['tags'][1] AS \"first\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"first\": c.tags[0] } FROM products c");
        }

        /// <remarks>
        /// A non-constant key has no path form — the statement addresses a property by name, and the
        /// name is not known until the row is read — so it is declined rather than guessed at.
        /// </remarks>
        [TestMethod]
        public void ANonConstantKeyIsNotAPath()
        {
            var act = () => PlanToCosmos("SELECT c.\"_MAP\"[c.\"id\"] AS \"dynamic\" FROM products AS c");

            act.Should().Throw<Exception>();
        }


        // ── Testing whether a property exists ─────────────────────────────────────

        /// <remarks>
        /// The SQL spelling. Cosmos distinguishes an absent property from one present and null and SQL
        /// does not, so this renders as both tests — which is what makes it match a document that simply
        /// lacks the property.
        /// </remarks>
        [TestMethod]
        public void IsNotNullOnAMapPropertyTestsBothCosmosStates()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['metadata'] IS NOT NULL");

            Render(best).Should().Contain("(IS_DEFINED(c.metadata) AND NOT IS_NULL(c.metadata))");
        }

        /// <remarks>
        /// The exact spelling, for a query that needs to tell absent from null — which SQL cannot say and
        /// this adapter's own operator can. It reaches the validator through the chained operator table.
        /// </remarks>
        [TestMethod]
        public void IsDefinedTestsExistenceAlone()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(c.\"_MAP\"['metadata'])");

            Render(best).Should().Contain("WHERE IS_DEFINED(c.metadata)");
        }

        /// <remarks>
        /// Existence of a nested property, which is the same question one level down and the same path.
        /// </remarks>
        [TestMethod]
        public void IsDefinedReachesANestedProperty()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(c.\"_MAP\"['metadata']['sku'])");

            Render(best).Should().Contain("WHERE IS_DEFINED(c.metadata.sku)");
        }


        // ── Ranking by a scoring function ─────────────────────────────────────────

        /// <remarks>
        /// Calcite expresses this as three nodes — project the score, sort on it, project it away — and
        /// the first is a statement Cosmos rejects, a scoring function not being projectable. The whole
        /// shape collapses into one clause, and the score never appears in the select list.
        /// </remarks>
        [TestMethod]
        public void OrderingByAScoreBecomesOrderByRank()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') FETCH FIRST 10 ROWS ONLY");
            var sql = Render(best);

            sql.Should().Be("SELECT TOP 10 VALUE { \"id\": c.id } FROM products c ORDER BY RANK FULLTEXTSCORE(c.name, @p0)");
            sql.Should().NotContain("\"$f");
        }

        /// <remarks>
        /// The keyword binds like any other literal, so the statement text does not vary with it.
        /// </remarks>
        [TestMethod]
        public void TheRankKeywordIsBound()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') FETCH FIRST 5 ROWS ONLY"));

            query.Parameters.Should().ContainSingle().Which.Value.Should().Be("steel");
        }

        /// <remarks>
        /// RRF fuses two scores, and its arguments are themselves scoring functions rather than paths.
        /// </remarks>
        [TestMethod]
        public void RrfFusesTwoScores()
        {
            var best = PlanToCosmos(
                "SELECT c.\"id\" FROM products AS c " +
                "ORDER BY RRF(FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel'), FULLTEXTSCORE(c.\"_MAP\"['tags'], 'frame')) " +
                "FETCH FIRST 10 ROWS ONLY");

            Render(best).Should().Contain("ORDER BY RANK RRF(FULLTEXTSCORE(c.name, @p0), FULLTEXTSCORE(c.tags, @p1))");
        }

        /// <remarks>
        /// A scoring function anywhere but the rank clause is refused: the service will not project one,
        /// and will not filter on one either.
        /// </remarks>
        [TestMethod]
        public void AProjectedScoreIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') AS \"s\" FROM products AS c");

            act.Should().Throw<Exception>();
        }

        [TestMethod]
        public void AScoreInAPredicateIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') > 1");

            act.Should().Throw<Exception>();
        }


        /// <remarks>
        /// The shape the cast case exists for, reached from SQL rather than built by hand: comparing
        /// against a function that returns a double coerces the literal, so the predicate arrives with a
        /// cast wrapped around the bound. Declining it would decline the predicate.
        /// </remarks>
        [TestMethod]
        public void AComparisonAgainstAVectorDistancePushes()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE VECTORDISTANCE(c.\"_MAP\"['a'], c.\"_MAP\"['b']) < 0.5");

            Render(best).Should().Contain("WHERE (VECTORDISTANCE(c.a, c.b) < @p0)");
        }


        // ── The declaration decides ───────────────────────────────────────────────

        /// <remarks>
        /// A full text predicate over a path the container declares nothing about is refused by the
        /// service with a bodyless 400 that names neither the path nor the function. The rule
        /// declines instead, so the refusal happens while planning and says which path is at fault.
        /// <c>/description</c> is not among the container's declared paths; <c>/name</c> is, and the
        /// tests above push.
        /// </remarks>
        [TestMethod]
        public void AFullTextPredicateOverAnUndeclaredPathIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTCONTAINS(c.\"_MAP\"['description'], 'steel')");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// And the same for a score, which reaches the rank clause through a different rule.
        /// </remarks>
        [TestMethod]
        public void ARankOverAnUndeclaredPathIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(c.\"_MAP\"['description'], 'steel') FETCH FIRST 10 ROWS ONLY");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// A vector distance needs one of its two vectors to be a declared path. <c>/a</c> is one and
        /// <c>/b</c> is not, so the test above pushes on the strength of the first argument alone;
        /// with neither declared there is nothing for the service to search.
        /// </remarks>
        [TestMethod]
        public void AVectorDistanceOverUndeclaredPathsIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE VECTORDISTANCE(c.\"_MAP\"['b'], c.\"_MAP\"['d']) < 0.5");

            act.Should().Throw<Exception>();
        }

        // ── Point lookup ──────────────────────────────────────────────────────────

        /// <remarks>
        /// A lookup by id and a complete partition key is a read, not a query: about 1 RU against the
        /// 2.3 a query costs at best, and no query engine.
        /// </remarks>
        [TestMethod]
        public void IdAndPartitionKeyBecomeAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"category\" = 'bikes'"));

            query.PointReadId.Should().Be("x");
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// <b>The predicate must say nothing else.</b> A point read applies no predicate of its own, so
        /// under an extra conjunct it would return a document the query excludes — a wrong answer rather
        /// than a slow one. The statement is still rendered and still executed; it is just executed as a
        /// query.
        /// </remarks>
        [TestMethod]
        public void AResidualPredicateRulesOutAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"category\" = 'bikes' AND c.\"_ts\" > 100"));

            query.PointReadId.Should().BeNull();
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        [TestMethod]
        public void AnIdWithoutThePartitionKeyIsNotAPointRead()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'")).PointReadId.Should().BeNull();
        }

        [TestMethod]
        public void APartitionKeyWithoutAnIdIsNotAPointRead()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes'")).PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// A read returns one document whole. A row limit and an ordering describe a result set rather
        /// than a document, so either rules it out even though the predicate would allow it.
        /// </remarks>
        [TestMethod]
        public void ARowLimitRulesOutAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"category\" = 'bikes' FETCH FIRST 1 ROWS ONLY"));

            query.PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// Under a disjunction an equality does not constrain the whole predicate, so it pins nothing —
        /// the same reason the partition key is not recovered from one.
        /// </remarks>
        [TestMethod]
        public void ADisjunctionIsNotAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE (c.\"id\" = 'x' AND c.\"category\" = 'bikes') OR c.\"id\" = 'y'"));

            query.PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// A projection of plain paths still reads: the converter walks each path in the returned
        /// document rather than naming a property of an object the statement never constructed.
        /// </remarks>
        [TestMethod]
        public void AProjectionOfPathsStillPointReads()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\", c.\"category\" FROM products AS c WHERE c.\"id\" = 'x' AND c.\"category\" = 'bikes'"));

            query.PointReadId.Should().Be("x");
        }


        // ── Hierarchical partition keys ───────────────────────────────────────────

        static readonly CosmosContainerMetadata Tenanted = new("products", new[] { "/tenant", "/user" });

        /// <summary>
        /// Plans against a container whose partition key is hierarchical.
        /// </summary>
        CosmosQuery TenantedQuery(string sql)
        {
            _table = new CosmosTable(Tenanted);

            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            planner.setRoot(planner.changeTraits(logical, logical.getTraitSet().replace(_table.Convention).simplify()));
            var best = planner.findBestExp();

            var implementor = new CosmosImplementor(best.getCluster().getRexBuilder(), Tenanted);
            implementor.Visit(best);
            return implementor.Build();
        }

        /// <remarks>
        /// Cosmos routes on any prefix of a hierarchical key, so pinning the outermost path reaches the
        /// partitions under that tenant rather than every partition in the container. Recovering only a
        /// complete key threw that away.
        /// </remarks>
        [TestMethod]
        public void APinnedOutermostPathRoutesOnThePrefix()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"tenant\" = 'acme'");

            query.PartitionKeyValues.Should().Equal("acme");
            query.PartitionKeyIsComplete.Should().BeFalse();
        }

        [TestMethod]
        public void PinningEveryPathIsACompleteKey()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"tenant\" = 'acme' AND c.\"user\" = 'kim'");

            query.PartitionKeyValues.Should().Equal("acme", "kim");
            query.PartitionKeyIsComplete.Should().BeTrue();
        }

        /// <remarks>
        /// Prefix means prefix. Routing is on the leading components, so pinning an inner path without
        /// the one above it narrows nothing and must not be presented as though it did.
        /// </remarks>
        [TestMethod]
        public void AnInnerPathWithoutTheOuterRoutesNothing()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"user\" = 'kim'");

            query.PartitionKeyValues.Should().BeNull();
        }

        /// <remarks>
        /// A prefix routes to a set of partitions and does not identify a document, so it cannot carry
        /// a point read however much of the predicate is an id.
        /// </remarks>
        [TestMethod]
        public void APrefixCannotCarryAPointRead()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"tenant\" = 'acme' AND c.\"id\" = 'x'");

            query.PartitionKeyValues.Should().Equal("acme");
            query.PointReadId.Should().BeNull();
        }


        // ── What the table claims about ordering ──────────────────────────────────

        /// <remarks>
        /// A probe, not a specification. RelOptTableImpl.getCollationList returns the statistic's
        /// collations, and RelMdCollation reports them as the collation <em>of a scan</em> — that is,
        /// the order rows already arrive in. Whether the planner is being told that is what this asks.
        /// </remarks>
        [TestMethod]
        public void AScanIsNotClaimedToBeSorted()
        {
            var best = PlanToCosmos("SELECT * FROM products");
            var mq = best.getCluster().getMetadataQuery();

            var collations = mq.collations(best);

            // The container declares a composite index over (/id, /_ts). An index permits an ORDER BY;
            // it does not order a query that has none, and Cosmos guarantees no order without one. A
            // scan claiming a collation would licence the planner to drop a Sort that asked for it.
            collations.Should().NotBeNull();
            collations.size().Should().Be(0, "a Cosmos scan returns rows in no guaranteed order");
        }

    }

}