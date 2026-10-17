using Apache.Calcite.Cosmos.Adapter.Metadata;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A <c>MIN</c> or <c>MAX</c> the service computes over values whose order the plan proved only around anchors: which
    /// member of the aggregate's row holds it, and the statement that reads its argument again where it is no anchor.
    /// </summary>
    /// <remarks>
    /// An extreme that is an anchor is Calcite's: every value of its partition lies at or within it in the partition's
    /// order, the SDK keeps the least or greatest of the partitions' in Calcite's, and an anchor parts every value from
    /// itself alike in both. Any other answer may not be, and the argument is read again: in its order where its path is
    /// indexed, which may stop at the first anchor — every value past an anchor in the stream lies past it in Calcite's
    /// order too — and otherwise whole.
    /// </remarks>
    /// <param name="Member">The member of the aggregate's row holding the extreme.</param>
    /// <param name="Max">Whether it is a <c>MAX</c>.</param>
    /// <param name="Kind">How Calcite compares the argument's values.</param>
    /// <param name="Anchor">What the extreme has to be to be Calcite's.</param>
    /// <param name="Reread">The statement reading the argument's values, as <c>SELECT VALUE</c>, under the aggregate's
    /// own <c>FROM</c> and <c>WHERE</c>.</param>
    /// <param name="Ordered">Whether <paramref name="Reread"/> orders the values toward the extreme, so that the first
    /// anchor ends it.</param>
    public sealed record CosmosOrderExtreme(string Member, bool Max, CosmosOrderedKeyKind Kind, CosmosOrderAnchor Anchor, string Reread, bool Ordered);

}
