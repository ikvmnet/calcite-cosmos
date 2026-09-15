using Apache.Calcite.Cosmos.Adapter.Metadata;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.metadata;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Answers the planner's row count for a Cosmos filter that addresses a single document, where
    /// Calcite would otherwise guess.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RelMdRowCount</c> has a handler for <c>Filter</c> that estimates a predicate at a fixed
    /// selectivity per conjunct — 0.15 for an equality, 0.5 for a comparison — and it is reached by
    /// dispatch on the node's class, so a <see cref="CosmosFilter"/> overriding
    /// <c>estimateRowCount</c> is never consulted. For a by-id lookup the guess is badly wrong in a way
    /// that matters: over a container of a thousand documents, <c>id = 'x' AND pk = 'y'</c> is estimated
    /// at 22.5 rows rather than the one it can return.
    /// </para>
    /// <para>
    /// <b>What that costs is not the filter.</b> It is everything above it. A projection and a converter
    /// are priced on the rows they carry, so a plan that holds a conjunct back and applies it in process
    /// is charged for twice the rows crossing the convention boundary — about 20 units on that
    /// thousand-document container — against the two request units the point read it enables actually
    /// saves. The cheaper plan loses by a margin composed entirely of rows that do not exist. Correcting
    /// the count is what lets the measured charge decide instead, and it needs no conversion constant
    /// between request units and Calcite's own: with both plans carrying one row, what is left to compare
    /// is the mechanism.
    /// </para>
    /// <para>
    /// This is a <em>fact</em> rather than a preference — <c>id</c> is unique within a partition, so a
    /// complete partition key and a pinned <c>id</c> identify at most one document, whatever else the
    /// predicate says. See <see cref="CosmosPartitionKeyExtractor.PinsAtMostOneDocument"/>.
    /// </para>
    /// <para>
    /// Done by subclassing the query rather than by registering a metadata provider. A provider is the
    /// usual route, but Calcite reaches one through Janino-generated dispatch that has to name the
    /// handler class from generated Java source, which is not a thing to rely on for a handler written
    /// in C#. Overriding the query is the same interception one layer out, and needs no code generation.
    /// </para>
    /// </remarks>
    public class CosmosRelMetadataQuery : RelMetadataQuery
    {

        /// <summary>
        /// Installs this query on a cluster, so that plans over Cosmos tables are costed with it.
        /// </summary>
        /// <remarks>
        /// A host that builds its own cluster calls this before planning. Nothing is changed for a
        /// cluster that never sees a Cosmos filter: every other node's row count is the one Calcite
        /// would have given.
        /// </remarks>
        /// <param name="cluster">The cluster to install on.</param>
        public static void Install(RelOptCluster cluster)
        {
            if (cluster is null)
                return;

            cluster.setMetadataQuerySupplier(new Supplier());
            cluster.invalidateMetadataQuery();
        }

        /// <summary>
        /// Hands the cluster a fresh query, which is what Calcite expects of the supplier.
        /// </summary>
        sealed class Supplier : java.util.function.Supplier
        {

            public object get() => new CosmosRelMetadataQuery();

        }

        /// <inheritdoc />
        public override java.lang.Double getRowCount(RelNode rel)
        {
            if (rel is CosmosFilter filter &&
                filter.getConvention() is CosmosConvention convention &&
                CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out _) &&
                CosmosPartitionKeyExtractor.PinsAtMostOneDocument(filter.getCondition(), fields, convention.Container, CosmosImplementor.DefaultRootAlias))
                return java.lang.Double.valueOf(1d);

            return base.getRowCount(rel);
        }

    }

}
