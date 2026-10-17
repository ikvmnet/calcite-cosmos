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
    /// <param name="Anchors">For an anchored order, what each value has to be to certify the rows around it: for a sort,
    /// one per key in <paramref name="Written"/>; for an extreme, one for its argument; for a comparison, one for the
    /// parameter's value, which makes the comparison exact. Empty otherwise.</param>
    public sealed record CosmosOrderVerdict(CosmosOrderVerdictKind Kind, string Reason, IReadOnlyList<int> Written, IReadOnlyList<CosmosOrderAnchor> Anchors)
    {

        /// <summary>
        /// Returns a proof.
        /// </summary>
        /// <param name="reason">What proves it.</param>
        /// <param name="written">For a sort, the keys written.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Proved(string reason, IReadOnlyList<int>? written = null) =>
            new(CosmosOrderVerdictKind.Proved, reason, written ?? Array.Empty<int>(), Array.Empty<CosmosOrderAnchor>());

        /// <summary>
        /// Returns a proof completed when the rows arrive.
        /// </summary>
        /// <param name="reason">What the plan could not prove, and the rows will.</param>
        /// <param name="anchors">What each value checked has to be.</param>
        /// <param name="written">For a sort, the keys written.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Anchored(string reason, IReadOnlyList<CosmosOrderAnchor> anchors, IReadOnlyList<int>? written = null) =>
            new(CosmosOrderVerdictKind.Anchored, reason, written ?? Array.Empty<int>(), anchors ?? throw new ArgumentNullException(nameof(anchors)));

        /// <summary>
        /// Returns a refusal.
        /// </summary>
        /// <param name="reason">What is missing.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Refused(string reason) =>
            new(CosmosOrderVerdictKind.Refused, reason, Array.Empty<int>(), Array.Empty<CosmosOrderAnchor>());

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
