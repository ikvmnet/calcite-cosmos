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
            // expression has been translated against them. Given the container's unconditional facts,
            // because a projection may render a stored form as the value the plan declared -- a path
            // declared a UUID spelling among them -- and has no predicate of its own to prove a
            // guarded fact from.
            var translator = implementor.CreateTranslator(null, implementor.UnconditionalFacts);

            var paths = new CosmosPath?[projects.size()];
            var readings = new CosmosReading[projects.size()];
            var sortable = new string?[projects.size()];
            var rendered = new string?[projects.size()];
            var ordering = new CosmosPath?[projects.size()];

            // Only what holds outright. A projection carries no predicate of its own, so there is
            // nothing here to prove a guarded fact from -- the same argument, and the same
            // Derive(null), that CosmosSortRule makes for the null placement.
            var facts = implementor.Container?.Facts.Derive(null);

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

                // Recorded where the rendering is not the path, so that a node above which rebuilds
                // the select list emits what this decided rather than the path underneath. A guarded
                // accessor is the case: the path holds the raw value and the column carries text.
                rendered[i] = reading != CosmosReading.Typed ? expression : null;
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

                // A computed column that converts a path the container confines to one stored shape
                // may still be ordered by that path, even though it addresses none. A weaker claim
                // than a binding and recorded apart from one -- see CosmosImplementor.OrderingPaths.
                ordering[i] = paths[i] ?? OrderingPathOf(node, translator, facts, implementor.RootAlias);
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
            implementor.RenderedExpressions = rendered;
            implementor.OrderingPaths = ordering;
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

        /// <summary>
        /// Returns the path a sort may order by where a projection converts one, or <c>null</c> where
        /// the projection is not such a conversion or the container does not license it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Asked twice, from the plan and from the statement, and it has to answer the same both
        /// times.</b> <see cref="Convert.CosmosSortRule"/> decides whether a sort may be pushed at all
        /// and <see cref="Implement"/> records what it decided; if the two disagreed the rule would
        /// fire on a key implementation then refused. So the decision lives here and both call it,
        /// which is the arrangement <see cref="IsSortableAtTheService"/> already has.
        /// </para>
        /// <para>
        /// <b>Only a conversion to a type Cosmos has no equivalent of.</b> A <c>UUID</c> and a
        /// <c>TIMESTAMP</c> are both strings at the service, so the ordering question is about the
        /// stored spelling and <see cref="Metadata.CosmosRepresentation.PreservesOrder"/> answers it.
        /// A cast between two types the service compares natively is not this rewrite's business and
        /// gets no entry.
        /// </para>
        /// <para>
        /// <b>And the guard has to be vacuous</b>, which is the condition a reading of
        /// <c>TODO.md</c> alone would miss — see <see cref="CosmosImplementor.OrderingPaths"/> for
        /// what the column renders as and why an object at the path would otherwise sort on the wrong
        /// side of every scalar.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="translator">Resolves an expression to the path it addresses.</param>
        /// <param name="facts">What the container declares, already derived.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The path, or <c>null</c>.</returns>
        public static CosmosPath? OrderingPathOf(RexNode node, CosmosRexTranslator translator, Metadata.CosmosFactSet? facts, string rootAlias)
        {
            if (node is not RexCall call || facts is null || translator is null)
                return null;

            var kind = call.getKind().name();
            if (kind != nameof(org.apache.calcite.sql.SqlKind.__Enum.CAST) && kind != nameof(org.apache.calcite.sql.SqlKind.__Enum.SAFE_CAST))
                return null;

            if (call.getOperands().size() != 1 || IsStoredAsText(call.getType()?.getSqlTypeName()) == false)
                return null;

            if (translator.TryResolvePath((RexNode)call.getOperands().get(0), out var path) == false || path is null)
                return null;

            if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            if (Metadata.CosmosDocumentPath.From(path) is not Metadata.CosmosDocumentPath document)
                return null;

            if (facts.RepresentationOf(document) is not Metadata.CosmosRepresentation representation || representation.PreservesOrder == false)
                return null;

            return facts.IsAlwaysScalar(document) ? path : null;
        }

        /// <summary>
        /// Determines whether a SQL type is one Cosmos has no equivalent of and stores as a string.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><c>true</c> where the service holds the value as text.</returns>
        static bool IsStoredAsText(org.apache.calcite.sql.type.SqlTypeName? type) =>
            type == org.apache.calcite.sql.type.SqlTypeName.UUID
            || type == org.apache.calcite.sql.type.SqlTypeName.DATE
            || type == org.apache.calcite.sql.type.SqlTypeName.TIME
            || type == org.apache.calcite.sql.type.SqlTypeName.TIME_WITH_LOCAL_TIME_ZONE
            || type == org.apache.calcite.sql.type.SqlTypeName.TIMESTAMP
            || type == org.apache.calcite.sql.type.SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE;

    }

}
