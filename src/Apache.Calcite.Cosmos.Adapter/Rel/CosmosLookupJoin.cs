using System;
using System.Collections.Generic;
using System.Linq.Expressions;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Facts;

using Apache.Calcite.Extensions.Adapter.Cursor;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Joins a sequence to a container by fetching only the documents its keys could match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relational join is not expressible in Cosmos — its <c>JOIN</c> has no predicate and traverses a
    /// document's own arrays — so without this both sides are read whole and joined in process. This
    /// makes one side pay for the other: the build side's keys reach the service, which returns the
    /// documents that could join rather than all of them.
    /// </para>
    /// <para>
    /// <b>This is Flink's lookup join, and the shape is worth stating because it is the part that is
    /// easy to get wrong.</b> Calcite expresses "a value that will exist later" as a correlation
    /// variable, and that is a planner-level device: the rule that builds this node consumes it, exactly
    /// as Flink's <c>LogicalCorrelateToJoinFromTemporalTableRule</c> does when its <c>decorrelate()</c>
    /// rewrites every reference as an ordinary field reference. Nothing correlated reaches this node, and
    /// nothing here reads generated code. What crosses into the runtime is key values, which is what
    /// <c>asyncLookup(RowData)</c> receives there.
    /// </para>
    /// <para>
    /// Its left input is in <see cref="ClrCursorConvention"/> and its right in
    /// <see cref="CosmosConvention"/>, which makes it a converter as much as a join — the same shape as
    /// <see cref="Convert.CosmosToClrCursorConverter"/>, and for the same reason: below it is a
    /// statement, above it are rows. Like that converter it leads into the cursor convention and no
    /// other.
    /// </para>
    /// </remarks>
    public class CosmosLookupJoin : Join, ClrCursorRel
    {

        static readonly System.Reflection.MethodInfo JoinMethod = typeof(CosmosLookup).GetMethod(nameof(CosmosLookup.Join))
            ?? throw new InvalidOperationException($"'{nameof(CosmosLookup.Join)}' is missing from {nameof(CosmosLookup)}.");

        static readonly System.Reflection.MethodInfo JoinAsyncMethod = typeof(CosmosLookup).GetMethod(nameof(CosmosLookup.JoinAsync))
            ?? throw new InvalidOperationException($"'{nameof(CosmosLookup.JoinAsync)}' is missing from {nameof(CosmosLookup)}.");

        /// <summary>
        /// The name prefix of the parameters carrying a batch's keys.
        /// </summary>
        /// <remarks>
        /// Distinct from the <c>@p</c> the statement's own literals use, so the two cannot collide
        /// however many of each a statement has.
        /// </remarks>
        public const string KeyPrefix = "@k";

        /// <summary>
        /// How many build rows one fetch serves, and so how many key parameters the statement carries.
        /// </summary>
        /// <remarks>
        /// Calcite's own batch join defaults to the same number. It is the cardinality bound the whole
        /// idea needs: ten keys in a statement is a large saving, and a million is a statement the
        /// service refuses on length, so the batch is what stops the second from being reachable.
        /// </remarks>
        public const int DefaultBatchSize = 100;

        /// <summary>
        /// How many keys one execution remembers.
        /// </summary>
        /// <remarks>
        /// Flink's lookup connectors put a cache in front of the same operation for the same reason:
        /// reference data is looked up repeatedly by definition, and a remembered key costs nothing.
        /// This one lives for a single execution, so it answers for no staleness the join did not
        /// already have — the container is read across many requests either way.
        /// <para>
        /// Bounded so that a large build side cannot be remembered whole. The bound is generous
        /// because an entry is a key and its matching rows, and the alternative to holding one is a
        /// request.
        /// </para>
        /// </remarks>
        public const int DefaultCacheSize = 4096;

        readonly int _buildKey;
        readonly int _probeKey;
        readonly int _batchSize;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the cursor convention.</param>
        /// <param name="build">The side whose keys are pushed down, in <see cref="ClrCursorConvention"/>.</param>
        /// <param name="probe">The container subtree being restricted, in <see cref="CosmosConvention"/>.</param>
        /// <param name="condition">The join condition, which is one equality on the keys below.</param>
        /// <param name="buildKey">The ordinal of the join key in <paramref name="build"/>'s row.</param>
        /// <param name="probeKey">The ordinal of the join key in <paramref name="probe"/>'s row.</param>
        /// <param name="batchSize">How many build rows one fetch serves.</param>
        /// <param name="joinType">
        /// <see cref="JoinRelType.INNER"/>, or <see cref="JoinRelType.LEFT"/> to keep a build row nothing
        /// matched with nulls for the container's columns.
        /// </param>
        /// <remarks>
        /// A <see cref="Join"/> rather than a plain two-input node, and not only for the row type.
        /// Calcite's metadata dispatches on a node's class, and the operators above this one ask it
        /// things — predicates, uniqueness, selectivity — while they are being implemented. A node with
        /// two inputs that is not a join has no handler for those, and the failure is not a wrong answer
        /// but a plan that cannot be implemented at all.
        /// </remarks>
        public CosmosLookupJoin(RelOptCluster cluster, RelTraitSet traitSet, RelNode build, RelNode probe, RexNode condition, int buildKey, int probeKey, JoinRelType? joinType = null, int batchSize = DefaultBatchSize) :
            this(cluster, traitSet, build, probe, condition, buildKey, probeKey, joinType, batchSize, null)
        {

        }

        /// <summary>
        /// Initializes a new instance carrying what the probe side's rows are known to satisfy.
        /// </summary>
        /// <remarks>
        /// Decided where the rule decides, and asked again of the same facts when the join is implemented, for the
        /// reason <see cref="CosmosSort"/>'s are: whether the key can be restricted reads what the probe's filters
        /// establish (L1), and which filters a walk finds depends on the member of a set it reads. See
        /// <see cref="CosmosLookupJoinRule.TryProbeKey"/>.
        /// </remarks>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the cursor convention.</param>
        /// <param name="build">The side whose keys are pushed down, in <see cref="ClrCursorConvention"/>.</param>
        /// <param name="probe">The container subtree being restricted, in <see cref="CosmosConvention"/>.</param>
        /// <param name="condition">The join condition, which is one equality on the keys below.</param>
        /// <param name="buildKey">The ordinal of the join key in <paramref name="build"/>'s row.</param>
        /// <param name="probeKey">The ordinal of the join key in <paramref name="probe"/>'s row.</param>
        /// <param name="joinType"><see cref="JoinRelType.INNER"/>, or <see cref="JoinRelType.LEFT"/>.</param>
        /// <param name="batchSize">How many build rows one fetch serves.</param>
        /// <param name="facts">What every row of the probe side is known to satisfy, or <c>null</c> for the declaration.</param>
        internal CosmosLookupJoin(RelOptCluster cluster, RelTraitSet traitSet, RelNode build, RelNode probe, RexNode condition, int buildKey, int probeKey, JoinRelType? joinType, int batchSize, JsonFactSet? facts) :
            base(cluster, traitSet, com.google.common.collect.ImmutableList.of(), build, probe, condition, java.util.Collections.emptySet(), joinType ?? JoinRelType.INNER)
        {
            if (getJoinType() != JoinRelType.INNER && getJoinType() != JoinRelType.LEFT)
                throw new ArgumentException("A lookup join is an inner or a left join.", nameof(joinType));

            _buildKey = buildKey;
            _probeKey = probeKey;
            _batchSize = batchSize > 0 ? batchSize : throw new ArgumentOutOfRangeException(nameof(batchSize));
            Facts = facts;
        }

        /// <summary>
        /// Gets what every row of the probe side is known to satisfy, as the rule decided; <c>null</c> where it was built
        /// without, which reads what the container declares of every document.
        /// </summary>
        internal JsonFactSet? Facts { get; }

        /// <summary>Gets the ordinal of the join key in the build side's row.</summary>
        public int BuildKey => _buildKey;

        /// <summary>Gets the ordinal of the join key in the container subtree's row.</summary>
        public int ProbeKey => _probeKey;

        /// <summary>Gets how many build rows one fetch serves.</summary>
        public int BatchSize => _batchSize;

        /// <inheritdoc />
        public override Join copy(RelTraitSet traitSet, RexNode conditionExpr, RelNode left, RelNode right, JoinRelType joinType, bool semiJoinDone)
        {
            return new CosmosLookupJoin(getCluster(), traitSet, left, right, conditionExpr, _buildKey, _probeKey, joinType, _batchSize, Facts);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Charged for what it actually does: read the build side, and make one request per batch of it.
        /// The alternative the planner is comparing against reads the whole container once, so the
        /// comparison turns on the container being larger than the build side — which is the case this
        /// exists for, and the case where the row count read from the service earns its keep.
        /// </para>
        /// <para>
        /// The rows fetched are charged rather than the container's, since that is the point: only the
        /// documents a key could match are read.
        /// </para>
        /// </remarks>
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var buildRows = mq.getRowCount(getLeft()).doubleValue();
            var probeRows = mq.getRowCount(getRight()).doubleValue();
            var rows = mq.getRowCount(this).doubleValue();

            // One request per batch, and each is a round trip rather than a row read — which is why the
            // batch size divides here and appears nowhere else in the cost.
            var requests = System.Math.Ceiling(buildRows / _batchSize);

            // The documents fetched: those the keys name, and never more than the probe side holds,
            // since every batch reads only documents the statement would have selected. The join's
            // own estimate is not that — see BoundRowCount — and charged on it a list of fifty keys
            // cost more than reading the container, and lost to a scan that read every matching
            // document to keep fifty (#208). Each request is charged as a row as well, so a build side
            // far larger than the container still loses to reading it once.
            var fetched = System.Math.Min(rows, probeRows) + requests;

            return planner.getCostFactory()
                .makeCost(fetched, buildRows + requests, fetched)
                .multiplyBy(ClrCursorConvention.CostMultiplier);
        }

        /// <summary>
        /// Bounds Calcite's estimate of the rows this join returns by what its keys allow.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Calcite's estimate does not know a key when it sees one.</b> An inner join is estimated as
        /// the two sides' product scaled by a selectivity, so a list of a hundred distinct keys against
        /// 445 documents was estimated at 667 rows: more than the container could return to the keys.
        /// Every node above the join is priced on that, and the projection above a lookup cost it more
        /// than the in-process semi-join it replaced cost in total (#208).
        /// </para>
        /// <para>
        /// <b>Distinct build keys bound it by the documents they name</b>: each document matches at most
        /// one of them, so the join returns at most the documents the keys fetch — no more than the
        /// probe side, and no more than the keys times the documents each names. A unique probe key
        /// bounds it by the build side the same way. A left join returns every build row at least once, so its bound is never
        /// below the build side.
        /// </para>
        /// </remarks>
        /// <param name="mq">The metadata query.</param>
        /// <param name="estimate">Calcite's estimate.</param>
        /// <returns>The bounded estimate.</returns>
        internal double BoundRowCount(RelMetadataQuery mq, double estimate)
        {
            var buildRows = mq.getRowCount(getLeft()).doubleValue();
            var probeRows = mq.getRowCount(getRight()).doubleValue();

            var bound = double.PositiveInfinity;

            if (IsUnique(mq, getLeft(), _buildKey))
                bound = System.Math.Min(bound, System.Math.Min(probeRows, buildRows * DocumentsPerKey(mq)));

            if (IsUnique(mq, getRight(), _probeKey))
                bound = System.Math.Min(bound, buildRows);

            if (getJoinType() == JoinRelType.LEFT)
                bound = System.Math.Max(bound, buildRows);

            return System.Math.Min(estimate, bound);
        }

        /// <summary>
        /// Returns how many documents one key is taken to name.
        /// </summary>
        /// <remarks>
        /// The probe side's rows over its key's distinct values where the metadata knows them, and one
        /// where it does not — which is the ordinary case, a container reporting no distinct counts. One
        /// is the assumption the operator is for: a lookup joins on a key, and a key that names many
        /// documents per value is a join this was never going to win by much. It is never above the
        /// probe side's rows, which bound every use of it.
        /// </remarks>
        double DocumentsPerKey(RelMetadataQuery mq)
        {
            var probeRows = mq.getRowCount(getRight()).doubleValue();

            if (mq.getDistinctRowCount(getRight(), org.apache.calcite.util.ImmutableBitSet.of(_probeKey), null) is java.lang.Double distinct && distinct.doubleValue() >= 1d)
                return System.Math.Max(1d, probeRows / distinct.doubleValue());

            return 1d;
        }

        /// <summary>
        /// Determines whether the metadata knows a column to hold no value twice.
        /// </summary>
        static bool IsUnique(RelMetadataQuery mq, RelNode input, int ordinal) =>
            mq.areColumnsUnique(input, org.apache.calcite.util.ImmutableBitSet.of(ordinal)) is java.lang.Boolean unique && unique.booleanValue();

        /// <inheritdoc />
        /// <remarks>
        /// The build side is visited synchronously and handed to the join as it was opened, and the join
        /// acquires nothing more at its open. A batch is fetched by the advance that runs out of rows, and
        /// a synchronous advance blocks for that fetch — once per batch, not once per row.
        /// </remarks>
        public ClrCursorResult Implement(ClrCursorRelImplementor implementor, ClrCursorPrefer pref)
        {
            var buildResult = implementor.VisitChild(this, 0, (ClrCursorRel)getLeft(), pref);

            var (physType, method, arguments) = Prepare(implementor, pref, buildResult.PhysType);

            return implementor.Result(physType,
                Expression.Call(null, JoinMethod.MakeGenericMethod(method), [buildResult.Expression, .. arguments]));
        }

        /// <inheritdoc />
        public ClrCursorAsyncResult ImplementAsync(ClrCursorRelImplementor implementor, ClrCursorPrefer pref)
        {
            var buildResult = implementor.VisitChildAsync(this, 0, (ClrCursorRel)getLeft(), pref);

            var (physType, method, arguments) = Prepare(implementor, pref, buildResult.PhysType);

            // Ends in the open's token, as every awaiting open does. The join itself uses none of it:
            // each batch is fetched under the token of the advance that needs it.
            return implementor.ResultAsync(physType,
                Expression.Call(null, JoinAsyncMethod.MakeGenericMethod(method), [buildResult.Expression, .. arguments, implementor.CancellationToken]));
        }

        /// <summary>
        /// Renders the lookup statement and builds everything the join is called with after its build
        /// side, which both bodies do identically.
        /// </summary>
        (ClrPhysType PhysType, Type[] TypeArguments, Expression[] Arguments) Prepare(ClrCursorRelImplementor implementor, ClrCursorPrefer pref, ClrPhysType buildPhysType)
        {
            var probe = getRight();

            var probePhysType = ClrPhysTypeImpl.Of(implementor.TypeFactory, probe.getRowType(), pref.PreferArray());
            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());

            if (CosmosLookupJoinRule.TryProbeKey(probe, _probeKey, container => Facts ?? CosmosOracle.For(container.Facts).Declared, out var key) == false)
                throw new Sql.CosmosTranslationException("The lookup key can no longer be restricted.");

            var (query, _, readings) = CosmosConverters.GenerateLookupQuery(probe, implementor.RexBuilder, _probeKey, KeyPrefix, _batchSize, key.Path);

            Convert.CosmosConverters.AnnounceQueryPlan(implementor, this, query.Sql);

            var buildType = buildPhysType.RowType;
            var probeType = probePhysType.RowType;

            return (physType, [buildType, probeType, physType.RowType],
            [
                CosmosConverters.ExecutorExpression(probe, implementor.Root),
                implementor.Root,
                Expression.Constant(query),
                Expression.Constant(KeyPrefix),
                Expression.Constant(_batchSize),
                KeySelector(buildPhysType, buildType, _buildKey),
                CosmosConverters.RowBuilder(probePhysType, probe.getRowType(), readings),
                KeySelector(probePhysType, probeType, _probeKey),
                ResultSelector(physType, buildPhysType, buildType, probePhysType, probeType),
                Expression.Constant(DefaultCacheSize),
                CosmosConverters.LookupCacheExpression(probe, implementor.Root),
                getJoinType() == JoinRelType.LEFT
                    ? UnmatchedSelector(physType, buildPhysType, buildType)
                    : Expression.Constant(null, typeof(Func<,>).MakeGenericType(buildType, physType.RowType)),
                Expression.Constant(key.Uuid is { } uuid ? CosmosLookup.SpellUuid(uuid) : null, typeof(Func<object, object?>)),
            ]);
        }

        /// <summary>
        /// Builds the lambda reading the join key out of a row.
        /// </summary>
        /// <remarks>
        /// Boxed, because the two sides' keys are compared as objects — see
        /// <see cref="CosmosLookup.Normalize"/> for why they cannot simply be compared as they arrive.
        /// </remarks>
        static LambdaExpression KeySelector(ClrPhysType physType, Type rowType, int ordinal)
        {
            var row = Expression.Parameter(rowType, "row");

            return Expression.Lambda(Expression.Convert(physType.FieldReference(row, ordinal), typeof(object)), row);
        }

        /// <summary>
        /// Builds the lambda combining a matching pair into a joined row.
        /// </summary>
        /// <remarks>
        /// Written out rather than borrowed from <c>ClrEnumUtils.JoinSelector</c>, which takes the
        /// implementor by an interface internal to <c>Apache.Calcite.Extensions</c> and so cannot be
        /// called from here. What it does is this: the build's fields in order, then the probe's.
        /// </remarks>
        LambdaExpression ResultSelector(ClrPhysType physType, ClrPhysType buildPhysType, Type buildType, ClrPhysType probePhysType, Type probeType)
        {
            var b = Expression.Parameter(buildType, "b");
            var p = Expression.Parameter(probeType, "p");

            var fields = new List<Expression>();

            for (var i = 0; i < getLeft().getRowType().getFieldCount(); i++)
                fields.Add(buildPhysType.FieldReference(b, i));

            for (var i = 0; i < getRight().getRowType().getFieldCount(); i++)
                fields.Add(probePhysType.FieldReference(p, i));

            return Expression.Lambda(Expression.NewArrayInit(typeof(object), fields.ConvertAll(f => f.Type == typeof(object) ? f : Expression.Convert(f, typeof(object)))), b, p);
        }

        /// <summary>
        /// Builds the lambda giving a left join's row for a build row nothing matched: the build's fields,
        /// then a null for each of the container's.
        /// </summary>
        /// <remarks>
        /// A lambda of its own rather than the result selector over a null container row, because a
        /// container row of one column is the column's own type, which may be a value and has no null.
        /// </remarks>
        LambdaExpression UnmatchedSelector(ClrPhysType physType, ClrPhysType buildPhysType, Type buildType)
        {
            var b = Expression.Parameter(buildType, "b");

            var fields = new List<Expression>();

            for (var i = 0; i < getLeft().getRowType().getFieldCount(); i++)
            {
                var field = buildPhysType.FieldReference(b, i);
                fields.Add(field.Type == typeof(object) ? field : Expression.Convert(field, typeof(object)));
            }

            for (var i = 0; i < getRight().getRowType().getFieldCount(); i++)
                fields.Add(Expression.Constant(null, typeof(object)));

            return Expression.Lambda(Expression.NewArrayInit(typeof(object), fields), b);
        }

    }

}
