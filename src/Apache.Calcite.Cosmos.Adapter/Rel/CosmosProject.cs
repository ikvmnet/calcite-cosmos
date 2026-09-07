using System;

using Apache.Calcite.Cosmos.Adapter.Sql;

using com.google.common.collect;

using java.util;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Projection implemented in the <see cref="CosmosConvention"/> calling convention.
    /// </summary>
    /// <remarks>
    /// Rendered as <c>SELECT VALUE { name: expression, … }</c>. Cosmos treats a flat select list as
    /// sugar for exactly this object constructor, and emitting the explicit form keeps the result
    /// shape uniform: one JSON value per row, keyed by output field name, whatever the arity.
    /// </remarks>
    public class CosmosProject : Project, CosmosRel
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="projects">The projected expressions.</param>
        /// <param name="rowType">The output row type.</param>
        public CosmosProject(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, List projects, RelDataType rowType) :
            base(cluster, traitSet, ImmutableList.of(), input, projects, rowType, ImmutableSet.of())
        {

        }

        /// <inheritdoc />
        public override Project copy(RelTraitSet traitSet, RelNode input, List projects, RelDataType rowType)
        {
            return new CosmosProject(getCluster(), traitSet, input, projects, rowType);
        }

        /// <inheritdoc />
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            return base.computeSelfCost(planner, mq)?.multiplyBy(CosmosConvention.CostMultiplier);
        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            implementor.Visit(getInput());

            // Without derived tables there is nowhere to nest a second projection.
            if (implementor.Query.HasProjection)
                throw new CosmosTranslationException("A projection has already been applied.");

            var projects = getProjects();
            if (projects.size() == 0)
                throw new CosmosTranslationException("An empty projection has no Cosmos equivalent.");

            var names = getRowType().getFieldNames();

            // Bound to the input field bindings; the rebinding below happens only once every
            // expression has been translated against them.
            var translator = implementor.CreateTranslator();

            var paths = new CosmosPath?[projects.size()];
            var readings = new CosmosReading[projects.size()];
            var sortable = new string?[projects.size()];

            // Read before anything rebinds them. A column passed straight through keeps how it is
            // read: the JSON column projected under an alias is still the document, and reading it as
            // the VARCHAR it is declared would refuse the object it carries.
            var inputReadings = implementor.Readings;

            for (var i = 0; i < projects.size(); i++)
            {
                var node = (RexNode)projects.get(i);

                // A cast to text over a document value is dropped and put back by the reader, which is
                // the one expression the statement carries without. See
                // CosmosRexTranslator.TryRenderedTextOperand for why that is an equivalence and not a
                // trade, and why such a column addresses nothing afterwards.
                var expression = translator.TranslateProjection(node, out var reading);
                implementor.Query.SelectProperty((string)names.get(i), expression);

                // A geodesic distance is the one computed expression the service admits in an ORDER BY.
                // Recorded against the ordinal so a sort above this projection can write the expression
                // out again -- Cosmos cannot order by the alias -- and nothing else is, because nothing
                // else was measured to be accepted there. See CosmosImplementor.SortableExpressions.
                sortable[i] = IsSortableAtTheService(node) ? expression : null;
                readings[i] = reading != CosmosReading.Typed ? reading
                    : node is RexInputRef reference
                        && reference.getIndex() >= 0
                        && reference.getIndex() < inputReadings.Count
                            ? inputReadings[reference.getIndex()]
                            : CosmosReading.Typed;

                // Null where the projection computes rather than addresses. Nothing downstream can
                // refer to it, because Cosmos has no name for it — a projection alias is not
                // addressable from ORDER BY or WHERE. A rendered cast resolves to none for the same
                // reason it is rendered: the column carries text and the path carries the raw value,
                // so an operator written against the path would mean something else.
                paths[i] = translator.TryResolvePath(node, out var path) ? path : null;
            }

            // Downstream clauses address the source document, not the projected object — Cosmos
            // ORDER BY cannot reference a projection alias. Rebinding to the underlying paths is
            // therefore what lets a sort above a projection still work.
            //
            // Per ordinal, not all or nothing: a computed column bound to null declines only the
            // operators that actually read it, leaving a sort or filter over the plain paths beside
            // it still pushable.
            implementor.Fields = paths;
            implementor.Readings = readings;
            implementor.SortableExpressions = sortable;
        }


        /// <summary>
        /// Determines whether a projected expression is one the service will order by.
        /// </summary>
        /// <remarks>
        /// By name, because a call resolved through a schema carries an operator Calcite built around
        /// the declaration rather than the operator itself — the same reason the translator dispatches
        /// on names. Only the geodesic distance qualifies: measured, <c>ORDER BY ST_DISTANCE(…)</c> is
        /// accepted while <c>ORDER BY DateTimeToTicks(…)</c> and <c>ORDER BY IIF(…)</c> are refused
        /// with 400, error 2206.
        /// </remarks>
        static bool IsSortableAtTheService(RexNode node)
        {
            return node is RexCall call
                && string.Equals(call.getOperator().getName(), Apache.Calcite.Geography.Sql.GeographyOperatorTable.StGeogDistance.getName(), StringComparison.Ordinal);
        }

    }

}
