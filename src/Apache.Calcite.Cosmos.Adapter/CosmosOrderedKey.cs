using Apache.Calcite.Cosmos.Adapter.Metadata;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// One key of an order the reader completes: where a returned row holds its value, how Calcite compares it, which way,
    /// where its nulls go, and what a value has to be to certify the rows around it.
    /// </summary>
    /// <param name="Member">The member of a returned row holding the key's stored value: one the statement selects for
    /// the purpose, under a name no column has, or for a <c>DISTINCT</c> the column itself; <c>null</c> until the statement
    /// is built, as <see cref="Metadata.CosmosServiceOrder.OrderBy"/> decides a key without one.</param>
    /// <param name="Kind">How Calcite compares the key's values.</param>
    /// <param name="Descending">Whether the key orders descending.</param>
    /// <param name="NullsFirst">Whether a value the column reads as null comes before every other, whatever the
    /// direction; otherwise after, which is also how an unspecified placement runs.</param>
    /// <param name="Anchor">What a value has to be to certify the rows around it; <see cref="CosmosOrderAnchor.None"/>
    /// where every value does, the key's order being proved.</param>
    public sealed record CosmosOrderedKey(string? Member, CosmosOrderedKeyKind Kind, bool Descending, bool NullsFirst, CosmosOrderAnchor Anchor);

}
