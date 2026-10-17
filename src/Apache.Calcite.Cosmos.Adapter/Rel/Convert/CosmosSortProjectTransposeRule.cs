using Apache.Calcite.Cosmos.Adapter.Metadata;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.rules;
using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Calcite's transpose of a sort beneath the projection it orders, firing only where every key survives it as the
    /// order the plan asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the transpose is registered at all.</b> A projection is where a view gives a container a relational shape,
    /// and it does that by casting; nothing renders a bare cast, so the projection stays in process, and a sort and row
    /// limit above it stay with it, the container read whole to answer a bounded page. Transposed, the sort and its limit
    /// sit under the projection and push, leaving the cast above them to run over the rows that come back.
    /// </para>
    /// <para>
    /// <b>Why it is guarded.</b> Calcite maps the sort's keys through the projection, and a key that is a cast it maps to
    /// the cast's operand wherever it calls the cast monotonic — which it does of a cast of text to an instant or a UUID.
    /// The sort beneath then orders the operand's text where the plan asked for the converted value: <c>'2024-1-5'</c>
    /// after <c>'2024-10-1'</c> as text and before it as a date. The plan's inventory found twelve such transposes the
    /// planner admitted and, by luck of the costs, never chose. Whether a stored spelling orders as the converted value
    /// is a fact about a path, which the sort asks with its facts in hand of a conversion it reads off the projection;
    /// with no facts, a cast moves beneath the sort only where it preserves the order of every value it can be given —
    /// see <see cref="CosmosServiceOrder.CastPreservesOrder"/>.
    /// </para>
    /// <para>
    /// A transformation adds an equivalence rather than replacing one, so the untransposed plan survives and the planner
    /// costs both.
    /// </para>
    /// </remarks>
    public class CosmosSortProjectTransposeRule : SortProjectTransposeRule
    {

        /// <summary>
        /// The rule, over Calcite's own operands. Static, as Calcite's own instance is, so registering it once per
        /// convention registers it once.
        /// </summary>
        public static readonly CosmosSortProjectTransposeRule Instance = new(SortProjectTransposeRule.Config.DEFAULT);

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="config">Calcite's configuration of the transpose.</param>
        protected CosmosSortProjectTransposeRule(SortProjectTransposeRule.Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            var sort = (Sort)call.rel(0);
            var project = (Project)call.rel(1);
            var projects = project.getProjects();

            var collations = sort.getCollation().getFieldCollations();
            for (var i = 0; i < collations.size(); i++)
            {
                var index = ((RelFieldCollation)collations.get(i)).getFieldIndex();
                if (index < 0 || index >= projects.size())
                    return false;

                if (PreservesOrder((RexNode)projects.get(index)) == false)
                    return false;
            }

            return base.matches(call);
        }

        /// <summary>
        /// Determines whether a projected key, moved beneath the projection, orders the rows as it does above: a field
        /// itself, or a cast of one that preserves the order of every value.
        /// </summary>
        static bool PreservesOrder(RexNode node)
        {
            if (node is RexInputRef)
                return true;

            return node is RexCall call
                && (call.getKind() == SqlKind.CAST || call.getKind() == SqlKind.SAFE_CAST)
                && call.getOperands().size() == 1
                && call.getOperands().get(0) is RexInputRef operand
                && CosmosServiceOrder.CastPreservesOrder(operand.getType(), call.getType());
        }

    }

}
