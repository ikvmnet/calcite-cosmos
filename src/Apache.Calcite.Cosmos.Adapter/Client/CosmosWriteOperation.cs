namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// What a write does to a document.
    /// </summary>
    public enum CosmosWriteOperation
    {

        /// <summary>
        /// Creates a document, failing where one with that <c>id</c> already exists in the partition.
        /// </summary>
        /// <remarks>
        /// Create rather than upsert, because that is what <c>INSERT</c> means. An upsert would make
        /// <c>INSERT</c> silently replace, which SQL reserves for <c>MERGE</c>; the service answers a
        /// duplicate with a conflict and the statement fails, which is the answer the caller asked for.
        /// </remarks>
        Insert,

        /// <summary>
        /// Deletes the document a row identifies.
        /// </summary>
        Delete,

        /// <summary>
        /// Deletes every document in one logical partition, without reading them.
        /// </summary>
        /// <remarks>
        /// The one write whose rows are never read: the predicate names the partition, so the rows
        /// the plan would have scanned are exactly the rows the service will remove, and scanning
        /// them to delete them one at a time is the cost this avoids. Gated on the account
        /// supporting it — see <c>CosmosContainerMetadata.SupportsPartitionKeyDelete</c>.
        /// </remarks>
        DeletePartition,

        /// <summary>
        /// Replaces the document a row identifies with the one the row's <c>SET</c> values describe.
        /// </summary>
        /// <remarks>
        /// A whole-document write, because that is what the statement says: SQL assigns whole values
        /// to named columns, and the settable column of this row model is the document column, which is
        /// the document. Carrying a targeted <c>SET</c> as a patch instead is a different operation
        /// with its own conditions — see <c>DESIGN.md</c> under <em>Updating</em> — and is not yet
        /// taken.
        /// </remarks>
        Update,

    }

}
