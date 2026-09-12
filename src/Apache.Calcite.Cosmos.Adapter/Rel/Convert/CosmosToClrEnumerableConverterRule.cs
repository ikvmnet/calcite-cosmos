using Apache.Calcite.Extensions.Adapter.Enumerable;

using java.util.function;

using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Rule to convert a relational expression from <see cref="CosmosConvention"/> to
    /// <see cref="ClrEnumerableConvention"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a <see cref="CosmosConverterRule"/>: that base is for rules converting a standard operator
    /// <em>into</em> the Cosmos convention, and carries the obligation to decline anything Cosmos cannot
    /// express. This one converts out, and has nothing to decline. Every subtree in the convention got
    /// there by a rule that already established it renders — the operators that do not are the ones that
    /// were never converted in.
    /// </para>
    /// <para>
    /// It is the only route out, and there is deliberately no second one into Calcite's own
    /// <c>EnumerableConvention</c>: rows leave the statement as CLR rows, and a plan that wants linq4j
    /// gets there by the converter <see cref="ClrEnumerableConvention"/> already has.
    /// </para>
    /// <para>
    /// <b>One rule now covers both ways of reading the rows.</b> While the pulled and awaiting
    /// conventions were separate this rule had a sibling it deliberately lacked, and the absence was the
    /// gate that kept a Cosmos plan from being read synchronously. There is one convention now, so the
    /// gate is gone and the choice belongs to whoever calls the root — see
    /// <see cref="CosmosToClrEnumerableConverter"/> for what the synchronous side costs.
    /// </para>
    /// </remarks>
    public class CosmosToClrEnumerableConverterRule : ConverterRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose nodes will be converted.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosToClrEnumerableConverterRule Create(CosmosConvention convention)
        {
            return (CosmosToClrEnumerableConverterRule)Config.INSTANCE
                .withConversion(typeof(RelNode), convention, ClrEnumerableConvention.Instance, "CosmosToClrEnumerableConverterRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosToClrEnumerableConverterRule>(c => new CosmosToClrEnumerableConverterRule(c)))
                .toRule(typeof(CosmosToClrEnumerableConverterRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosToClrEnumerableConverterRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            return new CosmosToClrEnumerableConverter(rel.getCluster(), rel.getTraitSet().replace(getOutConvention()), rel);
        }

    }

}
