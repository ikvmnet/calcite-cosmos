using System;
using System.Collections.Generic;

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

                // Recorded where the rendering is not the path, so that a node above which rebuilds
                // the select list emits what this decided rather than the path underneath. A guarded
                // accessor is the case: the path holds the raw value and the column carries what the
                // accessor means for it.
                //
                // Asked of the two texts rather than of the reading, which is a proxy that used to
                // hold and does not: an array-typed accessor is guarded and still read as its
                // declared type, so a reading of Typed no longer says the rendering is the path.
                rendered[i] = string.Equals(expression, paths[i]?.ToString(), StringComparison.Ordinal) ? null : expression;

                // A computed column that converts a path the container confines to one stored shape
                // may still be ordered by that path, even though it addresses none. A weaker claim
                // than a binding and recorded apart from one -- see CosmosImplementor.OrderingPaths.
                string? chain = null;
                var held = Metadata.CosmosTemporalParts.None;
                var candidate = paths[i];

                if (candidate is null)
                    candidate = OrderingCandidateOf(node, translator, implementor.RootAlias, out chain, out held);

                ordering[i] = paths[i] is not null || IsOrderable(facts, candidate, chain, held) ? candidate : null;
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
        public static bool IsSortableAtTheService(RexNode node)
        {
            return node is RexCall call
                && string.Equals(call.getOperator().getName(), Apache.Calcite.Geography.Sql.GeographyOperatorTable.ClrStGeogDistance.getName(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns the path underneath a projection that converts one, or <c>null</c> where the
        /// projection is not such a conversion.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The structural half of the question, and it is split from the other half on purpose.</b>
        /// Which ordinal holds which candidate depends on <em>which tree is looked at</em> — a rule
        /// reading one member of a <c>RelSubset</c> and the implementation another would disagree,
        /// and a rule that decided differently from implementation fires on a key implementation then
        /// refuses. So this is derived once, on the walk
        /// <see cref="CosmosImplementor.TryBindOutput"/> already makes, and never by a second walk of
        /// its own. Whether the container licenses the candidate is <see cref="IsOrderable"/>, which
        /// is a pure lookup and cannot disagree with itself.
        /// </para>
        /// <para>
        /// <b>Only a conversion to a type Cosmos has no equivalent of.</b> A <c>UUID</c> and a
        /// <c>TIMESTAMP</c> are both strings at the service, so the ordering question is about the
        /// stored spelling. A cast between two types the service compares natively is not this
        /// rewrite's business and gets no entry.
        /// </para>
        /// <para>
        /// <b>A parse is the same conversion written as a chain, and it hands back the format with the
        /// path.</b> <c>PARSE_DATETIME(&lt;format&gt;, &lt;path&gt;)</c> converts the text the way a
        /// cast does, except that the query said <em>how</em> — and ordering by the stored strings is
        /// ordering by what the parse answers only where the format reads the declared shape. Which
        /// format was written is structural, so it is read here; whether it reads the shape is the
        /// pure lookup <see cref="IsOrderable"/> makes.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="translator">Resolves an expression to the path it addresses.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="format">
        /// On return, the format a parse reads the path with, or <c>null</c> where the conversion
        /// names none and is Calcite's own.
        /// </param>
        /// <param name="held">On return, the halves of an instant the conversion's value holds.</param>
        /// <returns>The path, or <c>null</c>.</returns>
        public static CosmosPath? OrderingCandidateOf(RexNode node, CosmosRexTranslator translator, string rootAlias, out string? format, out Metadata.CosmosTemporalParts held)
        {
            format = null;
            held = Metadata.CosmosTemporalParts.None;

            if (node is not RexCall call || translator is null)
                return null;

            RexNode? converted;
            var kind = call.getKind().name();

            if ((kind == nameof(org.apache.calcite.sql.SqlKind.__Enum.CAST) || kind == nameof(org.apache.calcite.sql.SqlKind.__Enum.SAFE_CAST))
                && call.getOperands().size() == 1
                && IsStoredAsText(call.getType()?.getSqlTypeName()))
            {
                converted = (RexNode)call.getOperands().get(0);
                held = Metadata.CosmosTemporalParse.PartsOf(call.getType()?.getSqlTypeName());
            }
            else if (Metadata.CosmosTemporalParse.TryRead(call, out var text, out var written, out var parsed) && text is not null)
            {
                converted = text;
                format = written;
                held = parsed;
            }
            else
            {
                return null;
            }

            if (translator.TryResolvePath(converted, out var path) == false || path is null
                || string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
            {
                format = null;
                held = Metadata.CosmosTemporalParts.None;
                return null;
            }

            return path;
        }

        /// <summary>
        /// Determines whether the container licenses ordering by a candidate path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two conditions.</b> The stored form has to preserve order, which is what
        /// <see cref="Metadata.CosmosRepresentation.PreservesOrder"/> says and which a form preserving
        /// equality alone does not give.
        /// </para>
        /// <para>
        /// <b>And the guard the projection renders has to be vacuous</b>, which is the condition a
        /// reading of <c>TODO.md</c> alone would miss — see
        /// <see cref="CosmosImplementor.OrderingPaths"/> for what the column renders as and why an
        /// object at the path would otherwise sort on the wrong side of every scalar.
        /// </para>
        /// <para>
        /// A pure function of the path and the facts, which is what lets the rule and the
        /// implementation each ask it without a walk between them.
        /// </para>
        /// </remarks>
        /// <param name="facts">What the container declares, already derived.</param>
        /// <param name="path">The candidate path, or <c>null</c>.</param>
        /// <param name="format">
        /// The format a parse reads the path with, where the candidate is a chain rather than a cast;
        /// <c>null</c> otherwise. A format that does not read the declared shape maps the stored
        /// strings onto instants of its own, and their order is then not the order the key asked for.
        /// </param>
        /// <param name="held">The halves of an instant the conversion's value holds.</param>
        /// <returns><c>true</c> where a sort may order by the path.</returns>
        public static bool IsOrderable(Metadata.CosmosFactSet? facts, CosmosPath? path, string? format = null, Metadata.CosmosTemporalParts held = Metadata.CosmosTemporalParts.None)
        {
            if (facts is null || path is null)
                return false;

            if (Metadata.CosmosDocumentPath.From(path) is not Metadata.CosmosDocumentPath document)
                return false;

            if (facts.RepresentationOf(document) is not Metadata.CosmosRepresentation representation || representation.PreservesOrder == false)
                return false;

            // A parse names how the text is read and is licensed by the format; a cast leaves it to
            // the engine and is licensed only where the engine can do it. A temporal cast over an
            // ISO-8601 instant cannot be, measured -- so ordering by the stored strings would order
            // rows for a query whose key raises. A candidate carrying neither is a UUID cast, whose
            // conversion the engine performs and which `CalciteUuidReadingMeasurementTests` pins.
            if (format is not null)
            {
                if (Metadata.CosmosStoredForms.ParsesExactly(representation, format, held) == false)
                    return false;
            }
            else if (held != Metadata.CosmosTemporalParts.None
                && Metadata.CosmosStoredForms.EngineReads(representation, held) == false)
            {
                return false;
            }

            return facts.IsAlwaysScalar(document);
        }

        /// <summary>
        /// Returns the document paths a sortable expression reads, where every operand is one this
        /// can account for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What a sort needs to know beyond whether the expression renders.</b>
        /// <see cref="IsSortableAtTheService"/> says the service will order by a geodesic distance;
        /// it does not say the key can never be null, and on a nullable key
        /// <c>CosmosSort.TryGetDescending</c> refuses any placement but Cosmos's own — which is the
        /// default spelling for neither direction, so a generated <c>ORDER BY</c> is declined and the
        /// container is read whole and sorted in process. A distance is null only where an operand
        /// is, and an operand is a literal or a stored shape, so the question reduces to whether the
        /// paths hold a value in every document.
        /// </para>
        /// <para>
        /// <b>The two operand shapes are exactly the two the translator renders.</b>
        /// <c>WriteGeographyLiteral</c> writes a geography that is a literal or resolves to a path and
        /// declines everything else, so an expression whose operands this can account for is an
        /// expression that reaches the service at all. Anything else answers <c>null</c> rather than a
        /// partial list, because a list missing an operand would prove the wrong thing.
        /// </para>
        /// <para>
        /// Structural, like <see cref="OrderingCandidateOf"/>, and derived on the one walk
        /// <see cref="CosmosImplementor.TryBindOutput"/> makes. Whether the container licenses the
        /// paths is a pure lookup the caller makes — <c>CosmosSortRule.NonNullFields</c> — for the
        /// reason <see cref="CosmosOrdering"/> gives: two walks can disagree about which expression
        /// sits at an ordinal, and a pure lookup cannot disagree with itself.
        /// </para>
        /// </remarks>
        /// <param name="node">The projected expression.</param>
        /// <param name="translator">Resolves an expression to the path it addresses.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The paths, empty where every operand is a literal, or <c>null</c>.</returns>
        public static IReadOnlyList<CosmosPath>? SortableOperandsOf(RexNode node, CosmosRexTranslator translator, string rootAlias)
        {
            if (IsSortableAtTheService(node) == false || node is not RexCall call || translator is null)
                return null;

            var paths = new List<CosmosPath>();
            var operands = call.getOperands();

            for (var i = 0; i < operands.size(); i++)
            {
                var operand = (RexNode)operands.get(i);

                // A geography written out of the statement itself. It is the object the constructor
                // stood for, and an object is not null.
                if (IsGeographyLiteral(operand))
                    continue;

                // A stored shape, reached through the constructor the same way every other geography
                // operand is.
                if (translator.TryResolveGeography(operand, out var path) == false
                    || path is null
                    || string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                    return null;

                paths.Add(path);
            }

            return paths;
        }

        /// <summary>
        /// Determines whether an operand is a geography the statement carries rather than reads.
        /// </summary>
        /// <remarks>
        /// <c>CLR_ST_GEOG_GEOMFROMGEOJSON</c> over a character literal, which is the form
        /// <c>WriteGeographyLiteral</c> writes into the statement as the object it denotes. A literal
        /// that is SQL <c>NULL</c> is not one — it would make the distance null, which is the whole
        /// thing being ruled out.
        /// </remarks>
        static bool IsGeographyLiteral(RexNode node)
        {
            return node is RexCall call
                && string.Equals(call.getOperator().getName(), Apache.Calcite.Geography.Sql.GeographyOperatorTable.ClrStGeogGeomFromGeoJson.getName(), StringComparison.Ordinal)
                && call.getOperands().size() == 1
                && call.getOperands().get(0) is RexLiteral literal
                && literal.isNull() == false;
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
