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
    /// plans, at every correlate the planner's rules carry it onto where that rule would move a part of it, would be
    /// transposed by its test; and nowhere in a statement where one would not.
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
    /// One instance for every container, as Calcite's own rules are registered once whatever registers them. A statement's
    /// answer is kept with the Volcano planner planning it, for the planner's root and the number of its rules, and worked
    /// out again where either changes (<see cref="Statements"/>).
    /// </para>
    /// </remarks>
    public class CosmosFilterCorrelateRule : RelOptRule
    {

        /// <summary>
        /// The rule.
        /// </summary>
        public static CosmosFilterCorrelateRule Instance { get; } = new();

        /// <summary>
        /// Each Volcano planner's statement, as its root and the number of rules it held, whether it takes Calcite's
        /// transposition, and the planner runs, the questions and the times it took to work that out; weak on the planner,
        /// which dies with the statement.
        /// </summary>
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RelOptPlanner, Answered> Statements = new();

        /// <summary>
        /// A statement's answer: the root and the number of rules it was read under, the answer, and how many planner runs
        /// carried conjuncts past a member, how many questions its tests asked the facts graph and how many times the answer
        /// was worked out, over the planner's life.
        /// </summary>
        sealed record Answered(RelNode Root, int Rules, bool Transposes, long Carries, int Questions, int Times);

        /// <summary>
        /// Returns how many times the planner's rules were run over a member to carry conjuncts past it, how many questions
        /// the statement's tests asked the facts graph, and how many times the statement's answer was worked out, for the
        /// statements a planner planned.
        /// </summary>
        internal static (long Carries, int Questions, int Times) Work(RelOptPlanner planner) =>
            Statements.TryGetValue(planner, out var answered) ? (answered.Carries, answered.Questions, answered.Times) : (0, 0, 0);

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
        /// Read off the planner's root as registered, the tree the query was converted to, once for a Volcano planner's root
        /// and rules and kept with the planner, so every firing over one statement answers alike; a planner with no root is
        /// read from the filter, and another planner, whose root is the tree as it now stands, at each firing.
        /// </remarks>
        static bool Statement(RelOptPlanner planner, RexBuilder rexBuilder, Filter filter)
        {
            var root = planner.getRoot();
            if (root is null || planner is not VolcanoPlanner)
                return Statement(new Walk(rexBuilder, planner), root ?? filter);

            var rules = planner.getRules().size();
            Statements.TryGetValue(planner, out var known);
            if (known is not null && ReferenceEquals(known.Root, root) && known.Rules == rules)
                return known.Transposes;

            var walk = new Walk(rexBuilder, planner);
            var transposes = Statement(walk, root);
            Statements.AddOrUpdate(planner, new Answered(root, rules, transposes, (known?.Carries ?? 0) + walk.Carries, (known?.Questions ?? 0) + walk.Questions, (known?.Times ?? 0) + 1));
            return transposes;
        }

        /// <summary>
        /// Walks the tree beneath a root, asking each filter's conjuncts, and each join's condition, of the correlates they
        /// reach.
        /// </summary>
        static bool Statement(Walk walk, RelNode root)
        {
            var seen = CosmosPlanMembers.NewSeen();
            var pending = new Stack<RelNode>();
            pending.Push(Original(root));
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (seen.Add(node) == false)
                    continue;

                if (node is Filter own && walk.Reaches(own.getInput(), RelOptUtil.conjunctions(own.getCondition()), false, 0, CosmosPlanMembers.NewSeen(), true) == false)
                    return false;

                // A join's own condition, which the planner's rules may carry into its inputs as a filter.
                if (node is Join && walk.Carried(node, new java.util.ArrayList(), false).Any(c => walk.Reaches(c.Input, c.Conjuncts, false, 0, CosmosPlanMembers.NewSeen(), true) == false))
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
        const int CarriesBound = 64;

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
        /// <remarks>
        /// Each question is asked once over a statement (<see cref="_reached"/>), and each member's carry once for one set of
        /// conjuncts (<see cref="_carried"/>), so the work grows with the members and the sets of conjuncts reaching each,
        /// not with the paths down to it.
        /// </remarks>
        sealed class Walk
        {

            readonly RexBuilder _rexBuilder;
            readonly RexSimplify _simplify;
            readonly java.util.List _carriers;
            readonly bool _merges;
            readonly Dictionary<Asked, bool> _reached = new();
            readonly Dictionary<Asked, List<(RelNode Input, java.util.List Conjuncts)>> _carried = new();
            int _cuts;

            /// <summary>
            /// Gets how many times the planner's rules were run over a member to carry conjuncts past it.
            /// </summary>
            public long Carries { get; private set; }

            /// <summary>
            /// Gets how many of the walk's tests asked the facts graph something the transposition newly evaluates.
            /// </summary>
            public int Questions { get; private set; }

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
            /// Determines whether Calcite's transposition is taken: where the filter a firing writes into a side reaches a
            /// correlate, the transposition of it there is taken too; and the facts graph answers that nothing the transposition
            /// newly evaluates raises on a document the declaration admits (<see cref="RaisesNothing"/>). That is every
            /// conjunct of what each firing leaves above, as Calcite's builder writes it and read unknown as false, that
            /// Calcite's simplification of the whole condition, unknown read as false, lacks, compared as Calcite compares
            /// expressions; and where Calcite reads the whole as false, as written or as its builder writes it, or the
            /// builder writes a rest as false, every part each firing moves into either side.
            /// </summary>
            /// <remarks>
            /// <para>
            /// That simplification of the whole is what Calcite's Calc above the correlate evaluates without the transposition,
            /// every operand of it; a conjunct of it evaluated above on the rows the transposed correlate joins is evaluated on
            /// rows that plan joins too. Where the whole is read as false that plan evaluates none of it, so each part moved is
            /// new; elsewhere a part moves as Calcite's rule moves it, on every row its side reads. A literal is no term to
            /// ask, and the set is joined unsimplified, so a <c>FALSE</c> in it cannot read the rest away.
            /// </para>
            /// <para>
            /// The whole is read as Calcite's builder writes it, the form in which a later firing reads what the one before left,
            /// and a side's filter as Calcite's rule fires on it next, where the planner's rules carry it, so each firing over the
            /// filters this one writes decides as this one does. The bounds on firings and depth decline.
            /// </para>
            /// </remarks>
            public bool Transposes(Correlate correlate, RexNode condition, java.util.List above, java.util.List left, java.util.List right, int depth, bool merging)
            {
                if (depth == Firings)
                    return false;

                var calc = _simplify.simplifyUnknownAsFalse(condition);
                var whole = _simplify.simplifyFilterPredicates(java.util.Collections.singletonList(condition));
                var readFalse = calc.isAlwaysFalse() || whole is null || _simplify.simplifyUnknownAsFalse(whole).isAlwaysFalse();

                // Each firing's builder writes what is left above as Calcite's RelBuilder does, and the next firing moves what of
                // that reads one side; a rest the builder writes as false moves nothing, and firings past the bound decline.
                var shift = correlate.getLeft().getRowType().getFieldCount();
                var parts = new List<RexNode>();
                var rests = new List<RexNode>();
                var rest = _simplify.simplifyFilterPredicates(above);
                for (var firing = 1; ; firing++)
                {
                    if (Reaches(correlate.getLeft(), left, true, depth, CosmosPlanMembers.NewSeen(), merging) == false || Reaches(correlate.getRight(), right, true, depth, CosmosPlanMembers.NewSeen(), merging) == false)
                        return false;

                    for (var i = 0; i < left.size(); i++)
                        parts.Add((RexNode)left.get(i));
                    for (var i = 0; i < right.size(); i++)
                        parts.Add(RexUtil.shift((RexNode)right.get(i), shift));

                    if (rest is null)
                    {
                        readFalse = true;
                        break;
                    }

                    rests.AddRange(Conjuncts(_simplify.simplifyUnknownAsFalse(rest)));
                    (above, left, right) = Classify(correlate, rest);
                    if (left.isEmpty() && right.isEmpty())
                        break;

                    if (firing == Firings)
                        return false;

                    rest = _simplify.simplifyFilterPredicates(above);
                }

                var evaluated = new HashSet<RexNode>(Conjuncts(calc));
                var asked = (readFalse ? parts : Enumerable.Empty<RexNode>()).Concat(rests.Where(r => evaluated.Contains(r) == false))
                    .Where(r => r is not RexLiteral || (r.isAlwaysTrue() == false && r.isAlwaysFalse() == false))
                    .Distinct()
                    .ToList();

                return asked.Count == 0 || RaisesNothing(correlate, asked);
            }

            /// <summary>
            /// Asks the facts graph whether anything given raises on a document the declaration admits, each a term over the
            /// correlate's row, read through the binding that names the element a traversal yields and a lateral's accessor
            /// columns (<see cref="CosmosImplementor.TryBindElements"/>); a row that binds no container may raise.
            /// </summary>
            /// <remarks>
            /// The terms are joined by an <c>AND</c> as given, never simplified, and the statement read from it is the
            /// question's alone: its binding names paths no request may spell, and nothing renders it.
            /// </remarks>
            bool RaisesNothing(Correlate correlate, List<RexNode> asked)
            {
                Questions++;
                if (CosmosImplementor.TryBindElements(correlate, out var fields, out var readings, out var container) == false)
                    return false;

                var terms = new java.util.ArrayList();
                foreach (var term in asked)
                    terms.add(term);

                var condition = asked.Count == 1 ? asked[0] : _rexBuilder.makeCall(org.apache.calcite.sql.fun.SqlStdOperatorTable.AND, terms);
                return Metadata.CosmosFilterStatement.Of(condition, fields, readings, container, _rexBuilder).RaisesNothing;
            }

            /// <summary>
            /// Follows conjuncts down from a node, as registered, to the correlates the planner's rules carry them onto, and
            /// determines whether each transposes them there: at a correlate, <see cref="Transposes"/>; at a filter, theirs
            /// alone and, where the planner's rules merge filters and <paramref name="merging"/> says they still may, the two
            /// together; past any other member, the filter the planner's rules write beneath it as they write it
            /// (<see cref="Carried"/>), and a member they carry nothing past stops the walk. A filter the builder writes as
            /// false, which <paramref name="written"/> says it may, is none. <paramref name="path"/> holds the members on the
            /// way down, which keeps the search finite.
            /// </summary>
            /// <remarks>
            /// A filter beneath is asked its own conjuncts where the statement walks to it, not again for each filter above.
            /// Where filters merge, conjuncts carried past a filter without it reach what is beneath only once that filter has
            /// gone into the correlate beneath it, whose sides then hold its parts, so they meet any filter further down with
            /// those parts, which the two merged are asked: alone, they merge with none. An answer is kept where no member on
            /// the way was cut short, which in a tree none is.
            /// </remarks>
            public bool Reaches(RelNode node, java.util.List conjuncts, bool written, int depth, HashSet<RelNode> path, bool merging)
            {
                if (conjuncts.isEmpty())
                    return true;

                var asked = new Asked(Original(node), new java.util.ArrayList(conjuncts), written, depth, merging && _merges);
                if (_reached.TryGetValue(asked, out var known))
                    return known;

                var cuts = _cuts;
                var reached = Reach(asked, path);
                if (_cuts == cuts)
                    _reached[asked] = reached;

                return reached;
            }

            /// <summary>
            /// Answers one question of <see cref="Reaches"/>.
            /// </summary>
            bool Reach(Asked asked, HashSet<RelNode> path)
            {
                var (member, conjuncts, written, depth, merging) = asked;
                if (member is Correlate correlate)
                {
                    var condition = written ? _simplify.simplifyFilterPredicates(conjuncts) : RexUtil.composeConjunction(_rexBuilder, conjuncts);
                    if (condition is null)
                        return true;

                    var (above, left, right) = Classify(correlate, condition);
                    return (left.isEmpty() && right.isEmpty()) || Sql.CosmosOperators.HoldsServiceForm(condition) || Transposes(correlate, condition, above, left, right, depth + 1, merging);
                }

                if (path.Add(member) == false)
                {
                    _cuts++;
                    return true;
                }

                try
                {
                    if (member is Filter filter)
                    {
                        if (Reaches(filter.getInput(), conjuncts, written, depth, path, false) == false)
                            return false;

                        if (merging == false)
                            return true;

                        var merged = new java.util.ArrayList(conjuncts);
                        merged.addAll(RelOptUtil.conjunctions(filter.getCondition()));
                        return Reaches(filter.getInput(), merged, true, depth, path, true);
                    }

                    return Carried(member, conjuncts, written).All(c => Reaches(c.Input, c.Conjuncts, false, depth, path, merging));
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
            public List<(RelNode Input, java.util.List Conjuncts)> Carried(RelNode member, java.util.List conjuncts, bool written)
            {
                var asked = new Asked(member, new java.util.ArrayList(conjuncts), written, 0, false);
                if (_carried.TryGetValue(asked, out var known))
                    return known;

                var carried = new List<(RelNode Input, java.util.List Conjuncts)>();
                _carried[asked] = carried;

                var inputs = member.getInputs();
                if (_carriers.isEmpty() || inputs.isEmpty())
                    return carried;

                var cluster = member.getCluster();
                var leaves = new java.util.ArrayList();
                for (var i = 0; i < inputs.size(); i++)
                    leaves.add(new Leaf(cluster, ((RelNode)inputs.get(i)).getRowType(), i));

                RelNode root = member.copy(member.getTraitSet(), leaves);
                if (conjuncts.isEmpty() == false)
                {
                    var condition = written ? _simplify.simplifyFilterPredicates(conjuncts) : RexUtil.composeConjunction(_rexBuilder, conjuncts);
                    if (condition is null)
                        return carried;

                    root = LogicalFilter.create(root, condition);
                }

                Carries++;
                var hep = new HepPlanner(new HepProgramBuilder().addMatchLimit(CarriesBound).addRuleCollection(_carriers).build());
                hep.setRoot(root);

                var over = new Dictionary<int, java.util.List>();
                Over(hep.findBestExp(), over, CosmosPlanMembers.NewSeen());
                foreach (var (ordinal, landed) in over)
                    carried.Add(((RelNode)inputs.get(ordinal), landed));

                return carried;
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
        /// A question of the walk: conjuncts at a member as registered, whether the builder writes them, at what depth of
        /// firings, and whether they may still merge with a filter beneath; the member compared by reference and the
        /// conjuncts as Calcite compares expressions, in order.
        /// </summary>
        readonly record struct Asked(RelNode Member, java.util.List Conjuncts, bool Written, int Depth, bool Merging)
        {

            /// <inheritdoc />
            public bool Equals(Asked other) =>
                ReferenceEquals(Member, other.Member) && Written == other.Written && Depth == other.Depth && Merging == other.Merging && Conjuncts.Equals(other.Conjuncts);

            /// <inheritdoc />
            public override int GetHashCode() =>
                System.HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Member), Conjuncts.GetHashCode(), Written, Depth, Merging);

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
