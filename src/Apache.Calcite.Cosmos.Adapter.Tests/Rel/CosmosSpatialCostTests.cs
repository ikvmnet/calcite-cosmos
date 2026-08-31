using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.plan;
using org.apache.calcite.rex;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// What a spatial predicate costs, which is a property of the container rather than of the plan.
    /// </summary>
    /// <remarks>
    /// A range index does not serve a spatial function, and the default indexing policy declares no
    /// spatial index while indexing every path — so the general question <c>IsPathIndexed</c> answers
    /// says a proximity predicate is cheap on a container where it reads everything. Asking the spatial
    /// question instead is what makes the two plans comparable.
    /// </remarks>
    [TestClass]
    public class CosmosSpatialCostTests
    {

        const string PointText = """{"type":"Point","coordinates":[-122.12,47.66]}""";

        static readonly CosmosContainerMetadata WithoutIndex = new("places", new[] { "/category" });

        static readonly CosmosContainerMetadata WithIndex = new("places", new[] { "/category" }, null, null, null, new[] { "/location/*" });

        static double CostOfSpatialFilter(CosmosContainerMetadata container)
        {
            var typeFactory = new org.apache.calcite.jdbc.JavaTypeFactoryImpl();
            var rootSchema = org.apache.calcite.jdbc.CalciteSchema.createRootSchema(false);
            rootSchema.add("places", new CosmosTable(container));

            var reader = new org.apache.calcite.prepare.CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new org.apache.calcite.config.CalciteConnectionConfigImpl(new java.util.Properties()));

            var table = (RelOptTable)reader.getTable(java.util.Collections.singletonList("places"));

            var planner = new org.apache.calcite.plan.volcano.VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var rex = new RexBuilder(typeFactory);
            var cluster = RelOptCluster.create(planner, rex);
            var convention = CosmosConvention.Create(container);
            var traits = cluster.traitSetOf(convention);

            var map = rex.makeInputRef(((org.apache.calcite.rel.type.RelDataTypeField)table.getRowType().getFieldList().get(0)).getType(), 0);
            var path = rex.makeCall(
                org.apache.calcite.sql.fun.SqlStdOperatorTable.ITEM,
                map,
                rex.makeLiteral("location", typeFactory.createSqlType(SqlTypeName.VARCHAR, 8)));

            var condition = rex.makeCall(
                CosmosOperators.StWithin,
                path,
                rex.makeLiteral(PointText, typeFactory.createSqlType(SqlTypeName.VARCHAR, PointText.Length)));

            var filter = new CosmosFilter(cluster, traits, new CosmosTableScan(cluster, traits, table), condition);

            return filter.computeSelfCost(planner, cluster.getMetadataQuery())!.getRows();
        }

        /// <remarks>
        /// The container that can serve the predicate from an index prices it below the one that has to
        /// read every document to answer it.
        /// </remarks>
        [TestMethod]
        public void ASpatialPredicateCostsMoreWithoutASpatialIndex()
        {
            CostOfSpatialFilter(WithoutIndex).Should().BeGreaterThan(CostOfSpatialFilter(WithIndex));
        }

        /// <remarks>
        /// And the penalty is the same one an ordinary unindexed path carries — the difference is which
        /// index is asked about, not how much it is worth.
        /// </remarks>
        [TestMethod]
        public void ThePenaltyIsTheUnindexedPathPenalty()
        {
            CostOfSpatialFilter(WithoutIndex).Should().BeApproximately(CostOfSpatialFilter(WithIndex) * CosmosFilter.UnindexedPathPenalty, 1e-9);
        }

    }

}
