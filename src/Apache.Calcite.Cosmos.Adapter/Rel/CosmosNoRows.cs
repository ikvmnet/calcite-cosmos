using System;
using System.Linq.Expressions;

using Apache.Calcite.Cosmos.Adapter.Client;

using Apache.Calcite.Extensions.Adapter.Cursor;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// What a filter the facts graph proves keeps no document and fails on none answers with: no rows, and no request
    /// (<see cref="Convert.CosmosFilterReduceRule"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a <c>Values</c>, so that nothing beside it is pruned.</b> Calcite's prune-empty rules read an input as empty
    /// where it is a <c>Values</c> with no tuples, a subset holding one, or a relation whose maximum row count is 0, and
    /// answer a join, set operation, semi-join or lateral over it without reading the other side. Calcite itself reads that
    /// side, and fails where something in it fails; so an empty <c>Values</c> answered a query with no request where
    /// Calcite fails. This relation is no <c>Values</c> and states no maximum row count, so a host reads the side beside it
    /// as Calcite does.
    /// </para>
    /// <para>
    /// <b>What that costs.</b> Where the host prunes empty inputs, as the connection the README opens does, the other side
    /// of a join or set operation beside a reduced filter is read, one request, even where nothing in it can fail
    /// (<c>TODO.md</c>, <em>An empty side's sibling</em>). An aggregate over it is the host's, over no rows.
    /// </para>
    /// <para>
    /// <b>It estimates the rows of the filter it stands for</b>, not none, so that a choice beside it by cost, such as
    /// a join the adapter drives by its keys (<c>CosmosLookupJoin</c>), reads it as it read the filter. Its own cost is
    /// an empty <c>Values</c>'s, so it is chosen wherever one was. A lookup join driven by it reads nothing of the other
    /// side, as one driven by a side that matches nothing reads only that side (<c>TODO.md</c>, <em>A filter above a
    /// lookup join</em>).
    /// </para>
    /// <para>
    /// <b>It names the filter it stands for</b>, in its digest only, so that two reduced filters are two relations. One
    /// relation for every empty of a row type merged the sets of the filters it answered, and where one was stacked on
    /// the other the merged set held a filter over itself, beneath which the planner's transposes and merges built new
    /// filters without end until the stack overflowed.
    /// </para>
    /// </remarks>
    public class CosmosNoRows : AbstractRelNode, ClrCursorRel
    {

        static readonly System.Reflection.MethodInfo NoneMethod = typeof(CosmosCursors).GetMethod(nameof(CosmosCursors.None))
            ?? throw new InvalidOperationException($"'{nameof(CosmosCursors.None)}' is missing from {nameof(CosmosCursors)}.");

        /// <summary>
        /// Creates the relation in the cursor convention.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="rowType">The row type of the filter it answers for.</param>
        /// <param name="rows">The rows the planner estimates that filter keeps.</param>
        /// <param name="standsFor">The planner's identifier of that filter.</param>
        /// <returns>The relation.</returns>
        public static CosmosNoRows Create(RelOptCluster cluster, RelDataType rowType, double rows, int standsFor)
        {
            return new CosmosNoRows(cluster, cluster.traitSetOf(ClrCursorConvention.Instance), rowType, rows, standsFor);
        }

        readonly RelDataType _rowType;
        readonly double _rows;
        readonly int _standsFor;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the cursor convention.</param>
        /// <param name="rowType">The row type.</param>
        /// <param name="rows">The rows the planner estimates the filter it stands for keeps.</param>
        /// <param name="standsFor">The planner's identifier of the filter it stands for.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rowType"/> is <c>null</c>.</exception>
        public CosmosNoRows(RelOptCluster cluster, RelTraitSet traitSet, RelDataType rowType, double rows, int standsFor) :
            base(cluster, traitSet)
        {
            _rowType = rowType ?? throw new ArgumentNullException(nameof(rowType));
            _rows = rows;
            _standsFor = standsFor;
        }

        /// <inheritdoc />
        protected override RelDataType deriveRowType()
        {
            return _rowType;
        }

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosNoRows(getCluster(), traitSet, _rowType, _rows, _standsFor);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The row type is in the digest, as an empty <c>Values</c>'s is, and the filter it stands for, so that no two
        /// filters' are one relation. Neither is printed in a plan.
        /// </remarks>
        public override RelWriter explainTerms(RelWriter pw)
        {
            return base.explainTerms(pw)
                .itemIf("type", _rowType, pw.getDetailLevel() == SqlExplainLevel.DIGEST_ATTRIBUTES)
                .itemIf("for", java.lang.Integer.valueOf(_standsFor), pw.getDetailLevel() == SqlExplainLevel.DIGEST_ATTRIBUTES);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The filter's estimate, not none, so that no cost-based choice beside it reads it as empty.
        /// </remarks>
        public override double estimateRowCount(RelMetadataQuery mq)
        {
            return _rows;
        }

        /// <inheritdoc />
        public override RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            return planner.getCostFactory().makeCost(0d, 1d, 0d);
        }

        /// <inheritdoc />
        public DeriveMode getDeriveMode()
        {
            return DeriveMode.PROHIBITED;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Opens nothing and visits no input, so the awaiting open is this one, awaited.
        /// </remarks>
        public ClrCursorResult Implement(ClrCursorRelImplementor implementor, ClrCursorPrefer pref)
        {
            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());

            return implementor.Result(physType, Expression.Call(null, NoneMethod.MakeGenericMethod(physType.RowType)));
        }

    }

}
