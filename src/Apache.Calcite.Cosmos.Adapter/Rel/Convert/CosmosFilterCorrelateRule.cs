using System.Collections.Generic;
using System.Linq;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rex;
using org.apache.calcite.tools;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Moves each conjunct of a filter above a correlate that reads one side alone into that side, as Calcite's
    /// <c>FILTER_CORRELATE</c> does, and leaves above Calcite's simplification of the rest beside them, a conjunct that
    /// simplification would take down to one side as written; where the whole reads as false it moves nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A predicate over a traversed element is written above the traversal, where <see cref="CosmosUnnestRule"/> cannot see
    /// it; moved into the correlate it sits between the traversal and the uncollect, which that rule renders as a
    /// <c>WHERE</c> over the alias, and a conjunct of the document alone goes into the request.
    /// </para>
    /// <para>
    /// What is left above is <see cref="CosmosFilterSplitRule.Residual"/>'s reading of the condition beside the conjuncts
    /// moved, each true on every row the correlate returns, but for a conjunct whose own reading beside them holds a term of
    /// one side, which is kept as written, the rest read beside it too, so that no later firing moves the term to a side
    /// where it is evaluated on documents the correlate returns no row for. For the same reason the conjuncts are written
    /// as they stand where the builder's simplification of Calcite's rule's filter would derive such a term, and into a
    /// side that may itself be a correlate. A condition holding the service's form
    /// (<see cref="Sql.CosmosOperators.HoldsServiceForm"/>) is left as Calcite's rule leaves it, since that reading is of a
    /// condition Calcite evaluates.
    /// </para>
    /// <para>
    /// It terminates: it does not fire where every conjunct it would move is applied beneath its side already, compared as
    /// Calcite compares expressions, so the filter matched again over the correlate it made does not move them again.
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
            var serviceForm = Sql.CosmosOperators.HoldsServiceForm(filter.getCondition());

            // A whole Calcite's simplification reads as false: nothing moves, and Calcite evaluates the condition above the
            // correlate as it does in process, where a conjunct moved into a side would be evaluated there alone.
            if (serviceForm == false && new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, RexUtil.EXECUTOR).simplifyUnknownAsFalse(filter.getCondition()).isAlwaysFalse())
                return;

            // What Calcite evaluates of the condition over the rows the correlate returns, each conjunct moved true on all of
            // them. Where that is the rest as written, it is Calcite's rule's own filter.
            var own = Metadata.CosmosFilterStatement.ConjunctsOf(filter.getCondition());
            var moved = own.Select(conjunct => above.contains(conjunct) == false).ToList();
            var written = RexUtil.composeConjunction(rexBuilder, java.util.Arrays.asList(own.Where((_, i) => moved[i] == false).ToArray()));
            var rest = serviceForm ? written : CosmosFilterSplitRule.Residual(rexBuilder, filter.getCondition(), moved, null);

            // What is left above holds nothing a later firing would move into a side, where it would be evaluated on documents
            // the correlate returns no row for: a conjunct the reading takes down to a term of one side is kept as written.
            if (rest.Equals(written) == false && Movable(correlate, rest))
                rest = Kept(rexBuilder, correlate, filter.getCondition(), own, moved, written);

            var builder = call.builder();
            var leftRel = Side(builder, rexBuilder, correlate.getLeft(), left);
            var rightRel = Side(builder, rexBuilder, correlate.getRight(), right);

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
            if (rest.Equals(written))
            {
                // Calcite's rule's own filter; where the builder's simplification takes a conjunct of both sides down to a term
                // of one side, or the rest to false, the conjuncts as written, so no later firing moves the term.
                var conjuncts = RexUtil.fixUp(rexBuilder, above, types);
                var built = builder.push(pushed).filter(conjuncts).build();
                var derived = built is Filter simplified ? Movable(correlate, simplified.getCondition()) : built != pushed;
                call.transformTo(derived ? LogicalFilter.create(pushed, RexUtil.composeConjunction(rexBuilder, conjuncts)) : built);
            }
            else if (rest.isAlwaysTrue())
                call.transformTo(pushed);
            else
                // Written as it is, not through the builder, whose simplification would rewrite the conjuncts kept as written.
                call.transformTo(LogicalFilter.create(pushed, (RexNode)RexUtil.fixUp(rexBuilder, java.util.Collections.singletonList(rest), types).get(0)));
        }

        /// <summary>
        /// Returns what is left above a correlate where <see cref="CosmosFilterSplitRule.Residual"/>'s reading of the whole
        /// condition holds a term of one side: each conjunct not moved whose own reading beside the conjuncts moved holds such
        /// a term as written, beside the reading of the rest beside both; the conjuncts not moved as written where that holds
        /// one too.
        /// </summary>
        static RexNode Kept(RexBuilder rexBuilder, Correlate correlate, RexNode condition, IReadOnlyList<RexNode> own, IReadOnlyList<bool> moved, RexNode written)
        {
            // A conjunct not moved read beside the conjuncts moved.
            var sent = new HashSet<RexNode>(own.Where((_, i) => moved[i]));
            RexNode Read(int conjunct)
            {
                var read = RexUtil.composeConjunction(rexBuilder, java.util.Arrays.asList(own.Where((_, i) => moved[i] || i == conjunct).ToArray()));
                return CosmosFilterSplitRule.Residual(rexBuilder, read, Metadata.CosmosFilterStatement.ConjunctsOf(read).Select(sent.Contains).ToList(), null);
            }

            // The rest read beside the conjuncts kept as well, which Calcite's simplification of the whole reads beside it: a
            // term a conjunct kept absorbs is read away, as Calcite reads it.
            var kept = Enumerable.Range(0, own.Count).Where(i => moved[i] == false && Movable(correlate, Read(i))).ToHashSet();
            var rest = CosmosFilterSplitRule.Residual(rexBuilder, condition, own.Select((_, i) => moved[i] || kept.Contains(i)).ToList(), null);
            if (Movable(correlate, rest))
                return written;

            return RexUtil.composeConjunction(rexBuilder, java.util.Arrays.asList(own.Where((_, i) => kept.Contains(i)).Append(rest).ToArray()));
        }

        /// <summary>
        /// Returns a side given the conjuncts moved into it: Calcite's rule's filter, which its builder simplifies, but over a
        /// side that may be a correlate, the conjuncts as written, since the builder's simplification may take a conjunct of
        /// both that correlate's sides down to a term of one, which this rule's firing there would move.
        /// </summary>
        static RelNode Side(RelBuilder builder, RexBuilder rexBuilder, RelNode side, java.util.List conjuncts)
        {
            if (conjuncts.isEmpty() == false && Rel.CosmosPlanMembers.Of(side).Any(member => member is Correlate))
                return LogicalFilter.create(side, RexUtil.composeConjunction(rexBuilder, conjuncts));

            return builder.push(side).filter(conjuncts).build();
        }

        /// <summary>
        /// Determines whether a conjunct of a condition left above a correlate is one this rule would move into a side: one
        /// naming no correlation variable that reads one side alone, the right only where the join type lets a filter reach it.
        /// </summary>
        static bool Movable(Correlate correlate, RexNode condition)
        {
            var above = new java.util.ArrayList();
            var conjunctions = RelOptUtil.conjunctions(condition);
            for (var i = 0; i < conjunctions.size(); i++)
                if (RexUtil.containsCorrelation((RexNode)conjunctions.get(i)) == false)
                    above.add(conjunctions.get(i));

            var left = new java.util.ArrayList();
            var right = new java.util.ArrayList();
            RelOptUtil.classifyFilters(correlate, above, false, true, correlate.getJoinType().canPushRightFromAbove(), above, left, right);
            return left.isEmpty() == false || right.isEmpty() == false;
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
