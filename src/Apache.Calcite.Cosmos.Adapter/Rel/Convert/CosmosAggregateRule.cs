using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts an <see cref="Aggregate"/> to a <see cref="CosmosAggregate"/>.
    /// </summary>
    /// <remarks>
    /// Only aggregates whose Cosmos semantics match SQL's are converted; see
    /// <see cref="CosmosAggregate"/> for what that excludes and why. Everything else is left to
    /// Calcite's own runtime.
    /// </remarks>
    public class CosmosAggregateRule : CosmosConverterRule
    {

        /// <summary>
        /// Determines whether an aggregate can be pushed down in full.
        /// </summary>
        /// <remarks>
        /// Bound the way implementation will bind, so the answer here is final — the same reasoning
        /// as <see cref="CosmosSortRule"/>. An input whose output is not addressable as document
        /// paths — another aggregate, a computed projection, an unnested element — cannot carry the
        /// grouping keys or the aggregate arguments, and firing anyway would only surface as a
        /// translation failure after the plan is chosen. An aggregate above a pushed aggregate is
        /// the case that matters: <c>AGGREGATE_EXPAND_DISTINCT_AGGREGATES</c> produces exactly that
        /// shape, and only the inner half is Cosmos's to take.
        /// </remarks>
        static bool IsSupported(Aggregate aggregate)
        {
            if (aggregate.getGroupType() != Aggregate.Group.SIMPLE)
                return false;

            return CanPush(aggregate.getInput(), aggregate.getGroupSet(), aggregate.getAggCallList());
        }

        /// <summary>
        /// Determines whether a simple aggregation over the given input, keys and calls can be
        /// rendered faithfully. Shared with <see cref="CosmosAggregateSplitRule"/>, which asks the
        /// question of the bottom half it would create rather than of a node that exists.
        /// </summary>
        internal static bool CanPush(RelNode input, org.apache.calcite.util.ImmutableBitSet groupSet, java.util.List calls)
        {
            if (CosmosImplementor.TryBindOutput(input, out var fields, out var readings, out _, out var written, out var container) == false)
                return false;

            // Binding passes through a sort and a DISTINCT, an aggregate cannot.
            //
            // Cosmos rejects GROUP BY with ORDER BY in one statement, and applies GROUP BY before
            // OFFSET/LIMIT, so grouping above a pushed row restriction would group the container
            // rather than the restriction. And a DISTINCT's output is paths, which is what lets a
            // *sort* join one in the same statement; an aggregate cannot join it, because there is
            // one SELECT to share and folding the outer in would clear the projection while leaving
            // the DISTINCT behind, counting the rows of the container rather than its distinct
            // values. That is the COUNT(DISTINCT) shape, whose outer half belongs outside the
            // convention.
            //
            // The same conditions implementation refuses, decided here instead — off the binding's
            // own walk rather than a second one beside it, so the two cannot drift.
            if ((written & (CosmosClauses.OrderBy | CosmosClauses.RowLimit | CosmosClauses.Distinct)) != 0)
                return false;

            var groupKeys = groupSet.asList();
            for (var i = 0; i < groupKeys.size(); i++)
            {
                var key = (java.lang.Integer)groupKeys.get(i);

                if (Resolves(fields, key) == false || GroupsAsTheColumnReads(input, fields, readings, container, key.intValue()) == false)
                    return false;
            }

            var inputRowType = input.getRowType();

            for (var i = 0; i < calls.size(); i++)
            {
                var call = (AggregateCall)calls.get(i);
                if (CosmosAggregate.CanImplement(call, inputRowType) == false)
                    return false;

                var arguments = call.getArgList();
                for (var j = 0; j < arguments.size(); j++)
                    if (Resolves(fields, (java.lang.Integer)arguments.get(j)) == false)
                        return false;
            }

            return true;
        }

        static bool Resolves(System.Collections.Generic.IReadOnlyList<Sql.CosmosPath?> fields, java.lang.Integer ordinal)
        {
            var index = ordinal.intValue();
            return index >= 0 && index < fields.Count && fields[index] is not null;
        }

        /// <summary>
        /// Determines whether grouping by the value the service holds at a key's path puts two rows in
        /// one group exactly where the column, as it is read back, says they are equal.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The service groups the stored value; SQL groups the column.</b> A column read as its
        /// declared type is the stored value, and the two agree. A <c>JSON_VALUE</c> read as text is a
        /// rendering of it, and the guard the grouping key carries keeps JSON's types apart where the
        /// rendering does not: a stored <c>30</c> and a stored <c>"30"</c> are two groups at the service
        /// and one value, <c>'30'</c>, in SQL. Measured on the emulator over the typed container (review
        /// probes J1, J2 and J13), <c>SELECT DISTINCT JSON_VALUE(c."DOC", '$.price')</c> returned
        /// <c>'30'</c> twice, its <c>GROUP BY</c> with <c>COUNT(*)</c> two groups of one where SQL has
        /// one of two, and <c>COUNT(DISTINCT …)</c> — whose inner half is this distinct — counted six
        /// where SQL counts five. The lookup join's key and the filter translator's comparison are the
        /// same rendering, and both already refuse it; this is the third reader of the reading.
        /// </para>
        /// <para>
        /// The exception is theirs too, asked of the same method: a container that declares the path a
        /// string outright, where the rendering is the stored value and a second JSON type cannot be
        /// there to be kept apart. A <c>JSON_QUERY</c> fragment has no such exception — its text is a
        /// serialisation, and whether the service holds two objects equal exactly where their compact
        /// texts are equal, in key order and number spelling, is not measured — so it is declined. The
        /// document column groups nothing either way: no two documents are equal, their <c>_rid</c>
        /// alone telling them apart.
        /// </para>
        /// <para>
        /// Declining costs the grouping and nothing else: it runs in process over what the projection
        /// beneath still sends.
        /// </para>
        /// </remarks>
        static bool GroupsAsTheColumnReads(RelNode input, System.Collections.Generic.IReadOnlyList<Sql.CosmosPath?> fields, System.Collections.Generic.IReadOnlyList<CosmosReading> readings, Metadata.CosmosContainerMetadata? container, int ordinal)
        {
            var reading = ordinal < readings.Count ? readings[ordinal] : CosmosReading.Typed;

            if (reading is CosmosReading.Typed or CosmosReading.Json)
                return true;

            if (reading != CosmosReading.Text || container is null)
                return false;

            var translator = new Sql.CosmosRexTranslator(input.getCluster().getRexBuilder(), fields, new Sql.CosmosParameterList(), null, container, readings);

            return translator.IsDeclaredStringOutright(org.apache.calcite.rex.RexInputRef.of(ordinal, input.getRowType()));
        }

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosAggregateRule Create(CosmosConvention convention)
        {
            return (CosmosAggregateRule)Config.INSTANCE
                .withConversion(typeof(Aggregate), new DelegatePredicate<Aggregate>(IsSupported), Convention.NONE, convention, "CosmosAggregateRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosAggregateRule>(c => new CosmosAggregateRule(c)))
                .toRule(typeof(CosmosAggregateRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosAggregateRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var aggregate = (Aggregate)rel;

            return new CosmosAggregate(
                aggregate.getCluster(),
                aggregate.getTraitSet().replace(@out),
                convert(aggregate.getInput(), aggregate.getInput().getTraitSet().replace(@out)),
                aggregate.getGroupSet(),
                aggregate.getGroupSets(),
                aggregate.getAggCallList());
        }

    }

}
