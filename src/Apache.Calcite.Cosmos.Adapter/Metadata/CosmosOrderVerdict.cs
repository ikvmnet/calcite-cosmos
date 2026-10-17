using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The answer to whether the service may compute an order, and for a sort, which keys it is handed.
    /// </summary>
    /// <param name="Kind">Whether it is proved.</param>
    /// <param name="Reason">Why, in words a report can group by.</param>
    /// <param name="Written">For a proved sort, the positions of the keys the service is handed, in order: a key the
    /// facts make one value over every row, and a key that repeats an earlier one, is not among them. Empty
    /// otherwise.</param>
    public sealed record CosmosOrderVerdict(CosmosOrderVerdictKind Kind, string Reason, IReadOnlyList<int> Written)
    {

        /// <summary>
        /// Returns a proof.
        /// </summary>
        /// <param name="reason">What proves it.</param>
        /// <param name="written">For a sort, the keys written.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Proved(string reason, IReadOnlyList<int>? written = null) =>
            new(CosmosOrderVerdictKind.Proved, reason, written ?? Array.Empty<int>());

        /// <summary>
        /// Returns a refusal.
        /// </summary>
        /// <param name="reason">What is missing.</param>
        /// <returns>The verdict.</returns>
        public static CosmosOrderVerdict Refused(string reason) =>
            new(CosmosOrderVerdictKind.Refused, reason, Array.Empty<int>());

        /// <summary>
        /// Gets whether the order is proved.
        /// </summary>
        public bool IsProved => Kind == CosmosOrderVerdictKind.Proved;

    }

}
