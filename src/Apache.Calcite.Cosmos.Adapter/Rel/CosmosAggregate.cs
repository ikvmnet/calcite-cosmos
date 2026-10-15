using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using com.google.common.collect;

using java.util;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.sql;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Aggregation implemented in the <see cref="CosmosConvention"/> calling convention, rendered
    /// as a <c>GROUP BY</c> clause with aggregate functions in the projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cosmos aggregates do not share SQL's null handling, so what may be pushed down is narrow.
    /// Measured against the emulator over the values <c>{10, 20, null, 5}</c> with two documents
    /// lacking the property entirely:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>COUNT(c.v)</c> returns 4 — it counts the JSON <c>null</c> and skips only the absent
    /// documents. SQL <c>COUNT(x)</c> excludes nulls and would return 3.
    /// </description></item>
    /// <item><description>
    /// <c>SUM(c.v)</c> and <c>AVG(c.v)</c> return <c>undefined</c> rather than ignoring the null,
    /// where SQL would return 35 and 11.67.
    /// </description></item>
    /// <item><description>
    /// A grouping key that is absent from a document produces a group whose key is omitted from
    /// the result object, rather than a null group.
    /// </description></item>
    /// </list>
    /// <para>
    /// The divergence disappears when the aggregated value cannot be null, which is the same
    /// reasoning applied to sort null placement. So <c>COUNT(*)</c> is always safe, and the value
    /// aggregates are pushed down only over a non-nullable input. <c>COUNT(x)</c> needs no
    /// non-nullable case of its own: Calcite rewrites it to <c>COUNT(*)</c> there before any rule
    /// sees it.
    /// </para>
    /// </remarks>
    public class CosmosAggregate : Aggregate, CosmosRel
    {

        /// <summary>
        /// Determines whether an aggregate call can be rendered faithfully.
        /// </summary>
        /// <param name="call">The aggregate call.</param>
        /// <param name="inputRowType">The row type of the aggregate's input.</param>
        /// <returns><c>true</c> if the call may be pushed down; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
        public static bool CanImplement(AggregateCall call, RelDataType inputRowType)
        {
            if (call is null)
                throw new ArgumentNullException(nameof(call));
            if (inputRowType is null)
                throw new ArgumentNullException(nameof(inputRowType));

            // DISTINCT aggregates and FILTER clauses have no Cosmos equivalent.
            if (call.isDistinct() || call.hasFilter() || call.distinctKeys is not null)
                return false;

            // An aggregate carrying its own ordering cannot be expressed either.
            if (call.getCollation() is not null && call.getCollation().getFieldCollations().size() > 0)
                return false;

            var arguments = call.getArgList();

            switch ((SqlKind.__Enum)call.getAggregation().getKind().ordinal())
            {
                case SqlKind.__Enum.COUNT:
                    // COUNT(*) counts rows and agrees with SQL. COUNT(x) does not: Cosmos counts
                    // the JSON null where SQL excludes it. Admitting COUNT(x) over a non-nullable
                    // column — the value aggregates' reasoning — was probed and is dead code:
                    // Calcite rewrites that COUNT(x) to COUNT(*) before any rule sees it.
                    return arguments.size() == 0;

                case SqlKind.__Enum.SUM:
                case SqlKind.__Enum.MIN:
                case SqlKind.__Enum.MAX:
                    return arguments.size() == 1 && IsNonNullable(inputRowType, ((java.lang.Integer)arguments.get(0)).intValue());

                case SqlKind.__Enum.AVG:
                    // Cosmos AVG returns the exact mean; Calcite types an integer AVG as an integer
                    // with truncating division. The fraction cannot be materialized as the declared
                    // type — found by the differential suite, a fractional mean of _ts failing to
                    // read as BIGINT — and truncating it here would still diverge from SQL's
                    // toward-zero division on negatives. So AVG pushes only where the argument is
                    // approximate and the two agree exactly; an integer AVG is carried by
                    // AGGREGATE_REDUCE_FUNCTIONS as SUM and COUNT with the division done above, in
                    // SQL's own semantics.
                    return arguments.size() == 1
                        && IsNonNullable(inputRowType, ((java.lang.Integer)arguments.get(0)).intValue())
                        && IsApproximateNumeric(inputRowType, ((java.lang.Integer)arguments.get(0)).intValue());

                default:
                    return false;
            }
        }

        /// <summary>
        /// Determines whether grouping by the key the service is given puts two rows in one group
        /// exactly where the column, as it is read back, says they are equal.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The service groups the key the projection wrote; SQL groups the column.</b> A column read
        /// as its declared type groups as the column reads. Where it is the stored value the key is the
        /// path; where a <c>RETURNING</c> asserted a type, the key is the guard the projection writes it
        /// through, null for every value of another type exactly as the column is — see
        /// <c>CosmosRexTranslator.ReadingTests</c>. Before that guard the key was the path, and a
        /// grouping or a <c>DISTINCT</c> over a path holding an object sent the object back for a
        /// column declared a number, which the reader refused. <c>RETURNING DECIMAL</c>, which parses
        /// rather than asserting, addresses no path at all — see
        /// <see cref="CosmosRexTranslator.IsParsingJsonValue"/> — so no key is bound to one.
        /// </para>
        /// <para>
        /// <b>A rendering groups by its text, and the service by the value it renders.</b> A key read
        /// as text is a <c>JSON_VALUE</c>, or a field bound to one, whose column is the value's
        /// rendering — see <see cref="CosmosReading.Text"/> — while the expression it is grouped by at
        /// the service is the guarded path, which holds the value itself: a stored <c>30</c> and a
        /// stored <c>"30"</c> are two groups at the service and one value, <c>'30'</c>, in SQL.
        /// Measured on the emulator over the typed container, whose price is both (review probes J1,
        /// J2 and J13): <c>SELECT DISTINCT JSON_VALUE(c."DOC", '$.price')</c> returned <c>'30'</c>
        /// twice, its <c>GROUP BY</c> with <c>COUNT(*)</c> two groups of one where SQL has one of two,
        /// and <c>COUNT(DISTINCT …)</c> — which Calcite expands into a grouping beneath the count —
        /// counted six where SQL counts five. The translator refuses a rendering read as a value for
        /// the same reason, and the lookup join's key is the same rendering; a grouping key is the
        /// third reader of the reading.
        /// </para>
        /// <para>
        /// The exception is theirs too, and the question is theirs, asked of the same method —
        /// <see cref="CosmosRexTranslator.IsReadAsStored"/>, which a sort key asks as well: a container
        /// that declares the path a string outright, where the rendering is the stored value and a
        /// second JSON type cannot be there to be kept apart. Outright, as <see cref="GroupingKey"/>
        /// reads the declaration, since a key is not a predicate and proves nothing. A
        /// <c>JSON_QUERY</c> fragment has no such exception — its text is a serialisation, and whether
        /// the service holds two objects equal exactly where their compact texts are equal, in key
        /// order and number spelling, is not measured — so it is declined. The two readings a grouping
        /// takes before asking are its own: the declared one, whose key is the column as above where
        /// the method asks whether the <em>path</em> is, and the document column, which groups nothing
        /// either way, no two documents being equal, their <c>_rid</c> alone telling them apart.
        /// </para>
        /// <para>
        /// <b>Asked by the rule and by implementation</b>, each of the binding its own walk produced —
        /// <see cref="Convert.CosmosAggregateRule"/> of <c>TryBindOutput</c>'s, and
        /// <see cref="Implement"/> and <see cref="EmitDistinct"/> of the implementor's — so that the
        /// two cannot disagree. Declined in the rule, it costs the grouping and nothing else: it runs
        /// in process over what the projection beneath still sends.
        /// </para>
        /// </remarks>
        /// <param name="translator">A translator over the input's binding, readings and container.</param>
        /// <param name="readings">How each of the input's fields is read back.</param>
        /// <param name="rowType">The input's row type.</param>
        /// <param name="ordinal">The key.</param>
        /// <returns><c>true</c> where the key may be grouped by at the service.</returns>
        internal static bool GroupsAsTheColumnReads(CosmosRexTranslator translator, IReadOnlyList<CosmosReading> readings, RelDataType rowType, int ordinal)
        {
            if ((ordinal < readings.Count ? readings[ordinal] : CosmosReading.Typed) is CosmosReading.Typed or CosmosReading.Json)
                return true;

            return translator.IsReadAsStored(org.apache.calcite.rex.RexInputRef.of(ordinal, rowType));
        }

        static bool IsApproximateNumeric(RelDataType rowType, int ordinal)
        {
            var fields = rowType.getFieldList();
            if (ordinal < 0 || ordinal >= fields.size())
                return false;

            return org.apache.calcite.sql.type.SqlTypeName.APPROX_TYPES.contains(((RelDataTypeField)fields.get(ordinal)).getType().getSqlTypeName());
        }

        static bool IsNonNullable(RelDataType rowType, int ordinal)
        {
            var fields = rowType.getFieldList();
            if (ordinal < 0 || ordinal >= fields.size())
                return false;

            return ((RelDataTypeField)fields.get(ordinal)).getType().isNullable() == false;
        }

        /// <summary>
        /// Returns the Cosmos function name for an aggregate call.
        /// </summary>
        static string FunctionName(AggregateCall call)
        {
            return (SqlKind.__Enum)call.getAggregation().getKind().ordinal() switch
            {
                SqlKind.__Enum.COUNT => "COUNT",
                SqlKind.__Enum.SUM => "SUM",
                SqlKind.__Enum.MIN => "MIN",
                SqlKind.__Enum.MAX => "MAX",
                SqlKind.__Enum.AVG => "AVG",
                _ => throw new CosmosTranslationException($"Unsupported aggregate '{call.getAggregation().getName()}'."),
            };
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="groupSet">The grouping key ordinals.</param>
        /// <param name="groupSets">The grouping sets.</param>
        /// <param name="aggCalls">The aggregate calls.</param>
        public CosmosAggregate(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, ImmutableBitSet groupSet, List? groupSets, List aggCalls) :
            base(cluster, traitSet, ImmutableList.of(), input, groupSet, groupSets, aggCalls)
        {

        }

        /// <inheritdoc />
        public override Aggregate copy(RelTraitSet traitSet, RelNode input, ImmutableBitSet groupSet, List? groupSets, List aggCalls)
        {
            return new CosmosAggregate(getCluster(), traitSet, input, groupSet, groupSets, aggCalls);
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

            // Calcite prunes columns with a projection below the aggregate. Cosmos has one SELECT
            // per statement, so the aggregate's projection replaces it. That is only sound when
            // the pruning projection was path-only — otherwise its computed values are the very
            // things being grouped or aggregated, and they are no longer addressable.
            if (implementor.Query.HasProjection)
            {
                if (implementor.Fields.Count == 0)
                    throw new CosmosTranslationException("An aggregation cannot be applied above a computed projection.");

                implementor.Query.ClearProjection();
            }

            // GROUP BY is applied before OFFSET/LIMIT, so an aggregation above a row restriction
            // would group the whole set rather than the restricted one.
            if (implementor.Query.HasRowLimit)
                throw new CosmosTranslationException("An aggregation cannot be applied above a pushed-down row limit.");

            if (getGroupType() != Group.SIMPLE)
                throw new CosmosTranslationException("Grouping sets have no Cosmos equivalent.");

            // AN AGGREGATE WITH NO CALLS IS A DISTINCT, AND IS EMITTED AS ONE.
            //
            // Grouping by every output column and computing nothing is what SELECT DISTINCT means,
            // and Cosmos has the keyword. Rendering it that way is not a tidier spelling of the
            // same statement: GROUP BY and ORDER BY cannot appear together, and DISTINCT and
            // ORDER BY can — measured — so this is the difference between a sort that pushes and
            // one that does not.
            if (getAggCallList().size() == 0)
            {
                EmitDistinct(implementor);
                return;
            }

            // Cosmos rejects GROUP BY and ORDER BY in the same statement.
            if (implementor.Query.HasOrderBy)
                throw new CosmosTranslationException("Cosmos SQL does not support GROUP BY together with ORDER BY.");

            // Cosmos rejects an aggregate inside an object constructor, so a grouped projection
            // must be written as a flat select list.
            implementor.Query.FlatProjection = true;

            var inputRowType = getInput().getRowType();
            var names = getRowType().getFieldNames();
            var fields = implementor.Fields;
            var rendered = implementor.RenderedExpressions;
            var inputReadings = implementor.Readings;
            var readings = new CosmosReading[names.size()];
            var translator = implementor.CreateTranslator();
            var output = 0;

            // Calcite orders an aggregate's output as the grouping keys followed by the calls.
            var groupKeys = getGroupSet().asList();
            for (var i = 0; i < groupKeys.size(); i++)
            {
                var index = ((java.lang.Integer)groupKeys.get(i)).intValue();
                if (index < 0 || index >= fields.Count || fields[index] is null)
                    throw new CosmosTranslationException("A grouping key does not resolve to a document path.");

                // What the projection rendered, where that is not the path -- a guarded accessor
                // reads as text while the path holds the raw value, so grouping by the path would
                // group different values and return one the reader refuses.
                var expression = index < rendered.Count ? rendered[index] : null;
                var key = GroupingKey(fields[index]!, implementor.Container.Facts.Derive(null), expression);

                readings[output] = index < inputReadings.Count ? inputReadings[index] : CosmosReading.Typed;

                // And the guarded accessor still groups the value rather than its text.
                if (GroupsAsTheColumnReads(translator, inputReadings, getInput().getRowType(), index) == false)
                    throw new CosmosTranslationException("A grouping key read as text groups by the rendering in Calcite and by the value at the service, which differ wherever the path holds something other than a string.");

                implementor.Query.AddGroupBy(key);
                implementor.Query.SelectProperty((string)names.get(output++), key);
            }

            var calls = getAggCallList();
            for (var i = 0; i < calls.size(); i++)
            {
                var call = (AggregateCall)calls.get(i);
                if (CanImplement(call, inputRowType) == false)
                    throw new CosmosTranslationException($"Aggregate '{call}' has no faithful Cosmos equivalent.");

                implementor.Query.SelectProperty((string)names.get(output++), Render(call, fields));
            }

            // Aggregated output is computed, so nothing downstream can address it as a path. Bound
            // per ordinal rather than emptied, so a downstream reference is declined for naming a
            // computed column rather than for being out of range.
            var aggregated = new CosmosPath?[names.size()];

            // Fields resets the readings, so what the grouping keys carry is restored afterwards.
            // An aggregate's own output is computed and reads as declared, which is the default.
            implementor.Fields = aggregated;
            implementor.Readings = readings;
        }

        /// <summary>
        /// Emits a call-less aggregate as <c>SELECT DISTINCT</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The projected paths are the grouping keys, so this is the same statement a
        /// <c>GROUP BY</c> over every key would return — with two differences that matter. It may
        /// carry an <c>ORDER BY</c>, which a grouped statement may not; and its output is still
        /// <em>paths</em>, since nothing is computed, so a sort or a filter above it can address
        /// them. That is why the bindings are kept rather than blanked the way an aggregated row's
        /// are.
        /// </para>
        /// <para>
        /// The flat projection a grouped statement needs is not needed here — nothing is aggregated,
        /// so no aggregate can sit inside an object constructor — and the ordinary object form is
        /// what the row builder above expects.
        /// </para>
        /// </remarks>
        void EmitDistinct(CosmosImplementor implementor)
        {
            var names = getRowType().getFieldNames();
            var fields = implementor.Fields;
            var rendered = implementor.RenderedExpressions;
            var inputReadings = implementor.Readings;
            var readings = new CosmosReading[names.size()];
            var translator = implementor.CreateTranslator();

            var groupKeys = getGroupSet().asList();
            var projected = new CosmosPath?[names.size()];

            for (var i = 0; i < groupKeys.size(); i++)
            {
                var index = ((java.lang.Integer)groupKeys.get(i)).intValue();
                if (index < 0 || index >= fields.Count || fields[index] is null)
                    throw new CosmosTranslationException("A grouping key does not resolve to a document path.");

                var path = fields[index]!;

                // What the projection rendered, where that is not the path. A guarded accessor reads
                // as text and the path holds the raw value, so emitting the path here would return a
                // number for a column the plan declared VARCHAR -- which the reader refuses rather
                // than coerces, so the statement fails rather than answering differently.
                var expression = index < rendered.Count ? rendered[index] : null;

                readings[i] = index < inputReadings.Count ? inputReadings[index] : CosmosReading.Typed;

                // A DISTINCT is a grouping, and de-duplicates a rendering by the value too.
                if (GroupsAsTheColumnReads(translator, inputReadings, getInput().getRowType(), index) == false)
                    throw new CosmosTranslationException("A DISTINCT over a column read as text de-duplicates the rendering in Calcite and the value at the service, which differ wherever the path holds something other than a string.");

                implementor.Query.SelectProperty((string)names.get(i), GroupingKey(path, implementor.Container.Facts.Derive(null), expression));

                projected[i] = DistinctKeyBinding(path, implementor.Container);
            }

            implementor.Query.Distinct = true;

            // Fields resets the readings, so what the projection decided is restored afterwards.
            implementor.Fields = projected;
            implementor.Readings = readings;
        }

        /// <summary>
        /// The path a <c>DISTINCT</c>'s key binds to for the operators above it, or <c>null</c> where
        /// it binds none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Bound to the path only where the projected value is that path. A normalised key — see
        /// <see cref="GroupingKey"/> — is a computed column: the service will not order by a
        /// select-list alias, and the path underneath sorts an absent property somewhere the projected
        /// null does not. A path the container says is always present is never normalised, so it is
        /// still the path.
        /// </para>
        /// <para>
        /// <b>One answer, asked by two callers.</b> <see cref="EmitDistinct"/> binds by it, and
        /// <see cref="CosmosImplementor.TryBindOutput(RelNode?, out IReadOnlyList{CosmosPath?}, out CosmosClauses)"/>
        /// derives by it what every rule above a <c>DISTINCT</c> decides on. The walk used to bind the
        /// input's path regardless, so <see cref="Convert.CosmosSortRule"/> converted a sort over a
        /// <c>DISTINCT</c> of a path some documents lack and <see cref="CosmosSort.Implement"/> refused
        /// it — after the planner had committed to the plan, so the query failed rather than sorting
        /// in process (soundness probe P16). Each was right about its own encoding; there were two.
        /// </para>
        /// </remarks>
        /// <param name="key">The path the grouping key resolves to beneath the aggregate, if any.</param>
        /// <param name="container">The container, whose unconditional facts say what is always present.</param>
        /// <returns>The path, or <c>null</c>.</returns>
        internal static CosmosPath? DistinctKeyBinding(CosmosPath? key, CosmosContainerMetadata? container) =>
            key is not null && container is not null && IsAlwaysPresent(key, container.Facts.Derive(null)) ? key : null;

        /// <summary>
        /// The expression a grouping key is grouped and projected by.
        /// </summary>
        /// <remarks>
        /// <para>
        /// SQL has one <c>NULL</c>; the service distinguishes an absent property from a
        /// present-and-null one and groups them apart. Over a container holding one of each, grouping
        /// by that property returned two groups of one where SQL returns one group of two — measured,
        /// and the same difference reached <c>DISTINCT</c>, which is the same statement without the
        /// calls.
        /// </para>
        /// <para>
        /// The two are brought together by grouping on the value the property has when it is there and
        /// on <c>null</c> when it is not, which is exactly SQL's reading of both. The projection uses
        /// the same expression, so the group's own key reads back as one null rather than as two
        /// different absences.
        /// </para>
        /// <para>
        /// <b>Only where it is needed.</b> A path known to be present on every document is never
        /// absent, so normalising it would buy nothing and cost the plain path form that an index is
        /// defined on. Those are grouped as they were. What is known comes from the container rather
        /// than from a list here: the service guarantees <c>id</c>, <c>_ts</c> and <c>_etag</c>, and a
        /// declared schema's <c>required</c> says the same of an application's own property — which
        /// the list this replaced could never have reached.
        /// </para>
        /// </remarks>
        static string GroupingKey(CosmosPath path, JsonFactSet facts, string? rendered = null)
        {
            // What the projection beneath rendered, where that is not the path. A guarded accessor
            // already answers null where the property is absent, so it needs no second guard.
            if (rendered is not null)
                return rendered;

            var text = path.ToString();

            return IsAlwaysPresent(path, facts) ? text : $"(IS_DEFINED({text}) ? {text} : null)";
        }

        /// <summary>
        /// Determines whether a path is known to be present on every document.
        /// </summary>
        static bool IsAlwaysPresent(CosmosPath path, JsonFactSet facts) =>
            Metadata.CosmosDocumentPaths.From(path) is JsonDocumentPath document &&
            facts.Knows(new JsonFact(document, new JsonClaim.Present()));

        string Render(AggregateCall call, IReadOnlyList<CosmosPath?> fields)
        {
            var arguments = call.getArgList();

            // COUNT(*) is spelled COUNT(1); Cosmos has no star form.
            if (arguments.size() == 0)
                return FunctionName(call) + "(1)";

            var index = ((java.lang.Integer)arguments.get(0)).intValue();
            if (index < 0 || index >= fields.Count || fields[index] is null)
                throw new CosmosTranslationException("An aggregate argument does not resolve to a document path.");

            return $"{FunctionName(call)}({fields[index]})";
        }

    }

}
