using System.Collections.Generic;
using System.Linq;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Moves each conjunct of a filter above a correlate that reads one side alone into that side, as Calcite's
    /// <c>FILTER_CORRELATE</c> does, and leaves above what Calcite's simplification of the whole condition evaluates beside
    /// the conjuncts moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A predicate over a traversed element is written above the traversal, where <see cref="CosmosUnnestRule"/> cannot see
    /// it; moved into the correlate it sits between the traversal and the uncollect, which that rule renders as a
    /// <c>WHERE</c> over the alias, and a conjunct of the document alone goes into the request. Calcite's rule leaves the
    /// rest above as written. Calcite in process simplifies the condition it is given whole, unknown read as false, so a
    /// conjunct its siblings make go unread is not evaluated: <c>m IS NOT NULL AND (m IS NOT NULL OR CAST(u.t AS INTEGER) =
    /// 1)</c> is <c>m IS NOT NULL</c>. Left above as written, the <c>OR</c> cast each element of the rows the request
    /// returned and failed on an element <c>"x"</c>, where Calcite answers.
    /// </para>
    /// <para>
    /// So what is left above is <see cref="CosmosFilterSplitRule.Residual"/>'s reading of the condition beside the conjuncts
    /// moved, each true on every row the correlate returns: the rest as written where the simplification reads the two
    /// apart, Calcite's rule's own filter; its terms but those of the conjuncts moved where it reads them together; and the
    /// condition as it stands where it reads the whole as false. A condition holding the service's form
    /// (<see cref="Sql.CosmosOperators.HoldsServiceForm"/>) is left as Calcite's rule leaves it, since that reading is of a
    /// condition Calcite evaluates.
    /// </para>
    /// <para>
    /// It terminates: it does not fire where every conjunct it would move is applied beneath its side already, compared as
    /// Calcite compares expressions, so a condition kept whole above the conjuncts moved does not move them again.
    /// </para>
    /// <para>
    /// One instance for every container, as Calcite's own rules are registered once whatever registers them.
    /// </para>
    /// </remarks>
    public class CosmosFilterCorrelateRule : RelOptRule
    {

        /// <summary>
        /// The rule.
        /// </summary>
        public static CosmosFilterCorrelateRule Instance { get; } = new();

        // As CosmosFilterSplitRule: RelOptRule's operand builders are deprecated in favour of RelRule.Config, whose operand
        // supplier costs more ceremony from C# than it buys. The operands are Calcite's rule's.
#pragma warning disable CS0612
        CosmosFilterCorrelateRule() :
            base(operand((java.lang.Class)typeof(Filter), operand((java.lang.Class)typeof(Correlate), any())), "CosmosFilterCorrelateRule")
        {

        }
#pragma warning restore CS0612

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);
            var correlate = (Correlate)call.rel(1);

            // As Calcite's rule classifies: a conjunct naming a correlation variable stays above, and of the rest each that
            // reads one side alone goes into it, the right side only where the join type lets a filter above reach it.
            var correlated = new java.util.ArrayList();
            var above = new java.util.ArrayList();
            var conjunctions = RelOptUtil.conjunctions(filter.getCondition());
            for (var i = 0; i < conjunctions.size(); i++)
            {
                var conjunct = (RexNode)conjunctions.get(i);
                if (RexUtil.containsCorrelation(conjunct))
                    correlated.add(conjunct);
                else
                    above.add(conjunct);
            }

            var left = new java.util.ArrayList();
            var right = new java.util.ArrayList();
            RelOptUtil.classifyFilters(correlate, above, false, true, correlate.getJoinType().canPushRightFromAbove(), above, left, right);
            above.addAll(correlated);
            if (left.isEmpty() && right.isEmpty())
                return;

            var rexBuilder = correlate.getCluster().getRexBuilder();
            var builder = call.builder();
            var leftRel = builder.push(correlate.getLeft()).filter(left).build();
            var rightRel = builder.push(correlate.getRight()).filter(right).build();

            // What it would move is applied beneath already: moved again, it would stack under itself for ever.
            if (Applied(left, leftRel, correlate.getLeft()) && Applied(right, rightRel, correlate.getRight()))
                return;

            var pushed = correlate.copy(correlate.getTraitSet(), com.google.common.collect.ImmutableList.of(leftRel, rightRel));
            call.getPlanner().onCopy(correlate, pushed);
            if (left.isEmpty() == false)
                call.getPlanner().onCopy(filter, leftRel);
            if (right.isEmpty() == false)
                call.getPlanner().onCopy(filter, rightRel);

            var types = RelOptUtil.getFieldTypeList(pushed.getRowType());

            // What Calcite evaluates of the condition over the rows the correlate returns, each conjunct moved true on all of
            // them. Where that is the rest as written, it is Calcite's rule's own filter.
            var own = Metadata.CosmosFilterStatement.ConjunctsOf(filter.getCondition());
            var moved = own.Select(conjunct => above.contains(conjunct) == false).ToList();
            var written = RexUtil.composeConjunction(rexBuilder, java.util.Arrays.asList(own.Where((_, i) => moved[i] == false).ToArray()));
            var rest = Sql.CosmosOperators.HoldsServiceForm(filter.getCondition()) ? written : CosmosFilterSplitRule.Residual(rexBuilder, filter.getCondition(), moved, null);

            if (rest.Equals(written))
                call.transformTo(builder.push(pushed).filter(RexUtil.fixUp(rexBuilder, above, types)).build());
            else if (rest.isAlwaysTrue())
                call.transformTo(pushed);
            else
                // Written as it is, not through the builder, whose simplification makes a condition kept whole because it reads
                // as false an empty relation, which Calcite's rules read beside a join where Calcite in process holds the condition.
                call.transformTo(LogicalFilter.create(pushed, (RexNode)RexUtil.fixUp(rexBuilder, java.util.Collections.singletonList(rest), types).get(0)));
        }

        /// <summary>
        /// Determines whether what a side is given of the conjuncts moved into it, the filter the builder writes over it, is
        /// applied beneath it already by a filter of any equivalent form; nothing given is.
        /// </summary>
        static bool Applied(java.util.List conjuncts, RelNode given, RelNode side)
        {
            if (conjuncts.isEmpty())
                return true;

            if (given is not Filter written)
                return false;

            var applied = new HashSet<RexNode>(CosmosFilterSplitRule.AlreadyApplied(side));
            return Metadata.CosmosFilterStatement.ConjunctsOf(written.getCondition()).All(applied.Contains);
        }

    }

}
