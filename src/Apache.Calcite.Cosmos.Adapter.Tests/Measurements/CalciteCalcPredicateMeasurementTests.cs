using Apache.Calcite.Extensions.Adapter.Cursor;
using Apache.Calcite.Extensions.Rel.Metadata;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.hep;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.tools;
using org.apache.calcite.util;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's metadata pulls up from beneath a correlate: the predicates the Calc above it simplifies its program
    /// over (<c>ClrCursorCalc.Implement</c>), where the correlate rule's own simplification reads none.
    /// </summary>
    /// <remarks>
    /// In process, over a tree built by hand: no statement and no planner run.
    /// </remarks>
    public class CalciteCalcPredicateMeasurementTests
    {

        /// <summary>
        /// A Calc pulls up no predicates, over a filter and of a condition of its own, a <c>LogicalCalc</c> and the host's
        /// <c>ClrCursorCalc</c> alike, under Calcite's metadata and the host's: <c>RelMdPredicates</c> has no handler for a
        /// Calc. So a correlate whose left is a Calc pulls up none, where one whose left is the filter pulls up its condition,
        /// a correlate's predicates being its left's. The correlate rule's simplification reads no predicates on that ground.
        /// </summary>
        [Fact]
        public void ACalcPullsUpNoPredicates()
        {
            foreach (var host in new[] { false, true })
            {
                var rex = new RexBuilder(new JavaTypeFactoryImpl());
                var cluster = RelOptCluster.create(new HepPlanner(HepProgram.builder().build()), rex);
                if (host)
                {
                    cluster.setMetadataQuerySupplier(ClrRelMetadataProvider.QuerySupplier(ClrCursorRelMetadata.Provider));
                    cluster.invalidateMetadataQuery();
                }

                var builder = RelBuilder.proto(Contexts.EMPTY_CONTEXT).create(cluster, null);
                var values = builder.values(new[] { "a" }, java.lang.Integer.valueOf(1), java.lang.Integer.valueOf(2), java.lang.Integer.valueOf(3)).build();
                var a = rex.makeInputRef(values, 0);
                var condition = rex.makeCall(SqlStdOperatorTable.EQUALS, a, rex.makeExactLiteral(java.math.BigDecimal.ONE));
                var filter = LogicalFilter.create(values, condition);
                var mq = cluster.getMetadataQuery();
                mq.getPulledUpPredicates(filter).pulledUpPredicates.toString().Should().Contain(condition.toString(), "a filter pulls up its condition");

                var projected = RexProgram.create(values.getRowType(), java.util.Collections.singletonList(a), null, values.getRowType(), rex);
                var conditioned = RexProgram.create(values.getRowType(), java.util.Collections.singletonList(a), condition, values.getRowType(), rex);
                foreach (var calc in new RelNode[] { LogicalCalc.create(filter, projected), LogicalCalc.create(values, conditioned), ClrCursorCalc.Create(filter, projected), ClrCursorCalc.Create(values, conditioned) })
                {
                    var shown = $"{calc.GetType().Name} over {calc.getInput(0).GetType().Name}, host {host}";
                    mq.getPulledUpPredicates(calc).pulledUpPredicates.isEmpty().Should().BeTrue(shown);
                    mq.getPulledUpPredicates(Correlate(calc, values)).pulledUpPredicates.isEmpty().Should().BeTrue("a correlate over " + shown);
                }

                mq.getPulledUpPredicates(Correlate(filter, values)).pulledUpPredicates.toString().Should().Contain(condition.toString(), "a correlate pulls up its left's");
            }
        }

        /// <summary>An inner correlate of a left and a right, which reads none of the left's fields.</summary>
        static RelNode Correlate(RelNode left, RelNode right) =>
            LogicalCorrelate.create(left, right, java.util.Collections.emptyList(), left.getCluster().createCorrel(), ImmutableBitSet.of(), JoinRelType.INNER);

    }

}
