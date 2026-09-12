using System;
using System.Linq.Expressions;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Client;

using Apache.Calcite.Extensions.Adapter.Enumerable;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.runtime;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Relational operator that converts a tree of <see cref="CosmosConvention"/> nodes into a
    /// <see cref="ClrEnumerableConvention"/> result by executing the generated Cosmos SQL against the
    /// container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only way out of the Cosmos convention, and the point at which a pushed-down subtree stops being
    /// a statement and becomes rows. Everything below it contributed to one Cosmos SQL statement; this
    /// renders that statement, executes it, and reads the JSON value each row arrives as into the row the
    /// plan above expects.
    /// </para>
    /// <para>
    /// <b>The awaiting body is the real one, and the pulled body is a bridge over it.</b> The v3 Cosmos
    /// SDK exposes no synchronous data-plane API — a page of results arrives only by awaiting
    /// <c>FeedIterator.ReadNextAsync</c> — so there is nothing for <see cref="Implement"/> to call that
    /// does not wait on a page. It is written as the delegation
    /// <see cref="ClrEnumerableRelImplementor.Pulled"/> exists for, which is the shape
    /// <see cref="ClrEnumerableRel"/> prescribes for an adapter whose client is asynchronous.
    /// </para>
    /// <para>
    /// <b>What that costs is a thread per row, and it is now a caller's choice rather than a refusal.</b>
    /// While the two conventions were separate, the absence of a converter into the pulled one was what
    /// kept sync-over-async out of a plan: a query over a Cosmos table simply did not plan unless the root
    /// was asked for asynchronously. One convention leaves no such gate — the plan is the same either way
    /// and the kind is chosen by whoever calls the root — so the cost has moved from a plan that does not
    /// exist to a thread that blocks at the leaf. A host that reads a Cosmos table through
    /// <see cref="ClrEnumerableRelImplementor.ImplementRoot"/> rather than
    /// <see cref="ClrEnumerableRelImplementor.ImplementRootAsync"/> pays it, and pays it per row.
    /// </para>
    /// <para>
    /// Rendering the statement, resolving the partition key and building the row builder all happen here,
    /// once, while the statement is prepared. Only the sequence and the row builder are on the per-row
    /// path.
    /// </para>
    /// </remarks>
    public class CosmosToClrEnumerableConverter : ConverterImpl, ClrEnumerableRel
    {

        static readonly System.Reflection.MethodInfo ReadAsyncMethod = typeof(CosmosSequences).GetMethod(nameof(CosmosSequences.ReadAsync))
            ?? throw new InvalidOperationException($"'{nameof(CosmosSequences.ReadAsync)}' is missing from {nameof(CosmosSequences)}.");

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traits">The trait set, which must carry the CLR convention.</param>
        /// <param name="input">The Cosmos subtree being converted.</param>
        public CosmosToClrEnumerableConverter(RelOptCluster cluster, RelTraitSet traits, RelNode input) :
            base(cluster, ConventionTraitDef.INSTANCE, traits, input)
        {

        }

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosToClrEnumerableConverter(getCluster(), traitSet, (RelNode)sole(inputs));
        }

        /// <inheritdoc />
        public override RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var cost = base.computeSelfCost(planner, mq);

            return cost == null ? null! : cost.multiplyBy(ClrEnumerableConvention.CostMultiplier);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The bridge, for the reason the type's own remarks give: there is no synchronous read to write
        /// here, so this is the awaiting body read across, and it blocks a thread per row.
        /// </remarks>
        public ClrEnumerableResult Implement(ClrEnumerableRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            return implementor.Pulled(ImplementAsync(implementor, pref));
        }

        /// <inheritdoc />
        public ClrAsyncEnumerableResult ImplementAsync(ClrEnumerableRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            var input = getInput();

            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());
            var rowType = physType.RowType;

            var (query, fields, readings) = CosmosConverters.GenerateQuery(input, implementor.RexBuilder);

            // A point read — one, or a batch of many — returns documents rather than the object the
            // statement constructs, so the two paths need different row builders; and the read needs
            // every output field to address a path, which only this knows. Where one does not, the
            // read is withdrawn and the statement is executed as the query it already is.
            var rowBuilder = query.PointReadId is null && query.PointReadIds is null
                ? null
                : CosmosConverters.DocumentRowBuilder(physType, getRowType(), fields);

            if ((query.PointReadId is not null || query.PointReadIds is not null) && rowBuilder is null)
                query = query with { PointReadId = null, PointReadIds = null };

            rowBuilder ??= CosmosConverters.RowBuilder(physType, getRowType(), readings);

            Hook.QUERY_PLAN.run(query.Sql);

            return implementor.ResultAsync(physType,
                Expression.Call(null,
                    ReadAsyncMethod.MakeGenericMethod(rowType),
                    CosmosConverters.ExecutorExpression(input, implementor.Root),
                    Expression.Constant(query),
                    rowBuilder,
                    // Calcite's cancellation is an AtomicBoolean on the DataContext rather than a token,
                    // and polling one between pages would not interrupt a page in flight. Enumerating the
                    // result is what cancels this, by not asking for the next page.
                    Expression.Constant(CancellationToken.None)));
        }

    }

}
