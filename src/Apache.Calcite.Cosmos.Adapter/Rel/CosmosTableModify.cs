using System;
using System.Linq.Expressions;

using Apache.Calcite.Cosmos.Adapter.Client;

using Apache.Calcite.Extensions.Adapter.Cursor;
using Apache.Calcite.Extensions.Adapter.Enumerable;
using Apache.Calcite.Extensions.Runtime;

using org.apache.calcite.plan;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Writes the rows of its input to a container, and reports how many it affected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This node is not in <see cref="CosmosConvention"/>, and that is the design rather than an
    /// oversight.</b> A subtree in that convention is a Cosmos SQL statement, and a write is not one:
    /// the query language has no DML, so the write goes through the SDK's item CRUD. Nothing below here
    /// renders text, and no implementor runs. What the node needs is rows, which is what
    /// <c>ClrCursorConvention</c> supplies — the same position <see cref="CosmosLookupJoin"/>
    /// occupies for the same reason.
    /// </para>
    /// <para>
    /// <b>Calcite's own <c>EnumerableTableModify</c> is not the model.</b> It writes through
    /// <c>ModifiableTable.getModifiableCollection()</c>, calling <c>add</c> and <c>remove</c> on a
    /// collection the table hands back. For Cosmos that collection would have to block on
    /// <c>CreateItemAsync</c> per element, and it would do so wherever the plan put it, with nothing
    /// in the signature to say a write was waiting on a round trip. Writing the awaiting open here
    /// instead keeps the blocking in one place, in <see cref="CosmosCursors.Write{TRow, TResult}"/>,
    /// where a caller that opened the plan synchronously asked for it. The cursor convention has no
    /// modify node of its own at all, so this is the first, and it is free to be shaped by what the
    /// service actually offers.
    /// </para>
    /// <para>
    /// The row type is Calcite's DML row type — one <c>BIGINT</c> count — so the node yields exactly one
    /// row. The writes are made at the open, as a sort's drain is, so by the time there is a cursor to
    /// read that row from every write has completed.
    /// </para>
    /// </remarks>
    public class CosmosTableModify : TableModify, ClrCursorRel
    {

        static readonly System.Reflection.MethodInfo WriteMethod = typeof(CosmosCursors).GetMethod(nameof(CosmosCursors.Write))
            ?? throw new InvalidOperationException($"'{nameof(CosmosCursors.Write)}' is missing from {nameof(CosmosCursors)}.");

        static readonly System.Reflection.MethodInfo WriteAsyncMethod = typeof(CosmosCursors).GetMethod(nameof(CosmosCursors.WriteAsync))
            ?? throw new InvalidOperationException($"'{nameof(CosmosCursors.WriteAsync)}' is missing from {nameof(CosmosCursors)}.");

        static readonly System.Reflection.MethodInfo NullableMethod = typeof(CosmosTableModify).GetMethod(nameof(Nullable))
            ?? throw new InvalidOperationException($"'{nameof(Nullable)}' is missing from {nameof(CosmosTableModify)}.");

        readonly CosmosTable _table;
        readonly CosmosWriteOperation _write;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the cursor convention.</param>
        /// <param name="table">The container being written to.</param>
        /// <param name="catalogReader">The catalog the target was resolved through.</param>
        /// <param name="input">The rows to write, in <see cref="ClrCursorConvention"/>.</param>
        /// <param name="operation">Which statement this stands for.</param>
        /// <param name="updateColumnList">The columns an <c>UPDATE</c> sets, or <c>null</c>.</param>
        /// <param name="sourceExpressionList">The values an <c>UPDATE</c> sets them to, or <c>null</c>.</param>
        /// <param name="flattened">Whether the input's row type has been flattened.</param>
        /// <param name="cosmos">The container's metadata-bearing table.</param>
        /// <param name="write">What the write does to each document.</param>
        /// <param name="partitionKey">The partition a whole-partition delete empties, or <c>null</c> for every other operation.</param>
        public CosmosTableModify(
            RelOptCluster cluster,
            RelTraitSet traitSet,
            RelOptTable table,
            Prepare.CatalogReader catalogReader,
            RelNode input,
            Operation operation,
            java.util.List? updateColumnList,
            java.util.List? sourceExpressionList,
            bool flattened,
            CosmosTable cosmos,
            CosmosWriteOperation write,
            object?[]? partitionKey = null) :
            base(cluster, traitSet, table, catalogReader, input, operation, updateColumnList, sourceExpressionList, flattened)
        {
            _table = cosmos ?? throw new ArgumentNullException(nameof(cosmos));
            _write = write;
            _partitionKey = partitionKey;
        }

        readonly object?[]? _partitionKey;

        /// <summary>
        /// Gets what the write does to each document.
        /// </summary>
        public CosmosWriteOperation Write => _write;

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosTableModify(
                getCluster(),
                traitSet,
                getTable(),
                getCatalogReader(),
                (RelNode)sole(inputs),
                getOperation(),
                getUpdateColumnList(),
                getSourceExpressionList(),
                isFlattened(),
                _table,
                _write,
                _partitionKey);
        }

        /// <inheritdoc />
        /// <remarks>
        /// One request per input row, charged as I/O. There is only ever one plan for a write — nothing
        /// competes with this — so the cost exists to be composed with the input's rather than to decide
        /// anything, and it says the true thing about the input: every row of it becomes a round trip.
        /// </remarks>
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var rows = mq.getRowCount(getInput()).doubleValue();

            return planner.getCostFactory()
                .makeCost(rows, rows, rows)
                .multiplyBy(ClrCursorConvention.CostMultiplier);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The input is visited synchronously, and the writes are waited for at the open: every write is
        /// a request the SDK only awaits, so there is no synchronous write to call. A whole-partition
        /// delete visits nothing, because it reads no rows — see <see cref="Source"/>.
        /// </remarks>
        public ClrCursorResult Implement(ClrCursorRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());
            var inputPhysType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getInput().getRowType(), pref.PreferArray());

            Expression source;
            if (Source)
            {
                var inputResult = implementor.VisitChild(this, 0, (ClrCursorRel)getInput(), pref);
                inputPhysType = inputResult.PhysType;
                source = inputResult.Expression;
            }
            else
            {
                source = Expression.Constant(null, typeof(IClrCursor<>).MakeGenericType(inputPhysType.RowType));
            }

            return implementor.Result(physType,
                Expression.Call(null,
                    WriteMethod.MakeGenericMethod(inputPhysType.RowType, physType.RowType),
                    [source, .. Arguments(implementor, physType, inputPhysType)]));
        }

        /// <inheritdoc />
        /// <remarks>
        /// The writes are the acquisition, under the open's token, which is why this appends it: the
        /// cursor handed back holds only the count.
        /// </remarks>
        public ClrCursorAsyncResult ImplementAsync(ClrCursorRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());
            var inputPhysType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getInput().getRowType(), pref.PreferArray());

            // The open of the input, as a ValueTask of a nullable cursor, which is what WriteAsync takes
            // so that the one write that reads no rows can pass a completed null.
            Expression source;
            if (Source)
            {
                var inputResult = implementor.VisitChildAsync(this, 0, (ClrCursorRel)getInput(), pref);
                inputPhysType = inputResult.PhysType;
                source = Expression.Call(null, NullableMethod.MakeGenericMethod(inputPhysType.RowType), inputResult.Expression);
            }
            else
            {
                var cursorType = typeof(IClrCursor<>).MakeGenericType(inputPhysType.RowType);
                source = Expression.Default(typeof(System.Threading.Tasks.ValueTask<>).MakeGenericType(cursorType));
            }

            return implementor.ResultAsync(physType,
                Expression.Call(null,
                    WriteAsyncMethod.MakeGenericMethod(inputPhysType.RowType, physType.RowType),
                    [source, .. Arguments(implementor, physType, inputPhysType), implementor.CancellationToken]));
        }

        /// <summary>
        /// Whether the write reads its input's rows at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A whole-partition delete does not: the predicate named the partition, so the rows the input
        /// would scan are precisely the rows the service will remove, and it counts them with a
        /// <c>COUNT(*)</c> instead.
        /// </para>
        /// <para>
        /// <b>Left unvisited rather than opened and ignored.</b> In the cursor convention opening is
        /// acquisition — the input's leaf sends its statement and awaits its first page at the open — so an
        /// input that was opened and never read would still have scanned a page of the partition being
        /// deleted. Not visiting it is what keeps the one request the fast path exists to avoid from being
        /// made.
        /// </para>
        /// </remarks>
        bool Source => _write != CosmosWriteOperation.DeletePartition;

        /// <summary>
        /// Builds everything the write is called with after its input, which both bodies do identically.
        /// </summary>
        Expression[] Arguments(ClrCursorRelImplementor implementor, ClrPhysType physType, ClrPhysType inputPhysType)
        {
            var inputRowType = getInput().getRowType();
            var fields = inputRowType.getFieldList();

            // An update's input is the table's row plus one trailing value per SET column; the
            // trailing fields have generated names and are not part of the document, so the write
            // is told the table's columns and the SET names separately.
            string[]? updates = null;
            var documentColumns = fields.size();
            if (getUpdateColumnList() is java.util.List updateColumns && updateColumns.size() > 0)
            {
                updates = new string[updateColumns.size()];
                for (var i = 0; i < updates.Length; i++)
                    updates[i] = (string)updateColumns.get(i);

                documentColumns -= updates.Length;
            }

            var names = new string[documentColumns];
            for (var i = 0; i < names.Length; i++)
                names[i] = ((org.apache.calcite.rel.type.RelDataTypeField)fields.get(i)).getName();

            var paths = new string[_table.Container.PartitionKeyPaths.Count];
            for (var i = 0; i < paths.Length; i++)
                paths[i] = _table.Container.PartitionKeyPaths[i];

            var write = new CosmosWrite(_write, names, paths, updates, _partitionKey);

            return
            [
                Rel.Convert.CosmosConverters.WriterExpression(getTable(), implementor.Root),
                Expression.Constant(write),
                FieldReader(inputPhysType, inputRowType),
                CountBuilder(physType),
                Rel.Convert.CosmosConverters.LookupCacheExpression(getTable(), implementor.Root),
                // Counts the partition a whole-partition delete is about to empty; unused by every
                // other operation, whose count is the rows it wrote.
                Rel.Convert.CosmosConverters.PartitionCounterExpression(getTable(), implementor.Root),
            ];
        }

        /// <summary>
        /// Reads an open of a cursor as an open of a cursor that may be absent.
        /// </summary>
        /// <remarks>
        /// A <see cref="System.Threading.Tasks.ValueTask{TResult}"/> is invariant, so the input's open
        /// cannot be handed to <see cref="CosmosCursors.WriteAsync{TRow, TResult}"/> as it stands. Costs
        /// a state machine only when the open actually suspends.
        /// </remarks>
        public static System.Threading.Tasks.ValueTask<IClrCursor<T>?> Nullable<T>(System.Threading.Tasks.ValueTask<IClrCursor<T>> open)
        {
            if (open.IsCompletedSuccessfully)
                return new System.Threading.Tasks.ValueTask<IClrCursor<T>?>(open.Result);

            return Awaited(open);

            static async System.Threading.Tasks.ValueTask<IClrCursor<T>?> Awaited(System.Threading.Tasks.ValueTask<IClrCursor<T>> open)
            {
                return await open.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Builds the lambda reading a row's values out, boxed and in field order.
        /// </summary>
        /// <remarks>
        /// Boxed because the document builder dispatches on what a value <em>is</em>. It has to: a row
        /// reaching here may hold Java boxes, having been read from a container, or CLR primitives,
        /// having been compiled from a literal.
        /// </remarks>
        static LambdaExpression FieldReader(ClrPhysType physType, org.apache.calcite.rel.type.RelDataType rowType)
        {
            var row = Expression.Parameter(physType.RowType, "row");
            var count = rowType.getFieldCount();

            var values = new Expression[count];
            for (var i = 0; i < count; i++)
            {
                var field = physType.FieldReference(row, i);
                values[i] = field.Type == typeof(object) ? field : Expression.Convert(field, typeof(object));
            }

            return Expression.Lambda(
                typeof(Func<,>).MakeGenericType(physType.RowType, typeof(object[])),
                Expression.NewArrayInit(typeof(object), values),
                row);
        }

        /// <summary>
        /// Builds the lambda wrapping the affected count as this node's own row.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The row type is one <c>BIGINT</c>, so by the same arity rule the rest of the adapter follows,
        /// the row is that value rather than an array holding it.
        /// </para>
        /// <para>
        /// <b>That value may be Java's box rather than the CLR's.</b> Where the physical type answers
        /// <c>java.lang.Long</c> there is no conversion operator from <see cref="long"/> for
        /// <see cref="Expression.Convert(Expression, Type)"/> to find, and the node fails to implement at all;
        /// the box is made by <c>Long.valueOf</c>, as javac makes it.
        /// </para>
        /// </remarks>
        static LambdaExpression CountBuilder(ClrPhysType physType)
        {
            var count = Expression.Parameter(typeof(long), "count");

            Expression body = count;
            if (physType.RowType == typeof(java.lang.Long))
                body = Expression.Call(typeof(java.lang.Long).GetMethod(nameof(java.lang.Long.valueOf), [typeof(long)])!, body);
            else if (body.Type != physType.RowType)
                body = Expression.Convert(body, physType.RowType);

            return Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(long), physType.RowType), body, count);
        }

    }

}
