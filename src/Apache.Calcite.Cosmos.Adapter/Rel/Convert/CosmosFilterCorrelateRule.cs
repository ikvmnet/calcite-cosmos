using System.Collections.Generic;
using System.Linq;

using org.apache.calcite.plan;
using org.apache.calcite.plan.hep;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rel.rules;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Calcite's <c>FILTER_CORRELATE</c>, written as its rule writes it, wherever every filter of the statement the planner
    /// plans, at every correlate the planner's rules carry it onto, would be transposed by its test; and nowhere in a
    /// statement where one would not.
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
            if (Sql.CosmosOperators.HoldsServiceForm(filter.getCondition()) == false && Statement(call.getPlanner(), rexBuilder, filter) == false)
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
        /// Determines whether the statement the planner plans takes Calcite's transposition: every filter of it as registered,
        /// and every join's condition, at every correlate the planner's rules carry it onto, is transposed there by
        /// <see cref="Walk.Transposes"/>.
        /// </summary>
        /// <remarks>
        /// Read off the planner's root as registered, the tree the query was converted to, so every firing over one statement
        /// answers alike; a planner with no root is read from the filter.
        /// </remarks>
        static bool Statement(RelOptPlanner planner, RexBuilder rexBuilder, Filter filter)
        {
            var walk = new Walk(rexBuilder, planner);
            var seen = CosmosPlanMembers.NewSeen();
            var pending = new Stack<RelNode>();
            pending.Push(Original(planner.getRoot() ?? filter));
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (seen.Add(node) == false)
                    continue;

                if (node is Filter own && walk.Reaches(own.getInput(), RelOptUtil.conjunctions(own.getCondition()), false, 0, CosmosPlanMembers.NewSeen()) == false)
                    return false;

                // A join's own condition, which the planner's rules may carry into its inputs as a filter.
                if (node is Join && walk.Carried(node, new java.util.ArrayList(), false).Any(c => walk.Reaches(c.Input, c.Conjuncts, false, 0, CosmosPlanMembers.NewSeen()) == false))
                    return false;

                var inputs = node.getInputs();
                for (var i = 0; i < inputs.size(); i++)
                    pending.Push(Original((RelNode)inputs.get(i)));
            }

            return true;
        }

        /// <summary>
        /// Returns the node a registered input stands for as the query was converted: an equivalence set's original, a vertex's
        /// current node, or the node itself.
        /// </summary>
        static RelNode Original(RelNode node) => node switch
        {
            RelSubset subset => subset.getOriginal() ?? node,
            HepRelVertex vertex => vertex.getCurrentRel(),
            _ => node,
        };

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
        /// How many of Calcite's rule's firings over one filter, or correlates within one another, are followed before nothing
        /// is moved.
        /// </summary>
        const int Firings = 16;

        /// <summary>
        /// How many rule applications the planner's rules are given to carry conjuncts past one member.
        /// </summary>
        const int Carries = 64;

        /// <summary>
        /// Returns a condition's conjuncts as Calcite reads them, what it calls always true left out.
        /// </summary>
        static IEnumerable<RexNode> Conjuncts(RexNode condition)
        {
            var conjunctions = RelOptUtil.conjunctions(condition);
            for (var i = 0; i < conjunctions.size(); i++)
                yield return (RexNode)conjunctions.get(i);
        }

        /// <summary>
        /// The test, over one statement: Calcite's simplification and the planner's own rules that carry a filter past a
        /// member.
        /// </summary>
        sealed class Walk
        {

            readonly RexBuilder _rexBuilder;
            readonly RexSimplify _simplify;
            readonly java.util.List _carriers;
            readonly bool _merges;

            /// <summary>
            /// Initializes a new instance, reading the planner's rules that move a filter, or a join's condition, beneath
            /// another member: past a projection, a grouping, a window, a join, a sample, a set operation, a sort or a table
            /// function; and whether one merges a filter into the filter beneath. A correlate is this rule's, and a filter
            /// beneath is read beside the conjuncts as <see cref="Reaches"/> says.
            /// </summary>
            public Walk(RexBuilder rexBuilder, RelOptPlanner planner)
            {
                _rexBuilder = rexBuilder;
                _simplify = new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, RexUtil.EXECUTOR);
                _carriers = new java.util.ArrayList();
                var rules = planner.getRules();
                for (var i = 0; i < rules.size(); i++)
                    if (rules.get(i) is FilterProjectTransposeRule or FilterAggregateTransposeRule or FilterWindowTransposeRule or ProjectToWindowRule.ProjectToLogicalProjectAndWindowRule
                        or FilterJoinRule or FilterSampleTransposeRule or FilterSetOpTransposeRule or FilterSortTransposeRule or FilterTableFunctionTransposeRule)
                        _carriers.add(rules.get(i));
                    else if (rules.get(i) is FilterMergeRule)
                        _merges = true;
            }

            /// <summary>
            /// Determines whether Calcite's transposition is taken: Calcite's simplification of the whole condition, unknown read
            /// as false, reads it as false neither as written nor as Calcite's builder writes it; every conjunct of that
            /// simplification of what Calcite's rule leaves above after its last firing is a conjunct of that simplification of
            /// the whole as the builder writes it, compared as Calcite compares expressions; and where the filter a firing writes
            /// into a side reaches a correlate, the transposition of it there is taken too.
            /// </summary>
            /// <remarks>
            /// The whole is read as Calcite's builder writes it, the form in which a later firing reads what the one before left,
            /// and a side's filter as Calcite's rule fires on it next, where the planner's rules carry it, so each firing over the
            /// filters this one writes decides as this one does.
            /// </remarks>
            public bool Transposes(Correlate correlate, RexNode condition, java.util.List above, java.util.List left, java.util.List right, int depth)
            {
                if (depth == Firings || _simplify.simplifyUnknownAsFalse(condition).isAlwaysFalse())
                    return false;

                var whole = _simplify.simplifyFilterPredicates(java.util.Collections.singletonList(condition));
                if (whole is null || _simplify.simplifyUnknownAsFalse(whole).isAlwaysFalse())
                    return false;

                // Each firing's builder writes what is left above as Calcite's RelBuilder does, and the next firing moves what of
                // that reads one side; a rest the builder writes as false, or firings past the bound, move nothing.
                var rest = _simplify.simplifyFilterPredicates(above);
                for (var firing = 1; ; firing++)
                {
                    if (Reaches(correlate.getLeft(), left, true, depth, CosmosPlanMembers.NewSeen()) == false || Reaches(correlate.getRight(), right, true, depth, CosmosPlanMembers.NewSeen()) == false)
                        return false;

                    if (rest is null)
                        return false;

                    (above, left, right) = Classify(correlate, rest);
                    if (left.isEmpty() && right.isEmpty())
                        break;

                    if (firing == Firings)
                        return false;

                    rest = _simplify.simplifyFilterPredicates(above);
                }

                var read = new HashSet<RexNode>(Conjuncts(_simplify.simplifyUnknownAsFalse(whole)));
                return Conjuncts(_simplify.simplifyUnknownAsFalse(rest)).All(read.Contains);
            }

            /// <summary>
            /// Follows conjuncts down from a node, as registered, to the correlates the planner's rules carry them onto, and
            /// determines whether each transposes them there: at a correlate, <see cref="Transposes"/>; at a filter, that
            /// filter's own transposition, theirs alone and, where the planner's rules merge filters, the transposition of the
            /// two together; past any other member, the filter the planner's rules write beneath it as they write it
            /// (<see cref="Carried"/>), and a member they carry nothing past stops the walk. A filter the builder writes as
            /// false, which <paramref name="written"/> says it may, is none. <paramref name="path"/> holds the members on the
            /// way down, which keeps the search finite.
            /// </summary>
            public bool Reaches(RelNode node, java.util.List conjuncts, bool written, int depth, HashSet<RelNode> path)
            {
                if (conjuncts.isEmpty())
                    return true;

                var member = Original(node);
                if (member is Correlate correlate)
                {
                    var condition = written ? _simplify.simplifyFilterPredicates(conjuncts) : RexUtil.composeConjunction(_rexBuilder, conjuncts);
                    if (condition is null)
                        return true;

                    var (above, left, right) = Classify(correlate, condition);
                    return (left.isEmpty() && right.isEmpty()) || Sql.CosmosOperators.HoldsServiceForm(condition) || Transposes(correlate, condition, above, left, right, depth + 1);
                }

                if (path.Add(member) == false)
                    return true;

                try
                {
                    if (member is Filter filter)
                    {
                        var own = RelOptUtil.conjunctions(filter.getCondition());
                        var merged = new java.util.ArrayList(conjuncts);
                        merged.addAll(own);
                        return Reaches(filter.getInput(), new java.util.ArrayList(own), false, depth, path) && Reaches(filter.getInput(), conjuncts, written, depth, path)
                            && (_merges == false || Reaches(filter.getInput(), merged, true, depth, path));
                    }

                    return Carried(member, conjuncts, written).All(c => Reaches(c.Input, c.Conjuncts, false, depth, path));
                }
                finally
                {
                    path.Remove(member);
                }
            }

            /// <summary>
            /// Returns what the planner's rules carry beneath a member, a filter of the given conjuncts above it, as the builder
            /// writes them where <paramref name="written"/> says so, or none for a join's own condition: the member's inputs
            /// stood in for by leaves, its rules applied as Calcite's planner applies them, and each input given the conjuncts
            /// of the filter left directly over its leaf, as the rule wrote it.
            /// </summary>
            public IEnumerable<(RelNode Input, java.util.List Conjuncts)> Carried(RelNode member, java.util.List conjuncts, bool written)
            {
                var inputs = member.getInputs();
                if (_carriers.isEmpty() || inputs.isEmpty())
                    yield break;

                var cluster = member.getCluster();
                var leaves = new java.util.ArrayList();
                for (var i = 0; i < inputs.size(); i++)
                    leaves.add(new Leaf(cluster, ((RelNode)inputs.get(i)).getRowType(), i));

                RelNode root = member.copy(member.getTraitSet(), leaves);
                if (conjuncts.isEmpty() == false)
                {
                    var condition = written ? _simplify.simplifyFilterPredicates(conjuncts) : RexUtil.composeConjunction(_rexBuilder, conjuncts);
                    if (condition is null)
                        yield break;

                    root = LogicalFilter.create(root, condition);
                }

                var hep = new HepPlanner(new HepProgramBuilder().addMatchLimit(Carries).addRuleCollection(_carriers).build());
                hep.setRoot(root);
                var carried = hep.findBestExp();

                var over = new Dictionary<int, java.util.List>();
                Over(carried, over, CosmosPlanMembers.NewSeen());
                foreach (var (ordinal, landed) in over)
                    yield return ((RelNode)inputs.get(ordinal), landed);
            }

            /// <summary>
            /// Collects, for each leaf in a tree, the conjuncts of the filter directly over it.
            /// </summary>
            static void Over(RelNode node, Dictionary<int, java.util.List> over, HashSet<RelNode> seen)
            {
                if (seen.Add(node) == false)
                    return;

                if (node is Filter filter && filter.getInput() is Leaf leaf)
                    over[leaf.Ordinal] = RelOptUtil.conjunctions(filter.getCondition());

                var inputs = node.getInputs();
                for (var i = 0; i < inputs.size(); i++)
                    Over((RelNode)inputs.get(i), over, seen);
            }

        }

        /// <summary>
        /// A leaf standing for one input of a member while the planner's rules carry a filter past that member.
        /// </summary>
        sealed class Leaf : AbstractRelNode
        {

            readonly RelDataType _rowType;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            public Leaf(RelOptCluster cluster, RelDataType rowType, int ordinal) :
                base(cluster, cluster.traitSetOf(Convention.NONE))
            {
                _rowType = rowType;
                Ordinal = ordinal;
            }

            /// <summary>
            /// Gets the input the leaf stands for.
            /// </summary>
            public int Ordinal { get; }

            /// <inheritdoc />
            protected override RelDataType deriveRowType() => _rowType;

            /// <inheritdoc />
            public override RelWriter explainTerms(RelWriter pw) => base.explainTerms(pw).item("input", java.lang.Integer.valueOf(Ordinal));

        }

    }

}
