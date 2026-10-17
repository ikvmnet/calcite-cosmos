using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The answer to whether the service may compute an order, and for a sort, which keys it is handed.
    /// </summary>
    /// <param name="Kind">Whether it is proved, and when.</param>
    /// <param name="Reason">Why, in words a report can group by.</param>
    /// <param name="Written">For a sort proved or anchored, the positions of the keys the service is handed, in order: a
    /// key the facts make one value over every row, and a key that repeats an earlier one, is not among them. Empty
    /// otherwise.</param>
    /// <param name="Anchors">For an anchored extreme or comparison, what its one value has to be to be Calcite's: the
    /// extreme's answer, or the parameter's value, which makes the comparison exact. Empty otherwise; a sort's are its
    /// keys'.</param>
    /// <param name="Keys">For a sort proved or anchored, how each key in <paramref name="Written"/> orders, as decided
    /// here: how Calcite compares its values, its direction and null placement as Calcite runs them, and what a value has
    /// to be to certify the rows around it — <see cref="CosmosOrderAnchor.None"/> on a key whose order is proved. The
    /// member a returned row holds it in is the statement's to choose, and is <c>null</c> here. Empty otherwise.</param>
    public sealed record CosmosOrderVerdict(CosmosOrderVerdictKind Kind, string Reason, IReadOnlyList<int> Written, IReadOnlyList<CosmosOrderAnchor> Anchors, IReadOnlyList<CosmosOrderedKey> Keys)
    {

        /// <summary>
        /// Returns a proof.
        /// </summary>
        /// <param name="reason">What proves it.</param>
        /// <param name="written">For a sort, the keys written.</param>
        /// <param name="keys">For a sort, how each written key orders.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Proved(string reason, IReadOnlyList<int>? written = null, IReadOnlyList<CosmosOrderedKey>? keys = null) =>
            new(CosmosOrderVerdictKind.Proved, reason, written ?? Array.Empty<int>(), Array.Empty<CosmosOrderAnchor>(), keys ?? Array.Empty<CosmosOrderedKey>());

        /// <summary>
        /// Returns a proof completed when the rows arrive.
        /// </summary>
        /// <param name="reason">What the plan could not prove, and the rows will.</param>
        /// <param name="anchors">For an extreme or a comparison, what its value has to be.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Anchored(string reason, IReadOnlyList<CosmosOrderAnchor> anchors) =>
            new(CosmosOrderVerdictKind.Anchored, reason, Array.Empty<int>(), anchors ?? throw new ArgumentNullException(nameof(anchors)), Array.Empty<CosmosOrderedKey>());

        /// <summary>
        /// Returns a sort proved when the rows arrive.
        /// </summary>
        /// <param name="reason">What the plan could not prove, and the rows will.</param>
        /// <param name="written">The keys written.</param>
        /// <param name="keys">How each written key orders, and what its values have to be.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Anchored(string reason, IReadOnlyList<int> written, IReadOnlyList<CosmosOrderedKey> keys) =>
            new(CosmosOrderVerdictKind.Anchored, reason, written ?? throw new ArgumentNullException(nameof(written)), Array.Empty<CosmosOrderAnchor>(), keys ?? throw new ArgumentNullException(nameof(keys)));

        /// <summary>
        /// Returns a refusal.
        /// </summary>
        /// <param name="reason">What is missing.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Refused(string reason) =>
            new(CosmosOrderVerdictKind.Refused, reason, Array.Empty<int>(), Array.Empty<CosmosOrderAnchor>(), Array.Empty<CosmosOrderedKey>());

        /// <summary>
        /// Gets whether the order is proved when the plan is made.
        /// </summary>
        public bool IsProved => Kind == CosmosOrderVerdictKind.Proved;

        /// <summary>
        /// Gets whether the order is proved as the rows arrive, by the anchors they hold.
        /// </summary>
        public bool IsAnchored => Kind == CosmosOrderVerdictKind.Anchored;

    }

}
