using org.apache.calcite.plan;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Replaces a filter that keeps no document with the empty relation, and one that keeps every document with its
    /// input: what the oracle says of the filter's conjuncts and the container's declaration together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rewrite reaches the constant; this is what makes it a plan.</b> <see cref="Metadata.CosmosFactRewriter"/>
    /// answers <c>FALSE</c> where the conjuncts contradict the declaration, a strengthening every conjunct's own facts
    /// may justify, and <c>TRUE</c> where every conjunct is one the rest and the declaration imply, deleted one at a
    /// time. A converted filter carrying <c>FALSE</c> is a round trip for no rows, and one carrying <c>TRUE</c> a
    /// <c>WHERE</c> that restricts nothing. <c>CoreRules.FILTER_REDUCE_EXPRESSIONS</c>, which makes an always-false
    /// filter an empty <c>Values</c>, matches the logical filter, and the constant exists only after the rewrite.
    /// </para>
    /// <para>
    /// <b>Only a logical filter, over this convention's own container.</b> A converted filter's condition has been
    /// rewritten already, under the declaration of the convention that converted it, and a filter in a host's
    /// convention is not this rule's. When a conversion could still reach another container's filter, the constant
    /// read off it was a reading of other documents, and a filter over one container became the empty relation beside
    /// a statement over another (<see cref="CosmosConvention.Owns"/>).
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

            return Rewritten(filter) is RexLiteral literal && (literal.isAlwaysFalse() || literal.isAlwaysTrue());
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);

            switch (Rewritten(filter))
            {
                // No document satisfies the filter, so the relation is the empty one, and no request at all is what
                // answers it: a host with a rule for an empty Values plans none.
                case RexLiteral literal when literal.isAlwaysFalse():
                    call.transformTo(LogicalValues.createEmpty(filter.getCluster(), filter.getRowType()));
                    break;

                // Every conjunct is one the rest of the filter and the declaration make true of every document: the
                // filter is its input, and the statement needs no WHERE.
                case RexLiteral literal when literal.isAlwaysTrue():
                    call.transformTo(filter.getInput());
                    break;
            }
        }

        /// <summary>
        /// The filter's condition as the filter rule pushes it, rewritten under this convention's declaration;
        /// remembered against the condition, so asking twice reads one statement.
        /// </summary>
        RexNode? Rewritten(Filter filter) =>
            CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out _)
                ? Metadata.CosmosFactRewriter.Rewrite(filter.getCondition(), fields, _convention.Container, CosmosImplementor.DefaultRootAlias, filter.getCluster().getRexBuilder(), readings)
                : null;

    }

}
