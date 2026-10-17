namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A comparison of a path with a parameter, written into a <c>WHERE</c> as it is, whose exactness at the service the
    /// parameter's value decides: the three ways it can be sent, and what the reader rechecks where it is not exact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why there are three.</b> The service compares a path's strings by code point and Calcite by UTF-16 code unit,
    /// and the two agree against a value below U+D800 whatever the path holds. Such a parameter is an anchor
    /// (<see cref="Metadata.CosmosServiceOrder.IsAnchor"/>), and <see cref="Exact"/> keeps exactly Calcite's rows.
    /// Against any other value the statement is sent with <see cref="Weakened"/> in its place — a bound every string
    /// Calcite keeps satisfies at the service (<see cref="Metadata.CosmosServiceOrder.LowerBound"/> and
    /// <see cref="Metadata.CosmosServiceOrder.UpperBound"/>) — or with <see cref="Guarded"/>, the type test, where no
    /// string bounds them; and the reader keeps only the rows whose <see cref="Member"/> compares with the value as
    /// Calcite compares it.
    /// </para>
    /// <para>
    /// <b>Only a top-level conjunct of a <c>WHERE</c>.</b> A superset of one conjunct, rechecked, is exact for the
    /// conjunction only where the other conjuncts are the service's own; under an <c>OR</c> or a <c>NOT</c> it would not
    /// be, and the translator refuses the comparison there, as it did every such comparison before.
    /// </para>
    /// </remarks>
    /// <param name="Parameter">The name the exact comparison binds the value under.</param>
    /// <param name="Bound">The name the weakened comparison binds the bound under, written when the value arrives.</param>
    /// <param name="Operator">The comparison, with the path on its left: <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c> or <c>&lt;=</c>.</param>
    /// <param name="Anchor">What the value has to be for <paramref name="Exact"/> to keep exactly Calcite's rows: the anchor
    /// <see cref="Metadata.CosmosServiceOrder.Compare"/> named, tested by <see cref="Metadata.CosmosServiceOrder.IsAnchor"/>.</param>
    /// <param name="Expression">What the service compares: the path, as the statement names it.</param>
    /// <param name="Exact">The comparison as the statement carries it.</param>
    /// <param name="Weakened">The comparison against the bound, which keeps a superset of Calcite's rows.</param>
    /// <param name="Guarded">The type test alone, sent where the value has no bound.</param>
    /// <param name="Member">The member of a returned row holding <paramref name="Expression"/>'s value, which the reader
    /// rechecks; assigned when the statement is built, and <c>null</c> until then.</param>
    public sealed record CosmosOrderComparison(string Parameter, string Bound, string Operator, Metadata.CosmosOrderAnchor Anchor, string Expression, string Exact, string Weakened, string Guarded, string? Member = null);

}
