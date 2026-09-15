using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Filter"/> to a <see cref="CosmosFilter"/>.
    /// </summary>
    /// <remarks>
    /// The rule declines any filter whose condition has no Cosmos equivalent, so that an
    /// untranslatable predicate is evaluated in-process rather than failing during
    /// implementation.
    /// </remarks>
    public class CosmosFilterRule : CosmosConverterRule
    {

        /// <summary>
        /// Returns the condition this rule would push: the caller's, with whatever the container
        /// declares already lowered into it.
        /// </summary>
        /// <remarks>
        /// A comparison against a type Cosmos has no equivalent for renders as nothing and takes the
        /// whole predicate with it. Where the container's declared facts say the stored form makes a
        /// string comparison mean the same thing, the lowered form is what both the legality test and
        /// the conversion see, so the rest of the rule needs no knowledge of any of it.
        /// </remarks>
        static RexNode Pushed(CosmosConvention convention, Filter filter, IReadOnlyList<CosmosPath?> fields) =>
            CosmosFactRewriter.Rewrite(
                filter.getCondition(), fields, convention.Container, CosmosImplementor.DefaultRootAlias, filter.getCluster().getRexBuilder());

        /// <summary>
        /// Determines whether a filter's condition can be rendered as Cosmos SQL.
        /// </summary>
        /// <remarks>
        /// Translation is attempted against a throwaway parameter list, using the binding derived by
        /// walking the input, which also reports what that subtree has already written. Deriving the
        /// binding from the row type instead would read alias names as document properties above a
        /// projection, and answer for paths the container does not have. The same walk reports how
        /// each field is read, which is what tells a view's text column from the path it binds to;
        /// without it a comparison over the column pushed as the raw comparison the accessor's own
        /// spelling is declined for (#83).
        /// </remarks>
        static bool IsTranslatable(CosmosConvention convention, Filter filter)
        {
            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out var written) == false)
                return false;

            // WHERE is evaluated before OFFSET/LIMIT, so onto a subtree that has taken a page this
            // filter would render into a statement that filters the container and pages the result,
            // where the plan asked for the page to be filtered. Implementation refuses it; deciding
            // here is what leaves the filter in process instead of failing the query.
            if ((written & CosmosClauses.RowLimit) != 0)
                return false;

            var condition = Pushed(convention, filter, fields);

            // What the container knows, closed under what this predicate proves. Safe to hand over
            // here and not in the split rule: this rule pushes the condition whole, so a conjunct that
            // licensed another is applied at the service beside it. Where only part of a predicate is
            // pushed that no longer holds, and the split rule is left alone until it can say which
            // conjuncts reached the service.
            var facts = convention.Container is CosmosContainerMetadata container
                ? container.Facts.Derive(CosmosFactExtractor.Extract(condition, fields, CosmosImplementor.DefaultRootAlias))
                : CosmosFactSet.Empty;

            var translator = new CosmosRexTranslator(filter.getCluster().getRexBuilder(), fields, new CosmosParameterList(), null, convention.Container, readings, facts);
            return translator.TryTranslate(condition, out _);
        }

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosFilterRule Create(CosmosConvention convention)
        {
            var rule = (CosmosFilterRule)Config.INSTANCE
                .withConversion(typeof(Filter), new DelegatePredicate<Filter>(f => IsTranslatable(convention, f)), Convention.NONE, convention, "CosmosFilterRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosFilterRule>(c => new CosmosFilterRule(c)))
                .toRule(typeof(CosmosFilterRule));

            // A rule is created per convention, and conversion needs the container the same way the
            // legality test does; the out trait carries it, but not without a cast at every use.
            rule._convention = convention;
            return rule;
        }

        CosmosConvention? _convention;

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosFilterRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var filter = (Filter)rel;

            // The converted filter carries the lowered condition, which is what puts a plain equality
            // where the rest of the adapter can already use it: the translator renders it, and
            // CosmosPartitionKeyExtractor recovers a partition key from it.
            var condition = _convention is CosmosConvention convention && CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out _, out _)
                ? Pushed(convention, filter, fields)
                : filter.getCondition();

            return new CosmosFilter(
                filter.getCluster(),
                filter.getTraitSet().replace(@out),
                convert(filter.getInput(), filter.getInput().getTraitSet().replace(@out)),
                condition);
        }

    }

}
