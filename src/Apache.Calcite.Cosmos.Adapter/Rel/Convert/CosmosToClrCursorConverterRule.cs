using Apache.Calcite.Extensions.Adapter.Cursor;

using java.util.function;

using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Rule to convert a relational expression from <see cref="CosmosConvention"/> to
    /// <see cref="ClrCursorConvention"/>.
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
    /// <b>It is the only route out, and it leads into <see cref="ClrCursorConvention"/> alone.</b> There is
    /// no converter from this convention into <c>ClrEnumerableConvention</c> or into Calcite's own
    /// <c>EnumerableConvention</c>. Rows leave the statement as a cursor, and a plan that wants them in
    /// another convention gets there higher in the plan, through the converters that convention and the
    /// cursor convention already have between them. The adapter's concern ends at the cursor.
    /// </para>
    /// <para>
    /// <b>One rule covers both ways of reading the rows.</b> A plan of the cursor convention is opened
    /// synchronously or with await, as its caller chooses, and advanced either way on every read — see
    /// <see cref="CosmosToClrCursorConverter"/> for what the synchronous side costs.
    /// </para>
    /// </remarks>
    public class CosmosToClrCursorConverterRule : ConverterRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose nodes will be converted.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosToClrCursorConverterRule Create(CosmosConvention convention)
        {
            return (CosmosToClrCursorConverterRule)Config.INSTANCE
                .withConversion(typeof(RelNode), convention, ClrCursorConvention.Instance, "CosmosToClrCursorConverterRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosToClrCursorConverterRule>(c => new CosmosToClrCursorConverterRule(c)))
                .toRule(typeof(CosmosToClrCursorConverterRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosToClrCursorConverterRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            return new CosmosToClrCursorConverter(rel.getCluster(), rel.getTraitSet().replace(getOutConvention()), rel);
        }

    }

}
