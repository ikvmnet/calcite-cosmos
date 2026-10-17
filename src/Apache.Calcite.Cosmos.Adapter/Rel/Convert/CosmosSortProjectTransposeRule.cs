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
    /// planner admitted and, by luck of the costs, never chose. So a cast moves beneath the sort where it preserves the
    /// order of every value it can be given, or on the last key where it only ties, as text narrowed to a length does —
    /// see <see cref="CosmosServiceOrder.CastPreservesOrder"/> — or where it is a
    /// conversion of text whose stored spelling the facts say orders as the converted value: asked of
    /// <see cref="CosmosServiceOrder.OrderBy"/>, of what the rows the projection reads are known to satisfy, as the sort
    /// over the projection would ask it. That second question reads the binding and the facts, and is asked only of a
    /// cast the first refused.
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

                var key = (RexNode)projects.get(index);
                if (PreservesOrder(key, last: i == collations.size() - 1) == false && IsStoredFormConversion(project, key, (RelFieldCollation)collations.get(i)) == false)
                    return false;
            }

            return base.matches(call);
        }

        /// <summary>
        /// Determines whether a projected key is a conversion of text into an instant or a UUID whose stored spelling the
        /// facts say orders as the converted value, under this key's direction and placement.
        /// </summary>
        static bool IsStoredFormConversion(Project project, RexNode node, RelFieldCollation collation)
        {
            if (node is not RexCall cast
                || cast.getKind() != SqlKind.CAST && cast.getKind() != SqlKind.SAFE_CAST
                || cast.getOperands().size() != 1
                || cast.getOperands().get(0) is not RexInputRef operand)
                return false;

            var target = cast.getType().getSqlTypeName();
            var held = CosmosTemporalParse.PartsOf(target);
            if (held == CosmosTemporalParts.None && target != org.apache.calcite.sql.type.SqlTypeName.UUID)
                return false;

            if (CosmosImplementor.TryBindOutput(project.getInput(), out var fields, out var readings, out _, out _, out var container) == false
                || operand.getIndex() >= fields.Count
                || fields[operand.getIndex()] is not Sql.CosmosPath path
                || string.Equals(path.Alias, CosmosImplementor.DefaultRootAlias, System.StringComparison.Ordinal) == false
                || CosmosDocumentPaths.From(path) is not Apache.Calcite.Cosmos.Facts.JsonDocumentPath document)
                return false;

            var facts = CosmosRowFacts.Of(project.getInput(), container, Apache.Calcite.Cosmos.Facts.OracleLicence.OrderBy);
            return CosmosServiceOrder.OrderBy(facts, new[] { new CosmosOrderKey(CosmosOrderOperand.ForConversion(document, null, held), collation) }).IsProved;
        }

        /// <summary>
        /// Determines whether a projected key, moved beneath the projection, orders the rows as it does above: a field
        /// itself, or a cast of one that preserves the order of every value — on the last key, one that only ties.
        /// </summary>
        static bool PreservesOrder(RexNode node, bool last)
        {
            if (node is RexInputRef)
                return true;

            return node is RexCall call
                && (call.getKind() == SqlKind.CAST || call.getKind() == SqlKind.SAFE_CAST)
                && call.getOperands().size() == 1
                && call.getOperands().get(0) is RexInputRef operand
                && CosmosServiceOrder.CastPreservesOrder(operand.getType(), call.getType(), last);
        }

    }

}
