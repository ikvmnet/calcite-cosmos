using Apache.Calcite.Cosmos.Facts;

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
        static bool IsSupported(CosmosConvention convention, Aggregate aggregate)
        {
            if (aggregate.getGroupType() != Aggregate.Group.SIMPLE)
                return false;

            return CanPush(convention, aggregate.getInput(), aggregate.getGroupSet(), aggregate.getAggCallList(), container => Facts(aggregate, container));
        }

        /// <summary>
        /// Returns what every row an aggregate groups is known to satisfy: the filters beneath it (L1), the same each
        /// time it is asked of the same aggregate. See <see cref="Metadata.CosmosRowFacts"/>.
        /// </summary>
        static JsonFactSet Facts(Aggregate aggregate, Metadata.CosmosContainerMetadata? container) =>
            Metadata.CosmosRowFacts.For(aggregate, container, OracleLicence.GroupBy);

        /// <summary>
        /// Determines whether a simple aggregation over the given input, keys and calls can be
        /// rendered faithfully. Shared with <see cref="CosmosAggregateSplitRule"/>, which asks the
        /// question of the bottom half it would create rather than of a node that exists.
        /// </summary>
        /// <param name="convention">The convention converting it, whose container the input must read
        /// (<see cref="CosmosConvention.Owns"/>).</param>
        /// <param name="input">The rows aggregated.</param>
        /// <param name="groupSet">The keys.</param>
        /// <param name="calls">The calls.</param>
        /// <param name="facts">What every row of the input is known to satisfy, given the container its walk found; by
        /// default what the filters beneath establish (L1), read afresh.</param>
        internal static bool CanPush(CosmosConvention convention, RelNode input, org.apache.calcite.util.ImmutableBitSet groupSet, java.util.List calls, System.Func<Metadata.CosmosContainerMetadata?, JsonFactSet>? facts = null)
        {
            if (CosmosImplementor.TryBindOutput(input, out var fields, out var readings, out _, out var written, out var container) == false
                || convention.Owns(container) == false)
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
            //
            // Nor a WHERE holding a comparison a parameter's value decides: where the value is no anchor the reader sends
            // a weaker comparison and rechecks the rows it returns, and a grouping or a DISTINCT leaves no row of the
            // comparison's to recheck (CosmosClauses.Recheck).
            if ((written & (CosmosClauses.OrderBy | CosmosClauses.RowLimit | CosmosClauses.Distinct | CosmosClauses.Recheck)) != 0)
                return false;

            // Whether each key groups as its column reads is asked of the walk's own binding, readings
            // and container -- what the node asks of the implementor the same walk built -- and of what
            // the rows grouped are known to satisfy, which the node is handed. See
            // CosmosAggregate.GroupsAsTheColumnReads.
            var known = facts?.Invoke(container) ?? Metadata.CosmosRowFacts.Of(input, container, OracleLicence.GroupBy);
            var translator = new Sql.CosmosRexTranslator(input.getCluster().getRexBuilder(), fields, new Sql.CosmosParameterList(), known, null, container, readings);

            var groupKeys = groupSet.asList();
            for (var i = 0; i < groupKeys.size(); i++)
            {
                var key = (java.lang.Integer)groupKeys.get(i);

                if (Resolves(fields, key) == false || CosmosAggregate.GroupsAsTheColumnReads(translator, readings, input.getRowType(), key.intValue()) == false)
                    return false;
            }

            var inputRowType = input.getRowType();

            for (var i = 0; i < calls.size(); i++)
            {
                var call = (AggregateCall)calls.get(i);
                if (CosmosAggregate.CanImplement(call, inputRowType, fields, readings, known, groupSet.isEmpty() == false) == false)
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
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosAggregateRule Create(CosmosConvention convention)
        {
            return (CosmosAggregateRule)Config.INSTANCE
                .withConversion(typeof(Aggregate), new DelegatePredicate<Aggregate>(a => IsSupported(convention, a)), Convention.NONE, convention, "CosmosAggregateRule")
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
        public override RelNode? convert(RelNode rel)
        {
            var aggregate = (Aggregate)rel;

            if (CosmosImplementor.TryBindOutput(aggregate.getInput(), out _, out _, out _, out _, out var container) == false)
                return null;

            // Built under the facts the legality test decided under, which the node asks again.
            return new CosmosAggregate(
                aggregate.getCluster(),
                aggregate.getTraitSet().replace(@out),
                convert(aggregate.getInput(), aggregate.getInput().getTraitSet().replace(@out)),
                aggregate.getGroupSet(),
                aggregate.getGroupSets(),
                aggregate.getAggCallList(),
                Facts(aggregate, container));
        }

    }

}
