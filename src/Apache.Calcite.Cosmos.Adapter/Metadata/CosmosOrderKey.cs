using org.apache.calcite.rel;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// One key of a sort the service may be asked to compute: what it orders, and the direction and null placement
    /// the plan asked for.
    /// </summary>
    /// <remarks>
    /// The collation as Calcite wrote it, strict directions, clustering and an unspecified placement included, so
    /// that what each of those means is decided once, by <see cref="CosmosServiceOrder"/>.
    /// </remarks>
    /// <param name="Operand">What the key orders.</param>
    /// <param name="Collation">The key's direction and null placement.</param>
    public sealed record CosmosOrderKey(CosmosOrderOperand Operand, RelFieldCollation Collation);

}
