using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Extensions.Runtime;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Client
{
    /// <summary>
    /// Executes a rendered Cosmos SQL statement and opens the resulting JSON values as a cursor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the seam between the adapter and the Cosmos SDK. Everything above it — planning,
    /// translation, statement assembly — is independent of the service, which is what allows it to
    /// be tested without a client or a network.
    /// </para>
    /// <para>
    /// <b>A cursor rather than a sequence, because a page is fetched under the token of the advance that
    /// needs it.</b> An <c>IAsyncEnumerable</c> takes its token once, at <c>GetAsyncEnumerator</c>, and a
    /// reader's per-call token had nowhere to go; <see cref="IClrCursor.ReadAsync"/> takes one on every
    /// advance, and the advance that runs out of a page is the one whose token reaches
    /// <c>FeedIterator.ReadNextAsync</c>. It is the shape <c>ClrCursorConvention</c> composes, so what
    /// the executor opens is handed up the plan as it is.
    /// </para>
    /// <para>
    /// There is only an awaiting open, and that is a fact about Cosmos rather than a gap here: the v3 SDK
    /// has no synchronous data-plane API. A plan opened synchronously blocks for it, once, in
    /// <see cref="CosmosCursors.Open{TRow}"/>, and a cursor advanced synchronously blocks where a page
    /// has to be fetched — per page, not per row.
    /// </para>
    /// </remarks>
    public interface ICosmosQueryExecutor
    {

        /// <summary>
        /// Executes a query and opens its results.
        /// </summary>
        /// <param name="query">The statement and its bound parameters.</param>
        /// <param name="partitionKey">Restricts execution to a single logical partition when known, avoiding a fan-out across every physical partition.</param>
        /// <param name="cancellationToken">The token the open runs under. Each advance of the cursor brings its own.</param>
        /// <returns>The cursor, positioned before the first value, one value per row.</returns>
        /// <remarks>
        /// Opening is acquisition, as it is everywhere in the cursor convention: the statement is sent
        /// here, and the first page is what the open awaits. A value the cursor yields is standalone —
        /// cloned out of the page it arrived in — so it may be held after the cursor has moved on.
        /// </remarks>
        ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default);

    }

}
