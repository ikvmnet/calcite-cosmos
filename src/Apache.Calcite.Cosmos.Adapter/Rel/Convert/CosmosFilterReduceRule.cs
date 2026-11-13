using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.plan.hep;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Replaces a filter that keeps no document and fails on none with a relation holding no rows, and one that keeps every
    /// document with its input: what the oracle says of the filter's conjuncts and the container's declaration together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rewrite reaches the constant; this is what makes it a plan.</b> <see cref="Metadata.CosmosFactRewriter"/>
    /// answers <c>FALSE</c> where no document the declaration admits makes the WHERE true and none makes anything in its
    /// gate raise (<see cref="Cosmos.Facts.OracleLicence.Reduce"/>), so that no rows is Calcite's own answer, and
    /// <c>TRUE</c> where every conjunct is one the rest and the declaration imply, deleted one at a time. A converted
    /// filter carrying <c>FALSE</c> is a round trip for no rows, and one carrying <c>TRUE</c> a <c>WHERE</c> that
    /// restricts nothing. <c>CoreRules.FILTER_REDUCE_EXPRESSIONS</c> matches the logical filter, and the constant exists
    /// only after the rewrite. A condition Calcite itself reads as never true is reduced too: <c>(x IS NULL) IS NULL</c>
    /// tests for null what its type says never is, and is the constant whatever the declaration.
    /// </para>
    /// <para>
    /// <b>The answer is <see cref="CosmosNoRows"/>, not an empty <c>Values</c>.</b> Both send nothing; but a host's
    /// prune-empty rules read an empty <c>Values</c> as empty, and answered a join, set operation, semi-join or lateral
    /// beside it without reading the other side, which Calcite reads, and fails on where something in it fails.
    /// </para>
    /// <para>
    /// <b>Only a logical filter, over this convention's own container.</b> A converted filter's condition has been
    /// rewritten already, under the declaration of the convention that converted it, and a filter in a host's
    /// convention is not this rule's. When a conversion could still reach another container's filter, the constant
    /// read off it was a reading of other documents, and a filter over one container was answered with no rows beside
    /// a statement over another (<see cref="CosmosConvention.Owns"/>).
    /// </para>
    /// <para>
    /// <b>The gate is the filters beneath as Calcite evaluates them.</b> No rows stops the evaluation of everything
    /// beneath the filter, so the reduce is gated on the filters stacked beneath it as well as its own condition.
    /// Calcite's calc pass merges a run of filters, and the projections between them, into one condition, the filter
    /// beneath first, and simplifies it (<c>RexSimplify</c> over the input's pulled-up predicates, of <c>IS TRUE</c> of the
    /// conjunction) before it evaluates a row; where that is <c>FALSE</c> nothing in it is evaluated and Calcite answers
    /// no rows. So a run is composed onto its lowest filter's input and simplified by Calcite's own simplifier, configured
    /// as that pass configures it, and the gate holds what is left. The run the filter heads is assumed as well, since
    /// every row the filter reads passed it. A grouping whose keys alone the conditions above it read is walked through,
    /// since <c>FILTER_AGGREGATE_TRANSPOSE</c> moves them beneath it before the calc pass; and a run whose lowest filter
    /// reads a correlate is split as <c>FILTER_CORRELATE</c> splits it, what it pushes into each side simplified apart.
    /// A run beneath a node no calc merges across (a join, a correlate, a window) is gated beside it, through its own
    /// binding. The decision is the facts graph's; this builds the term.
    /// </para>
    /// <para>
    /// <b>Only filters the query wrote, each read once.</b> A filter is read the first time the planner shows it, and
    /// so ever after, the match and a merge's firing again included. For a filter of the tree the planner was handed,
    /// that is before any rule has fired, so every filter beneath it is one the query wrote, a deletion's among them,
    /// though the deletion later merges its set away. A filter a rule made (transposed, pushed or inferred) holds a
    /// condition Calcite's evaluation of the query need not, and conjoining one could make the gate say nothing raises
    /// where Calcite raises: for a filter first shown after the tree, nothing is simplified or assumed, and each filter
    /// beneath it is gated as written. A window is computed over every row beneath it and binds nothing, so a filter
    /// over one is never reduced
    /// (<see cref="CosmosImplementor.TryBindOutput(RelNode?, out IReadOnlyList{CosmosPath?}, out IReadOnlyList{CosmosReading}, out IReadOnlyList{CosmosOrdering}, out CosmosClauses, out Metadata.CosmosContainerMetadata?)"/>).
    /// </para>
    /// </remarks>
    public class CosmosFilterReduceRule : RelOptRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule reduces filters over.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosFilterReduceRule Create(CosmosConvention convention)
        {
            return new CosmosFilterReduceRule(convention);
        }

        readonly CosmosConvention _convention;

        /// <summary>
        /// What each filter read the first time the planner showed it: its rewritten condition, or <c>null</c>.
        /// </summary>
        readonly ConditionalWeakTable<Filter, StrongBox<RexNode?>> _read = new();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule reduces filters over.</param>
        // As CosmosFilterSplitRule: RelOptRule's operand builders are deprecated in favour of RelRule.Config, whose
        // operand supplier costs more ceremony from C# than it buys.
#pragma warning disable CS0612
        public CosmosFilterReduceRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(Filter), Convention.NONE, any()),
                "CosmosFilterReduceRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);

            if (CosmosFilterSplitRule.FindTable(filter.getInput(), throughGroupings: true) is not CosmosTable table || ReferenceEquals(table.Convention, _convention) == false)
                return false;

            return Read(filter, call.getPlanner()) is RexNode rewritten && (KeepsNothing(rewritten) || KeepsEverything(rewritten));
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);

            switch (Read(filter, call.getPlanner()))
            {
                // No document satisfies the filter and none fails it, so the relation holds no rows, and no request at all
                // is what answers it.
                case RexNode rewritten when KeepsNothing(rewritten):
                    call.transformTo(CosmosNoRows.Create(filter.getCluster(), filter.getRowType(), call.getMetadataQuery().getRowCount(filter)?.doubleValue() ?? 1d, filter.getId()));
                    break;

                // Every conjunct is one the rest of the filter and the declaration make true of every document: the
                // filter is its input, and the statement needs no WHERE.
                case RexNode rewritten when KeepsEverything(rewritten):
                    call.transformTo(filter.getInput());
                    break;
            }
        }

        /// <summary>
        /// Determines whether a rewritten condition keeps no row: the oracle's <c>FALSE</c>, or a condition Calcite reads
        /// as never true on its own — <c>(x IS NULL) IS NULL</c>, a null test of what its type says is never null, which
        /// keeps nothing whatever the declaration says.
        /// </summary>
        static bool KeepsNothing(RexNode rewritten) => rewritten.isAlwaysFalse();

        /// <summary>
        /// Determines whether a rewritten condition keeps every row: every conjunct deleted, which the rewrite answers
        /// with <c>TRUE</c>.
        /// </summary>
        static bool KeepsEverything(RexNode rewritten) => rewritten is RexLiteral literal && literal.isAlwaysTrue();

        /// <summary>
        /// The filter's condition as <see cref="Rewritten"/> read it the first time the planner showed the filter.
        /// </summary>
        RexNode? Read(Filter filter, RelOptPlanner planner) =>
            _read.GetValue(filter, f => new StrongBox<RexNode?>(Rewritten(f, Written(planner)))).Value;

        /// <summary>
        /// Determines whether a Volcano planner is still being handed the query's tree, before any rule has fired: it has
        /// no root yet, or only one a <c>clear</c> left behind.
        /// </summary>
        static bool Written(RelOptPlanner planner) =>
            planner is VolcanoPlanner volcano
            && (volcano.getRoot() is not RelSubset root || root.getOriginal() is not RelNode original || volcano.isRegistered(original) == false);

        /// <summary>
        /// The filter's condition as the filter rule pushes it, rewritten under this convention's declaration, its reduce
        /// gated on the filters beneath it; or <c>null</c> where something it reads has no binding.
        /// </summary>
        RexNode? Rewritten(Filter filter, bool written)
        {
            var condition = filter.getCondition();
            var rexBuilder = filter.getCluster().getRexBuilder();

            var runs = new List<(RexNode Condition, IReadOnlyList<CosmosPath?> Fields, IReadOnlyList<CosmosReading> Readings, bool Reaching)>();
            if (Beneath(filter, written, rexBuilder, runs) == false)
                return null;

            // A grouping that computes binds nothing above it, so its keys are read beneath it, in the run the filter heads
            // (Run): the WHERE is then read through no binding, the run being what the reduce asks.
            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out _) == false)
            {
                if (runs.Exists(r => r.Reaching) == false)
                    return null;

                fields = new CosmosPath?[filter.getInput().getRowType().getFieldCount()];
                readings = System.Array.Empty<CosmosReading>();
            }

            if (runs.Count == 0)
                return Metadata.CosmosFactRewriter.Rewrite(condition, fields, _convention.Container, CosmosImplementor.DefaultRootAlias, rexBuilder, readings);

            // Each run read through its own binding, set after the filter's so that the condition's ordinals stay its own:
            // the gate is the conjunction over the bindings side by side, the WHERE among it where no run the filter heads
            // holds it merged.
            var gateFields = new List<CosmosPath?>(fields);
            var gateReadings = new List<CosmosReading>(Padded(readings, fields.Count));
            var gate = new java.util.ArrayList();
            RexNode? reaching = null;

            if (runs.Exists(r => r.Reaching) == false)
                gate.add(condition);

            foreach (var (below, belowFields, belowReadings, isReaching) in runs)
            {
                var shifted = RexUtil.shift(below, gateFields.Count);
                gate.add(shifted);
                if (isReaching)
                    reaching = shifted;

                gateFields.AddRange(belowFields);
                gateReadings.AddRange(Padded(belowReadings, belowFields.Count));
            }

            return Metadata.CosmosFactRewriter.Rewrite(condition, gateFields, _convention.Container, CosmosImplementor.DefaultRootAlias, rexBuilder, gateReadings,
                gate.size() == 1 ? (RexNode)gate.get(0) : rexBuilder.makeCall(SqlStdOperatorTable.AND, gate), reaching);
        }

        /// <summary>
        /// Collects what a filter's reduce is gated on: the run it heads, and each run beneath the node that ends it, with
        /// the binding of the run's lowest filter's input; <c>false</c> where one has no binding to read it through.
        /// </summary>
        static bool Beneath(Filter filter, bool written, RexBuilder rexBuilder, List<(RexNode, IReadOnlyList<CosmosPath?>, IReadOnlyList<CosmosReading>, bool)> runs)
        {
            var seen = new HashSet<RelNode>(ReferenceEqualityComparer.Instance);
            return Run(filter, reaching: true) is { } under && Visit(under);

            bool Visit(RelNode? at)
            {
                at = Resolve(at);
                if (at is null || seen.Add(at) == false)
                    return true;

                if (at is Filter below)
                    return Run(below, reaching: false) is { } under && Visit(under);

                var inputs = at.getInputs();
                for (var i = 0; i < inputs.size(); i++)
                    if (Visit((RelNode)inputs.get(i)) == false)
                        return false;

                return true;
            }

            // A run: the filter, then each filter or plain projection beneath it down to the lowest filter, which the calc
            // pass merges into one condition, and each grouping whose keys alone the conditions above it read, beneath which
            // Calcite moves them (FILTER_AGGREGATE_TRANSPOSE). Returns the lowest filter's input, or null where the run has
            // no binding.
            RelNode? Run(Filter top, bool reaching)
            {
                var run = new List<RelNode> { top };
                var lowest = 0;

                // The run's conditions composed onto the node the walk stands on, and as they stood at the lowest filter.
                var conditions = new List<RexNode> { top.getCondition() };
                var composed = conditions;

                var walked = new HashSet<RelNode>(ReferenceEqualityComparer.Instance) { top };
                for (var at = Resolve(top.getInput()); at is not null && walked.Add(at); at = Resolve(at.getInput(0)))
                {
                    if (at is Filter each)
                        composed = conditions = new List<RexNode>(conditions) { each.getCondition() };
                    else if (at is Project project && project.containsOver() == false)
                        conditions = conditions.ConvertAll(condition => RelOptUtil.pushPastProject(condition, project));
                    else if (at is Aggregate aggregate && ThroughKeys(aggregate, conditions, rexBuilder) is { } beneath)
                        conditions = beneath;
                    else
                        break;

                    run.Add(at);
                    if (at is Filter)
                        lowest = run.Count - 1;
                }

                run.RemoveRange(lowest + 1, run.Count - lowest - 1);
                foreach (var node in run)
                    seen.Add(node);

                var input = ((Filter)run[lowest]).getInput();

                // The filter alone heads its run: its WHERE stands for the run.
                if (lowest == 0 && reaching)
                    return input;

                // Read as written: a filter alone, or a run read after a rule fired, whose conditions are no query's to
                // merge, simplify or assume.
                if (written == false || lowest == 0)
                {
                    foreach (var node in run)
                    {
                        if (node is not Filter each || reaching && ReferenceEquals(each, top))
                            continue;

                        if (CosmosImplementor.TryBindOutput(each.getInput(), out var eachFields, out var eachReadings, out _) == false)
                            return null;

                        runs.Add((each.getCondition(), eachFields, eachReadings, false));
                    }

                    return input;
                }

                // A correlate beneath the run, through the plain projections beneath its lowest filter, which
                // FILTER_PROJECT_TRANSPOSE moves the merged filter past, is where FILTER_CORRELATE splits it (AtCorrelate).
                var onto = composed;
                var under = Resolve(input);
                var unwalked = new HashSet<RelNode>(ReferenceEqualityComparer.Instance);
                while (under is Project beneath && beneath.containsOver() == false && unwalked.Add(beneath))
                {
                    onto = onto.ConvertAll(condition => RelOptUtil.pushPastProject(condition, beneath));
                    under = Resolve(beneath.getInput());
                }

                var correlate = under as Correlate;
                var bound = correlate ?? input;
                if (CosmosImplementor.TryBindOutput(bound, out var fields, out var readings, out _) == false)
                    return null;

                // Each condition composed onto the lowest filter's input, then conjoined the filter beneath first, as the
                // calc pass merges them, and simplified as it simplifies the merged condition.
                var merged = new java.util.ArrayList();
                var conjoined = correlate is null ? composed : onto;
                for (var k = conjoined.Count - 1; k >= 0; k--)
                    merged.add(conjoined[k]);

                var simplify = new RexSimplify(rexBuilder, top.getCluster().getMetadataQuery().getPulledUpPredicates(bound), RexUtil.EXECUTOR);
                runs.Add((correlate is null ? simplify.simplify(rexBuilder.makeCall(SqlStdOperatorTable.IS_TRUE, rexBuilder.makeCall(SqlStdOperatorTable.AND, merged))) : AtCorrelate(correlate, merged, simplify, rexBuilder), fields, readings, reaching));

                return input;
            }
        }

        /// <summary>
        /// Returns a run's conditions read through a grouping onto its input, as <c>FILTER_AGGREGATE_TRANSPOSE</c> moves
        /// them beneath it; or <c>null</c> where the grouping is not a simple one with keys, or a condition reads anything
        /// but its keys, which that rule leaves above it.
        /// </summary>
        static List<RexNode>? ThroughKeys(Aggregate aggregate, List<RexNode> conditions, RexBuilder rexBuilder)
        {
            var keys = aggregate.getGroupCount();
            if (keys == 0 || aggregate.getGroupType() != Aggregate.Group.SIMPLE)
                return null;

            // Each key's ordinal moved to the input's field it groups, as that rule's own converter moves it.
            var output = aggregate.getRowType().getFieldList();
            var adjustments = new int[output.size()];
            var j = 0;
            for (var key = aggregate.getGroupSet().iterator(); key.hasNext(); j++)
                adjustments[j] = ((java.lang.Integer)key.next()).intValue() - j;

            var converter = new RelOptUtil.RexInputConverter(rexBuilder, output, aggregate.getInput().getRowType().getFieldList(), adjustments);
            var bits = org.apache.calcite.util.ImmutableBitSet.range(0, keys);

            var beneath = new List<RexNode>(conditions.Count);
            foreach (var condition in conditions)
            {
                if (bits.contains(RelOptUtil.InputFinder.bits(condition)) == false)
                    return null;

                beneath.Add((RexNode)condition.accept(converter));
            }

            return beneath;
        }

        /// <summary>
        /// Returns a merged run whose lowest filter reads a correlate as Calcite evaluates it: <c>FILTER_CORRELATE</c>
        /// pushes each conjunct that reads one side alone into that side, where it is evaluated before anything above, so
        /// what it pushes into each side is simplified apart, as the filter it builds there is, and only what stays above
        /// as the calc pass simplifies it. The parts are conjoined, not simplified together: a part Calcite's simplifier
        /// reads as <c>FALSE</c> stops nothing it pushed beside it.
        /// </summary>
        static RexNode AtCorrelate(Correlate correlate, java.util.List merged, RexSimplify simplify, RexBuilder rexBuilder)
        {
            // As FILTER_CORRELATE classifies: a conjunct holding a correlation variable stays above, the rest by the sides
            // they read and what the correlate's type lets it push.
            var above = new java.util.ArrayList();
            var correlated = new java.util.ArrayList();
            for (var i = 0; i < merged.size(); i++)
            {
                var conjuncts = RelOptUtil.conjunctions((RexNode)merged.get(i));
                for (var k = 0; k < conjuncts.size(); k++)
                {
                    var conjunct = (RexNode)conjuncts.get(k);
                    if (RexUtil.containsCorrelation(conjunct))
                        correlated.add(conjunct);
                    else
                        above.add(conjunct);
                }
            }

            var left = new java.util.ArrayList();
            var right = new java.util.ArrayList();
            RelOptUtil.classifyFilters(correlate, above, false, true, correlate.getJoinType().canPushRightFromAbove(), above, left, right);
            above.addAll(correlated);

            // What it pushes is simplified as the builder it pushes with simplifies a filter, and the right side's read back
            // onto the correlate's own ordinals.
            var alone = new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, RexUtil.EXECUTOR);
            var parts = new java.util.ArrayList();
            foreach (var (part, simplifier, shift) in new[] { (above, simplify, 0), (left, alone, 0), (right, alone, correlate.getLeft().getRowType().getFieldCount()) })
                if (part.isEmpty() == false)
                    parts.add(RexUtil.shift(simplifier.simplify(rexBuilder.makeCall(SqlStdOperatorTable.IS_TRUE, RexUtil.composeConjunction(rexBuilder, part))), shift));

            return parts.size() == 1 ? (RexNode)parts.get(0) : rexBuilder.makeCall(SqlStdOperatorTable.AND, parts);
        }

        /// <summary>
        /// Returns what a planner's wrapper stands for: a set's first expression, which for the query's tree is the one it
        /// wrote, or its best where it has none.
        /// </summary>
        static RelNode? Resolve(RelNode? node) => node switch
        {
            RelSubset subset => subset.getOriginal() ?? subset.getBest(),
            HepRelVertex vertex => vertex.getCurrentRel(),
            _ => node,
        };

        /// <summary>
        /// Returns readings parallel to a binding of the given width, a field the walk left unread read as its type.
        /// </summary>
        static IEnumerable<CosmosReading> Padded(IReadOnlyList<CosmosReading> readings, int width)
        {
            for (var i = 0; i < width; i++)
                yield return i < readings.Count ? readings[i] : CosmosReading.Typed;
        }

    }

}
