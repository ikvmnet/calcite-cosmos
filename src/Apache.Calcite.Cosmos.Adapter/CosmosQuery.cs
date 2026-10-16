using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A rendered Cosmos SQL statement together with the values bound to its parameters.
    /// </summary>
    /// <param name="Sql">The statement text.</param>
    /// <param name="Parameters">The bound parameter values.</param>
    /// <param name="PartitionKeyValues">
    /// The leading partition key values the predicate pinned, outermost first, or <c>null</c> where it
    /// pinned none. Supplying them restricts execution to the partitions holding that prefix instead of
    /// fanning out across every one — a complete key reaches a single logical partition, and a prefix of
    /// a hierarchical key reaches the subset under it. Either way it routes and does not filter.
    /// </param>
    /// <param name="PartitionKeyIsComplete">
    /// Whether <paramref name="PartitionKeyValues"/> covers every declared path. A prefix routes but
    /// does not identify a document, so a point read requires this and a query does not.
    /// </param>
    /// <param name="MaxItemCount">
    /// The most rows the statement can return, when the statement says so, otherwise <c>null</c>.
    /// <para>
    /// A page size rather than a limit: it caps how many rows a single round trip brings back and
    /// cannot change which rows the query yields. Without it a statement ending in <c>LIMIT 5</c>
    /// still fetches a full default page, and pays for the rows it then discards.
    /// </para>
    /// </param>
    /// <param name="PointReadId">
    /// The <c>id</c> to read directly when the statement is exactly a lookup by <c>id</c> and a
    /// complete partition key, otherwise <c>null</c>.
    /// <para>
    /// A point read costs about 1 RU where the same fetch as a query costs 2.3 at best and goes through
    /// the query engine. It applies no predicate and returns the document rather than a projection, so
    /// it is offered only where the statement asks for nothing a read cannot answer — a
    /// <c>DISTINCT</c>, an ordering, a page, a grouping, an aggregate, a traversal, or a conjunct
    /// the pinning filter did not write; see <see cref="CosmosImplementor.Build"/>, and
    /// <c>CosmosPartitionKeyExtractor.TryExtractPointRead</c> for the predicate condition.
    /// </para>
    /// </param>
    /// <param name="PointReadIds">
    /// The distinct <c>id</c>s to read as a batch of point reads when the statement is exactly a
    /// lookup by a set of <c>id</c>s and a complete partition key, otherwise <c>null</c>.
    /// <para>
    /// <c>ReadManyItemsAsync</c> is charged as point reads and, like one, applies no predicate and
    /// returns documents rather than a projection — so the same conditions gate it, one level up.
    /// Never set together with <see cref="PointReadId"/>.
    /// </para>
    /// </param>
    public readonly record struct CosmosQuery(string Sql, IReadOnlyList<CosmosParameter> Parameters, IReadOnlyList<object?>? PartitionKeyValues = null, int? MaxItemCount = null, string? PointReadId = null, bool PartitionKeyIsComplete = false, IReadOnlyList<string>? PointReadIds = null);

}
