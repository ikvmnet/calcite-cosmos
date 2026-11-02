using System;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Everything a write needs that is known while the plan is being built.
    /// </summary>
    /// <remarks>
    /// The write path's counterpart to <c>CosmosQuery</c>, and deliberately not one: there is no
    /// statement here, because Cosmos SQL has no DML and a write goes through item CRUD instead. What
    /// is constant-folded into the plan is a description of the rows, not text.
    /// </remarks>
    /// <param name="Operation">What to do with each row.</param>
    /// <param name="ColumnNames">The table's field names, the document column first. An update's input rows carry one further value per <paramref name="UpdateColumnNames"/> entry, after these.</param>
    /// <param name="PartitionKeyPaths">The container's declared partition key paths, in policy form and outermost first.</param>
    /// <param name="UpdateColumnNames">The columns an <c>UPDATE</c> sets, in the order their new values trail the row, or <c>null</c> for any other operation.</param>
    /// <param name="PartitionKeyValues">The partition key a whole-partition delete empties, one value per declared path, or <c>null</c> for any other operation.</param>
    /// <param name="StoresSpelled">Whether the container's target stores a string in another spelling than it is sent, the
    /// emulator's, so that a row whose <c>id</c> or partition key holds U+0000 or U+FDD0 is not addressed by it.</param>
    public sealed record CosmosWrite(CosmosWriteOperation Operation, string[] ColumnNames, string[] PartitionKeyPaths, string[]? UpdateColumnNames = null, object?[]? PartitionKeyValues = null, bool StoresSpelled = false)
    {

        /// <summary>
        /// Gets the table's field names, the document column first.
        /// </summary>
        public string[] ColumnNames { get; } = ColumnNames ?? throw new ArgumentNullException(nameof(ColumnNames));

        /// <summary>
        /// Gets the container's declared partition key paths, in policy form and outermost first.
        /// </summary>
        public string[] PartitionKeyPaths { get; } = PartitionKeyPaths ?? throw new ArgumentNullException(nameof(PartitionKeyPaths));

    }

}
