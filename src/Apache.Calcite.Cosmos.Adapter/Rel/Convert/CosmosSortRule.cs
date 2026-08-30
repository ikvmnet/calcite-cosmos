using System.Collections.Generic;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Sort"/> to a <see cref="CosmosSort"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unusually for a conversion rule, legality here depends on container metadata rather than on
    /// the plan alone. Cosmos requires a composite index for any <c>ORDER BY</c> over two or more
    /// properties, and rejects the query outright when none matches. Converting such a sort would
    /// therefore produce a statement the service refuses, so the rule consults the container's
    /// indexing policy before firing.
    /// </para>
    /// <para>
    /// A single-key sort needs no composite index. It may be slower without one, but it runs.
    /// </para>
    /// </remarks>
    public class CosmosSortRule : CosmosConverterRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosSortRule Create(CosmosConvention convention)
        {
            return (CosmosSortRule)Config.INSTANCE
                .withConversion(typeof(Sort), new DelegatePredicate<Sort>(s => IsSupported(convention, s)), Convention.NONE, convention, "CosmosSortRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosSortRule>(c => new CosmosSortRule(c)))
                .toRule(typeof(CosmosSortRule));
        }

        /// <summary>
        /// Determines whether a sort can be pushed into the given container.
        /// </summary>
        /// <remarks>
        /// The binding is derived by walking the input, not read off its row type by name. Above a
        /// projection the names are aliases, and binding them by name invents paths the container does
        /// not have — which let this rule fire on a sort key that implementation then refused, and, worse,
        /// checked a multi-key sort against the composite indexes using paths like <c>/u</c>. Deciding on
        /// the same binding implementation will use is what makes the answer here final.
        /// </remarks>
        static bool IsSupported(CosmosConvention convention, Sort sort)
        {
            if (CosmosImplementor.TryBindOutput(sort.getInput(), out var fields) == false)
                return false;

            if (CosmosSort.TryResolveSortKeys(sort.getCollation(), fields, sort.getInput().getRowType(), CosmosImplementor.DefaultRootAlias, NonNullFields(sort), out var keys, out _) == false)
                return false;

            return convention.Container.IsSortSupported(keys);
        }

        /// <summary>
        /// Reads the fields the plan guarantees are never null over the sort's input.
        /// </summary>
        /// <remarks>
        /// Asked here and handed to the node, rather than asked twice. The answer depends on which
        /// equivalent of the input the metadata is asked about, so the rule and the node have to be
        /// deciding on the same one — the same reason the binding above is derived by walking the
        /// input rather than read off its row type.
        /// </remarks>
        static IReadOnlyList<int> NonNullFields(Sort sort)
        {
            return CosmosSort.FindNonNullFields(sort.getInput(), sort.getCluster().getMetadataQuery());
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosSortRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var sort = (Sort)rel;

            return new CosmosSort(
                sort.getCluster(),
                sort.getTraitSet().replace(@out),
                convert(sort.getInput(), sort.getInput().getTraitSet().replace(@out)),
                sort.getCollation(),
                sort.offset,
                sort.fetch,
                NonNullFields(sort));
        }

    }

}
