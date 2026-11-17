using System.Collections.Generic;
using System.Linq;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Calcite's <c>FILTER_CORRELATE</c>, written as its rule writes it, where every conjunct of Calcite's simplification of
    /// what that rule leaves above the correlate after its last firing is a conjunct of Calcite's simplification of the
    /// whole condition, and its firings over a side that is a correlate are taken alike; elsewhere, and where the whole
    /// reads as false, it moves nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A predicate over a traversed element is written above the traversal, where <see cref="CosmosUnnestRule"/> cannot see
    /// it; moved into the correlate it sits between the traversal and the uncollect, which that rule renders as a
    /// <c>WHERE</c> over the alias, and a conjunct of the document alone goes into the request.
    /// </para>
    /// <para>
    /// A condition holding the service's form (<see cref="Sql.CosmosOperators.HoldsServiceForm"/>) moves as Calcite's rule
    /// moves it.
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
            var (above, left, right) = Classify(correlate, filter.getCondition());
            if (left.isEmpty() && right.isEmpty())
                return;

            var rexBuilder = correlate.getCluster().getRexBuilder();
            if (Sql.CosmosOperators.HoldsServiceForm(filter.getCondition()) == false && Transposes(rexBuilder, correlate, filter.getCondition(), above, left, right) == false)
                return;

            // Calcite's rule, as it writes it.
            var builder = call.builder();
            var leftRel = builder.push(correlate.getLeft()).filter(left).build();
            var rightRel = builder.push(correlate.getRight()).filter(right).build();
            var pushed = correlate.copy(correlate.getTraitSet(), com.google.common.collect.ImmutableList.of(leftRel, rightRel));
            call.getPlanner().onCopy(correlate, pushed);
            if (left.isEmpty() == false)
                call.getPlanner().onCopy(filter, leftRel);
            if (right.isEmpty() == false)
                call.getPlanner().onCopy(filter, rightRel);

            builder.push(pushed);
            builder.filter(RexUtil.fixUp(rexBuilder, above, RelOptUtil.getFieldTypeList(builder.peek().getRowType())));
            call.transformTo(builder.build());
        }

        /// <summary>
        /// Classifies a condition's conjuncts as Calcite's rule does: one naming a correlation variable stays above, and of the
        /// rest each that reads one side alone goes into it, the right side only where the join type lets a filter above reach
        /// it.
        /// </summary>
        static (java.util.List Above, java.util.List Left, java.util.List Right) Classify(Correlate correlate, RexNode condition)
        {
            var correlated = new java.util.ArrayList();
            var above = new java.util.ArrayList();
            var conjunctions = RelOptUtil.conjunctions(condition);
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
            return (above, left, right);
        }

        /// <summary>
        /// Determines whether Calcite's transposition is taken: Calcite's simplification of the whole condition, unknown read
        /// as false, reads it as false neither as written nor as Calcite's builder writes it; every conjunct of that
        /// simplification of what Calcite's rule leaves above after its last firing is a conjunct of that simplification of
        /// the whole as the builder writes it, compared as Calcite compares expressions; and where a firing moves conjuncts
        /// into a side that is itself a correlate, the transposition of the filter it writes there is taken too.
        /// </summary>
        /// <remarks>
        /// The whole is read as Calcite's builder writes it, the form in which a later firing reads what the one before left,
        /// and a side's filter as Calcite's rule fires on it next, so each firing over the filters this one writes decides
        /// as this one does.
        /// </remarks>
        static bool Transposes(RexBuilder rexBuilder, Correlate correlate, RexNode condition, java.util.List above, java.util.List left, java.util.List right, int depth = 0)
        {
            var simplify = new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, RexUtil.EXECUTOR);
            if (depth == Firings || simplify.simplifyUnknownAsFalse(condition).isAlwaysFalse())
                return false;

            var whole = simplify.simplifyFilterPredicates(java.util.Collections.singletonList(condition));
            if (whole is null || simplify.simplifyUnknownAsFalse(whole).isAlwaysFalse())
                return false;

            // Each firing's builder writes what is left above as Calcite's RelBuilder does, and the next firing moves what of
            // that reads one side; a rest the builder writes as false, or firings past the bound, move nothing.
            var rest = simplify.simplifyFilterPredicates(above);
            for (var firing = 1; ; firing++)
            {
                if (Side(rexBuilder, simplify, correlate.getLeft(), left, depth) == false || Side(rexBuilder, simplify, correlate.getRight(), right, depth) == false)
                    return false;

                if (rest is null)
                    return false;

                (above, left, right) = Classify(correlate, rest);
                if (left.isEmpty() && right.isEmpty())
                    break;

                if (firing == Firings)
                    return false;

                rest = simplify.simplifyFilterPredicates(above);
            }

            var read = new HashSet<RexNode>(Conjuncts(simplify.simplifyUnknownAsFalse(whole)));
            return Conjuncts(simplify.simplifyUnknownAsFalse(rest)).All(read.Contains);
        }

        /// <summary>
        /// Determines whether the filter a firing writes over a side, of the conjuncts it moves there, leaves that side as
        /// Calcite's rule would: where a member of the side is a correlate, its transposition of that filter is taken, or
        /// moves nothing; a filter the builder writes as false is none.
        /// </summary>
        static bool Side(RexBuilder rexBuilder, RexSimplify simplify, RelNode side, java.util.List conjuncts, int depth)
        {
            if (conjuncts.isEmpty() || Rel.CosmosPlanMembers.Of(side).OfType<Correlate>().FirstOrDefault() is not Correlate inner)
                return true;

            var written = simplify.simplifyFilterPredicates(conjuncts);
            if (written is null)
                return true;

            var (above, left, right) = Classify(inner, written);
            return (left.isEmpty() && right.isEmpty()) || Sql.CosmosOperators.HoldsServiceForm(written) || Transposes(rexBuilder, inner, written, above, left, right, depth + 1);
        }

        /// <summary>
        /// How many of Calcite's rule's firings over one filter, or correlates within one another, are followed before nothing
        /// is moved.
        /// </summary>
        const int Firings = 16;

        /// <summary>
        /// Returns a condition's conjuncts as Calcite reads them, what it calls always true left out.
        /// </summary>
        static IEnumerable<RexNode> Conjuncts(RexNode condition)
        {
            var conjunctions = RelOptUtil.conjunctions(condition);
            for (var i = 0; i < conjunctions.size(); i++)
                yield return (RexNode)conjunctions.get(i);
        }

    }

}
