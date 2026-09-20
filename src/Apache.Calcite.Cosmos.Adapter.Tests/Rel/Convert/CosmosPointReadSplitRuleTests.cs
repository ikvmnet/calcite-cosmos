using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using Apache.Calcite.Extensions.Adapter.Enumerable;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.rel;
using org.apache.calcite.prepare;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel.Convert
{

    /// <summary>
    /// Covers the lookup that pins a point read and then says one thing more — issue #92 — and the
    /// choice <c>CosmosPointReadSplitRule</c> offers for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These plan for the CLR convention rather than the Cosmos one, for the reason
    /// <see cref="CosmosAggregateSplitRuleTests"/> does: a finishing filter needs somewhere outside
    /// the Cosmos convention to live. Asked for a plan wholly in the Cosmos convention the split cannot
    /// express itself — the residual would have to convert back and merge — which is exactly what it
    /// means for this to be a partial pushdown.
    /// </para>
    /// <para>
    /// The two cases differ only in the container's measured document size, which is the whole point:
    /// the same predicate plans one way over records and the other way over large bodies, because
    /// <see cref="CosmosRequestUnitModel"/> prices the routes differently and the planner is choosing
    /// rather than being told.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosPointReadSplitRuleTests
    {

        /// <summary>A container the service has not been asked about — the size is unknown.</summary>
        static CosmosContainerMetadata Unmeasured() => new("products", new[] { "/category" });

        /// <summary>A container of records, comfortably under the break-even.</summary>
        static CosmosContainerMetadata SmallDocuments() =>
            new CosmosContainerMetadata("products", new[] { "/category" })
                .WithStatistics(new CosmosContainerStatistics(1000L, 1000L * 512L, 1));

        /// <summary>A container of bodies well over the break-even, where the read loses.</summary>
        static CosmosContainerMetadata LargeDocuments() =>
            new CosmosContainerMetadata("products", new[] { "/category" })
                .WithStatistics(new CosmosContainerStatistics(1000L, 1000L * 200L * 1024L, 1));

        CosmosContainerMetadata _metadata = null!;
        CosmosTable _products = null!;

        void Use(CosmosContainerMetadata metadata)
        {
            _metadata = metadata;
            _products = new CosmosTable(metadata);
        }

        RelNode PlanLogical(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _products);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(), Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance);

            var validator = SqlValidatorUtil.newValidator(
                operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));

            // A by-id lookup returns one document, and Calcite guesses otherwise. See
            // CosmosRelMetadataQuery for why that guess decides this plan.
            CosmosRelMetadataQuery.Install(cluster);

            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).project();
        }

        /// <summary>
        /// Plans a statement for the CLR convention, which is where a finishing filter can live.
        /// </summary>
        RelNode Plan(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_products.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

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

        /// <summary>
        /// Builds the query a pushed subtree would execute, including what it recovered about execution.
        /// </summary>
        CosmosQuery Query(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), _metadata);
            implementor.Visit(rel);
            return implementor.Build();
        }

        static string Text(RelNode rel) => RelOptUtil.toString(rel);

        /// <summary>The lookup shape issue #92 is about.</summary>
        const string Lookup =
            "SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"$.category\" = 'bikes' AND c.\"_ts\" > 100";

        /// <remarks>
        /// Over a container of records the read is the cheaper route, so the planner holds the residual
        /// back and takes it. The point read is recovered from a predicate that now genuinely says
        /// nothing else, which is the standard <c>TryExtractPointRead</c> always applied — the rule moved
        /// the predicate rather than lowering the bar.
        /// </remarks>
        [TestMethod]
        public void AResidualIsHeldBackSoThePointReadIsRecovered()
        {
            Use(SmallDocuments());

            var plan = Plan(Lookup);

            var pushed = Find<CosmosFilter>(plan);
            pushed.Should().NotBeNull("the pinned equalities should still reach the service");

            var query = Query(pushed!);

            query.PointReadId.Should().Be("x", "the pushed half says nothing beyond id and the partition key");
            query.PartitionKeyValues.Should().Equal("bikes");

            // And the residual is not lost: it is applied above, by Calcite, with Calcite's semantics.
            query.Sql.Should().NotContain("_ts", "the residual is held back rather than pushed");
            Text(plan).Should().Contain("ClrEnumerableFilter", "and finishes outside the Cosmos convention");
        }

        /// <remarks>
        /// The same statement over a container of large bodies plans the other way. A point read is
        /// charged for the document it returns at about six times a query's rate, so past
        /// <see cref="CosmosRequestUnitModel.BreakEvenDocumentSizeInBytes"/> holding the residual back
        /// buys a more expensive plan, and the planner declines it.
        /// </remarks>
        [TestMethod]
        public void ALargeBodyKeepsTheWholePredicatePushed()
        {
            Use(LargeDocuments());

            var plan = Plan(Lookup);

            var pushed = Find<CosmosFilter>(plan);
            pushed.Should().NotBeNull();

            var query = Query(pushed!);

            query.PointReadId.Should().BeNull("a point read of a 200 KB body costs more than the query it would replace");
            query.Sql.Should().Contain("_ts", "so the whole predicate is pushed, as it is today");
        }

        /// <remarks>
        /// An unmeasured container prices the read at its floor, which is the most favourable reading it
        /// can be given — see <c>CosmosFilter.RequestUnits</c>. Even so the split is not taken, for the
        /// structural reason above rather than a pricing one, and this pins that so the two causes are
        /// not confused if one of them changes.
        /// </remarks>
        [TestMethod]
        public void AnUnmeasuredContainerTakesTheRead()
        {
            Use(Unmeasured());

            var query = Query(Find<CosmosFilter>(Plan(Lookup))!);

            query.PointReadId.Should().Be("x");
        }

        /// <remarks>
        /// Nothing changes for the predicate that was always a point read: there is no residual to hold
        /// back, so the rule does not fire and the existing path answers.
        /// </remarks>
        [TestMethod]
        public void APredicateWithNoResidualIsUnaffected()
        {
            Use(SmallDocuments());

            var query = Query(Find<CosmosFilter>(
                Plan("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"$.category\" = 'bikes'"))!);

            query.PointReadId.Should().Be("x");
        }

        /// <remarks>
        /// A residual only the service can evaluate cannot be lifted, however cheap the read would be.
        /// <c>REGEXMATCH</c> has no in-process body, so a plan that leaves it above the converter cannot
        /// be generated at all — Calcite fails only at code generation, with "Unable to implement",
        /// which is how CI found this. The type tests used to be in the same position and no longer are;
        /// see <c>ATypeTestResidualIsLiftedBecauseItHasABody</c>.
        /// The rule has to decline rather than offer a plan that is cheaper and impossible.
        /// </remarks>
        [TestMethod]
        public void AResidualOnlyTheServiceCanEvaluateIsNotLifted()
        {
            Use(SmallDocuments());

            var plan = Plan("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"$.category\" = 'bikes' AND REGEXMATCH(c.\"$.category\", 'b.*')");

            Text(plan).Should().NotContain("ClrEnumerableFilter",
                "a Cosmos function has no CLR implementation, so a filter carrying it cannot be implemented above the converter");

            var query = Query(Find<CosmosFilter>(plan)!);

            query.PointReadId.Should().BeNull("the whole predicate has to stay with the service");
            query.Sql.Should().Contain("REGEXMATCH");
        }

        /// <remarks>
        /// A type test is liftable, because <c>CosmosFunctionBodies</c> answers it in process and
        /// answers what the service answers — see <c>CosmosFunctionBodiesTests</c>. So the
        /// soft-delete shape issue #92 was really about, which asks <c>IS_NULL</c> rather than SQL's
        /// <c>IS NULL</c>, now reaches a point read instead of pinning the statement to a query.
        /// </remarks>
        [TestMethod]
        public void ATypeTestResidualIsLiftedBecauseItHasABody()
        {
            Use(SmallDocuments());

            var plan = Plan("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"$.category\" = 'bikes' AND IS_STRING(c.\"$.category\")");

            var query = Query(Find<CosmosFilter>(plan)!);

            query.PointReadId.Should().Be("x", "the type test can finish outside the convention, so the read is recoverable");
            query.Sql.Should().NotContain("IS_STRING", "it is held back rather than pushed");
            Text(plan).Should().Contain("ClrEnumerableFilter", "and finishes in process");
        }

        /// <remarks>
        /// And nothing changes where the pinned half is not a complete point read on its own: holding a
        /// residual back would weaken the pushed predicate for no read in return.
        /// </remarks>
        [TestMethod]
        public void AnIncompletePinningIsNotWorthHoldingBack()
        {
            Use(SmallDocuments());

            var query = Query(Find<CosmosFilter>(
                Plan("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"_ts\" > 100"))!);

            query.PointReadId.Should().BeNull("no partition key is pinned, so there is no read to recover");
            query.Sql.Should().Contain("_ts", "and the whole predicate is pushed");
        }

    }

}
