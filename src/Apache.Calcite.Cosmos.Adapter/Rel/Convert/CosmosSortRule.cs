using System.Linq;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Sort"/> to a <see cref="CosmosSort"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two questions decide it, and neither is the rule's own.</b> Whether the order the service
    /// computes is one Calcite allows is asked of <see cref="Metadata.CosmosServiceOrder"/>, of the
    /// facts the rows sorted are known to satisfy, through the key resolution the node shares; and
    /// whether the service accepts the statement at all is asked of the container.
    /// </para>
    /// <para>
    /// Unusually for a conversion rule, the second depends on container metadata rather than on the
    /// plan alone. Measured on Azure, an <c>ORDER BY</c> over a path the indexing policy excludes is
    /// a 400 — <c>_etag</c> is excluded by default — and so is one over two or more properties with no
    /// composite index matching their directions or their exact reverse, a repeated key included.
    /// Converting such a sort would produce a statement the service refuses, so the rule consults the
    /// container's indexing policy before firing — see <see cref="Metadata.CosmosContainerMetadata.IsSortSupported"/>.
    /// The emulator accepts both, which is why neither shows there.
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
        /// The binding is derived by walking the input, not read off its row type by name, and the same
        /// walk reports which clauses the subtree has already written. Above a projection the names are
        /// aliases, and binding them by name invents paths the container does not have — which let this
        /// rule fire on a sort key that implementation then refused, and, worse,
        /// checked a multi-key sort against the composite indexes using paths like <c>/u</c>. Deciding on
        /// the same binding implementation will use is what makes the answer here final.
        /// </remarks>
        static bool IsSupported(CosmosConvention convention, Sort sort)
        {
            if (CosmosImplementor.TryBindOutput(sort.getInput(), out var fields, out var readings, out var candidates, out var written, out var container) == false
                || convention.Owns(container) == false)
                return false;

            // A statement has one ORDER BY, and its OFFSET/LIMIT is applied after it rather than
            // before. Onto a subtree that has written either, this sort is not the statement's: above
            // an ordering it is a second one, which implementation refuses; above a page it renders
            // into a statement that orders the container and then takes a page, where the plan asked
            // for the page to be taken and then ordered. The second is the reason to decide it here —
            // those are different rows, and nothing downstream would have noticed.
            if ((written & (CosmosClauses.OrderBy | CosmosClauses.RowLimit)) != 0)
                return false;

            // What every row the sort orders is known to satisfy: what the filters beneath establish
            // (L1), the ORDER BY being applied to the rows the WHERE kept. Decided once for this sort
            // and handed to the node, which asks the same question again of the same facts: whether the
            // order the service computes over these keys is one Calcite allows (CosmosServiceOrder),
            // and which keys it is handed.
            var facts = Facts(sort, container);

            if (CosmosSort.TryResolveSortKeys(sort.getCollation(), fields, readings, candidates, sort.getInput().getRowType(), CosmosImplementor.DefaultRootAlias, facts, out var keys, out _, out _, out _) == false)
                return false;

            return convention.Container.IsSortSupported(keys);
        }

        /// <summary>
        /// Returns what every row a sort orders is known to satisfy: the filters beneath it (L1), the same each time it is
        /// asked of the same sort. See <see cref="Metadata.CosmosRowFacts"/>.
        /// </summary>
        static JsonFactSet Facts(Sort sort, Metadata.CosmosContainerMetadata? container) =>
            Metadata.CosmosRowFacts.For(sort, container, OracleLicence.OrderBy);

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosSortRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode? convert(RelNode rel)
        {
            var sort = (Sort)rel;

            if (CosmosImplementor.TryBindOutput(sort.getInput(), out var fields, out var readings, out var candidates, out _, out var container) == false)
                return null;

            // Built under the facts the legality test decided under, which the node asks again; and saying whether the
            // order is completed as the rows arrive, which the digest shows.
            var facts = Facts(sort, container);
            var anchored = CosmosSort.TryResolveSortKeys(sort.getCollation(), fields, readings, candidates, sort.getInput().getRowType(), CosmosImplementor.DefaultRootAlias, facts, out _, out _, out _, out var ordered)
                && ordered.Any(k => k.Anchor != Metadata.CosmosOrderAnchor.None);

            return new CosmosSort(
                sort.getCluster(),
                sort.getTraitSet().replace(@out),
                convert(sort.getInput(), sort.getInput().getTraitSet().replace(@out)),
                sort.getCollation(),
                sort.offset,
                sort.fetch,
                facts,
                anchored);
        }

    }

}
