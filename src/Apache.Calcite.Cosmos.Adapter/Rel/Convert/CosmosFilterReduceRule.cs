using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Replaces a filter that keeps no document and fails on none with a relation holding no rows, and one that keeps every
    /// document with its input: what the oracle says of the filter's conjuncts and the container's declaration together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rewrite reaches the constant; this is what makes it a plan.</b> <see cref="Metadata.CosmosFactRewriter"/>
    /// answers <c>FALSE</c> where no document the declaration admits makes the WHERE true and none makes anything in it
    /// raise (<see cref="Cosmos.Facts.OracleLicence.Reduce"/>), so that the empty relation is Calcite's own answer, and
    /// <c>TRUE</c> where every conjunct is one the rest and the declaration imply, deleted one at a time. A converted
    /// filter carrying <c>FALSE</c> is a round trip for no rows, and one carrying <c>TRUE</c> a <c>WHERE</c> that
    /// restricts nothing. <c>CoreRules.FILTER_REDUCE_EXPRESSIONS</c>, which makes an always-false filter an empty
    /// <c>Values</c>, matches the logical filter, and the constant exists only after the rewrite. A
    /// condition Calcite itself reads as never true is reduced too: <c>(x IS NULL) IS NULL</c> tests for null what its
    /// type says never is, and is the constant whatever the declaration.
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
    /// read off it was a reading of other documents, and a filter over one container became the empty relation beside
    /// a statement over another (<see cref="CosmosConvention.Owns"/>).
    /// </para>
    /// <para>
    /// <b>The empty relation stops the evaluation of what is beneath the filter too.</b> Calcite applies a filter beneath
    /// this one to every row before this one reads any, so a cast there that fails on a document fails the query whatever
    /// this one keeps; and the outer filter of two, written over a subquery, was the empty relation over a cast Calcite
    /// fails on. So the reduce is gated on this condition and every filter's beneath it, each read through its own input's
    /// binding: the empty relation only where nothing in any of them raises. A window is computed over every row beneath it
    /// as well, and binds nothing, so a filter over one is never reduced (<see cref="CosmosImplementor.TryBindOutput(RelNode?, out IReadOnlyList{CosmosPath?}, out IReadOnlyList{CosmosReading}, out IReadOnlyList{CosmosOrdering}, out CosmosClauses, out Metadata.CosmosContainerMetadata?)"/>).
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

            if (CosmosFilterSplitRule.FindTable(filter.getInput()) is not CosmosTable table || ReferenceEquals(table.Convention, _convention) == false)
                return false;

            return Rewritten(filter) is RexNode rewritten && (KeepsNothing(rewritten) || KeepsEverything(rewritten));
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);

            switch (Rewritten(filter))
            {
                // No document satisfies the filter and none fails it, so the relation holds no rows, and no request at all
                // is what answers it.
                case RexNode rewritten when KeepsNothing(rewritten):
                    call.transformTo(CosmosNoRows.Create(filter.getCluster(), filter.getRowType(), call.getMetadataQuery().getRowCount(filter)?.doubleValue() ?? 1d));
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
        /// The filter's condition as the filter rule pushes it, rewritten under this convention's declaration, its reduce
        /// gated on every filter beneath it as well; remembered against the condition, so asking twice reads one statement.
        /// </summary>
        RexNode? Rewritten(Filter filter)
        {
            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out _) == false)
                return null;

            var condition = filter.getCondition();
            var rexBuilder = filter.getCluster().getRexBuilder();

            if (Beneath(filter.getInput()) is not { } beneath)
                return null;

            if (beneath.Count == 0)
                return Metadata.CosmosFactRewriter.Rewrite(condition, fields, _convention.Container, CosmosImplementor.DefaultRootAlias, rexBuilder, readings);

            // Each condition beneath read through its own binding, set after this one's so that this one's ordinals stay
            // its own: the gate is the conjunction, over the bindings side by side.
            var gateFields = new List<CosmosPath?>(fields);
            var gateReadings = new List<CosmosReading>(Padded(readings, fields.Count));
            var gate = new java.util.ArrayList();
            gate.add(condition);

            foreach (var (below, belowFields, belowReadings) in beneath)
            {
                gate.add(RexUtil.shift(below, gateFields.Count));
                gateFields.AddRange(belowFields);
                gateReadings.AddRange(Padded(belowReadings, belowFields.Count));
            }

            return Metadata.CosmosFactRewriter.Rewrite(condition, gateFields, _convention.Container, CosmosImplementor.DefaultRootAlias, rexBuilder, gateReadings,
                rexBuilder.makeCall(org.apache.calcite.sql.fun.SqlStdOperatorTable.AND, gate));
        }

        /// <summary>
        /// Returns the condition of every filter beneath a node, each with the binding of its own input, or <c>null</c>
        /// where one has no binding to read it through.
        /// </summary>
        static List<(RexNode Condition, IReadOnlyList<CosmosPath?> Fields, IReadOnlyList<CosmosReading> Readings)>? Beneath(RelNode? node)
        {
            var found = new List<(RexNode, IReadOnlyList<CosmosPath?>, IReadOnlyList<CosmosReading>)>();
            return Visit(node) ? found : null;

            bool Visit(RelNode? at)
            {
                if (at is org.apache.calcite.plan.volcano.RelSubset subset)
                    at = subset.getOriginal() ?? subset.getBest();

                if (at is null)
                    return true;

                if (at is Filter below)
                {
                    if (CosmosImplementor.TryBindOutput(below.getInput(), out var fields, out var readings, out _) == false)
                        return false;

                    found.Add((below.getCondition(), fields, readings));
                }

                var inputs = at.getInputs();
                for (var i = 0; i < inputs.size(); i++)
                    if (Visit((RelNode)inputs.get(i)) == false)
                        return false;

                return true;
            }
        }

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
