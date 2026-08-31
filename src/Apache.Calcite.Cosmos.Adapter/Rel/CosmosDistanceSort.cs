using System;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// A projection ordered by <c>ST_DISTANCE</c>, implemented in the <see cref="CosmosConvention"/>
    /// calling convention as an ordinary <c>ORDER BY</c> over the distance expression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a node and not a <see cref="CosmosSort"/>.</b> An <c>ORDER BY</c> item must map to
    /// a document path — <c>ORDER BY UPPER(c.name)</c> is rejected with <em>"ORDER BY item expression
    /// could not be mapped to a document path"</em> — and a distance is the documented exception, served
    /// off the spatial index ascending or descending. But Calcite sorts by field ordinal, so ordering by
    /// an expression outside the select list arrives as three nodes: a projection that adds the
    /// distance, a sort on that column, and a projection that drops it again. <see cref="CosmosSort"/>
    /// resolves its keys to paths and a computed column has none, so the middle node declines and the
    /// whole ordering falls back to a sort in process over a container read whole. This node is the
    /// three collapsed into one, which is what lets the clause be written at all.
    /// </para>
    /// <para>
    /// It is the same shape <see cref="CosmosRank"/> collapses, and for a related but not identical
    /// reason. A scoring function <em>cannot</em> be projected, so the middle node is illegal there;
    /// a distance can be projected perfectly well, and what cannot be expressed is ordering by it
    /// through an alias. Cosmos has no name for a projected column, so the clause repeats the
    /// expression, and a node that repeats it has to own both ends.
    /// </para>
    /// <para>
    /// <b>A distance ordering is the only ordering.</b> Measured, <c>ORDER BY ST_DISTANCE(…), c.name</c>
    /// is rejected with the same document-path message as an ordering by any other expression: no index
    /// serves the pair. The rule therefore matches one collation key and refuses the composite rather
    /// than emitting a statement the service will not run.
    /// </para>
    /// <para>
    /// <b>Nothing above it addresses a path.</b> <c>CosmosImplementor.TryBindOutput</c> does not know
    /// this node, so a projection or a sort above one declines rather than being folded into a statement
    /// that already carries a projection and an <c>ORDER BY</c>. That is the same mechanism keeping
    /// <see cref="CosmosRank"/> safe, and it is load bearing: teaching the binding about this node
    /// without also teaching those operators to refuse would produce a plan whose implementation throws.
    /// </para>
    /// </remarks>
    public class CosmosDistanceSort : SingleRel, CosmosRel
    {

        readonly java.util.List _projects;
        readonly RexNode _distance;
        readonly bool _descending;
        readonly RexNode? _offset;
        readonly RexNode? _fetch;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="projects">The projected expressions, over the input's row type.</param>
        /// <param name="rowType">The output row type.</param>
        /// <param name="distance">The distance expression to order by, over the input's row type.</param>
        /// <param name="descending">Whether to order descending.</param>
        /// <param name="offset">The number of rows to skip, or <c>null</c>.</param>
        /// <param name="fetch">The maximum number of rows to return, or <c>null</c>.</param>
        public CosmosDistanceSort(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, java.util.List projects, RelDataType rowType, RexNode distance, bool descending, RexNode? offset, RexNode? fetch) :
            base(cluster, traitSet, input)
        {
            _projects = projects ?? throw new ArgumentNullException(nameof(projects));
            _distance = distance ?? throw new ArgumentNullException(nameof(distance));
            _descending = descending;
            _offset = offset;
            _fetch = fetch;

            this.rowType = rowType ?? throw new ArgumentNullException(nameof(rowType));
        }

        /// <summary>
        /// Gets the projected expressions.
        /// </summary>
        public java.util.List Projects => _projects;

        /// <summary>
        /// Gets the distance expression rows are ordered by.
        /// </summary>
        public RexNode Distance => _distance;

        /// <summary>
        /// Gets whether the ordering is descending.
        /// </summary>
        public bool Descending => _descending;

        /// <summary>
        /// Gets the number of rows to skip, or <c>null</c>.
        /// </summary>
        public RexNode? Offset => _offset;

        /// <summary>
        /// Gets the row limit, or <c>null</c>.
        /// </summary>
        public RexNode? Fetch => _fetch;

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosDistanceSort(getCluster(), traitSet, (RelNode)sole(inputs), _projects, getRowType(), _distance, _descending, _offset, _fetch);
        }

        /// <inheritdoc />
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            return base.computeSelfCost(planner, mq)?.multiplyBy(CosmosConvention.CostMultiplier);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The direction belongs in the digest. Two otherwise identical nodes ordering opposite ways
        /// render to different statements, and a planner that conflated them would keep whichever it
        /// saw first.
        /// </remarks>
        public override RelWriter explainTerms(RelWriter pw)
        {
            return base.explainTerms(pw)
                .item("projects", _projects)
                .item("distance", _distance)
                .item("dir", _descending ? "DESC" : "ASC")
                .itemIf("offset", _offset, _offset is not null)
                .itemIf("fetch", _fetch, _fetch is not null);
        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            implementor.Visit(getInput());

            if (implementor.Query.HasProjection)
                throw new CosmosTranslationException("A projection has already been applied.");

            // One ORDER BY per statement, and Cosmos rejects GROUP BY together with one. The builder
            // enforces both; refusing here keeps the failure at the node that caused it.
            if (implementor.Query.HasOrderBy || implementor.Query.HasOrderByRank)
                throw new CosmosTranslationException("A sort has already been applied.");

            if (implementor.Query.HasGroupBy)
                throw new CosmosTranslationException("Cosmos SQL does not support ORDER BY together with GROUP BY.");

            var translator = implementor.CreateTranslator();
            var names = getRowType().getFieldNames();
            var paths = new CosmosPath?[_projects.size()];

            for (var i = 0; i < _projects.size(); i++)
            {
                var node = (RexNode)_projects.get(i);
                implementor.Query.SelectProperty((string)names.get(i), translator.Translate(node));
                paths[i] = translator.TryResolvePath(node, out var path) ? path : null;
            }

            // Rendered against the input's binding, not the projection's: the clause repeats the
            // expression over document paths, because Cosmos cannot order by a projection alias.
            implementor.Query.AddOrderBy(translator.Translate(_distance), _descending);

            if (_offset is not null)
                implementor.Query.Offset = RexLiteral.intValue(_offset);
            if (_fetch is not null)
                implementor.Query.Fetch = RexLiteral.intValue(_fetch);

            implementor.Fields = paths;
        }

    }

}
