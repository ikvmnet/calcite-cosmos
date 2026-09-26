using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Extensions.Runtime;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// The cursors a compiled plan reads its rows from and writes its rows through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the converter and the table modify put on the per-row path, and the only part of the
    /// adapter that runs once per row. Everything else — rendering the statement, choosing the partition
    /// key, building the row builder — happens once, while the statement is being prepared.
    /// </para>
    /// <para>
    /// <b>Each has two opens, and one of them blocks.</b> <c>ClrCursorConvention</c> builds a plan both
    /// ways and lets whoever opens it choose, so every open here comes in a pair: the <c>Async</c> one the
    /// awaiting body calls, and the unsuffixed one the synchronous body calls. The v3 SDK has no
    /// synchronous data-plane API — a page arrives only through <c>FeedIterator.ReadNextAsync</c> — so
    /// the unsuffixed one waits for what the other awaits, through <see cref="Wait{T}"/>, where it can be
    /// read. What is opened is the same cursor either way, with <c>Read</c> and <c>ReadAsync</c> both.
    /// </para>
    /// </remarks>
    public static class CosmosCursors
    {

        /// <summary>
        /// Executes a statement and opens its results as rows, blocking for the first page.
        /// </summary>
        /// <typeparam name="TRow">The plan's row type.</typeparam>
        /// <param name="executor">Executes the statement.</param>
        /// <param name="root">The context the statement is executing against.</param>
        /// <param name="query">The statement and its bound parameters.</param>
        /// <param name="rowBuilder">Builds one row from the JSON value Cosmos returned for it.</param>
        /// <returns>The rows.</returns>
        /// <remarks>
        /// The synchronous body's open, and a wait on <see cref="OpenAsync{TRow}"/> rather than a read of
        /// its own, because there is no synchronous read to write. It blocks a thread for the first page,
        /// and the cursor it returns blocks again only where a later page has to be fetched.
        /// </remarks>
        public static IClrCursor<TRow> Open<TRow>(ICosmosQueryExecutor executor, org.apache.calcite.DataContext root, CosmosQuery query, Func<JsonElement, TRow> rowBuilder)
        {
            return Wait(cancellationToken => OpenAsync(executor, root, query, rowBuilder, cancellationToken));
        }

        /// <summary>
        /// Executes a statement and opens its results as rows, without blocking.
        /// </summary>
        /// <typeparam name="TRow">The plan's row type.</typeparam>
        /// <param name="executor">Executes the statement.</param>
        /// <param name="root">The context the statement is executing against.</param>
        /// <param name="query">The statement and its bound parameters.</param>
        /// <param name="rowBuilder">Builds one row from the JSON value Cosmos returned for it.</param>
        /// <param name="cancellationToken">The token the open runs under; each advance brings its own.</param>
        /// <returns>The rows.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="executor"/> or <paramref name="rowBuilder"/> is <c>null</c>.</exception>
        public static async ValueTask<IClrCursor<TRow>> OpenAsync<TRow>(ICosmosQueryExecutor executor, org.apache.calcite.DataContext root, CosmosQuery query, Func<JsonElement, TRow> rowBuilder, CancellationToken cancellationToken)
        {
            if (executor is null)
                throw new ArgumentNullException(nameof(executor));
            if (rowBuilder is null)
                throw new ArgumentNullException(nameof(rowBuilder));

            // The statement is the same object every execution; what differs is the values a prepared
            // one left open, which is the only thing read off the context here.
            query = CosmosQueries.Bind(query, root);

            return new SelectCursor<TRow>(await executor.OpenAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false), rowBuilder);
        }

        /// <summary>
        /// Applies a write to every row of an opened input, blocking for it, and opens the count.
        /// </summary>
        /// <remarks>
        /// The synchronous body's open, and a wait on the awaiting one for the reason
        /// <see cref="Open{TRow}"/> gives: every write is a request the SDK only awaits.
        /// </remarks>
        public static IClrCursor<TResult> Write<TRow, TResult>(
            IClrCursor<TRow>? source,
            ICosmosItemWriter writer,
            CosmosWrite write,
            Func<TRow, object?[]> fields,
            Func<long, TResult> result,
            CosmosLookupCache? invalidate = null,
            Func<Microsoft.Azure.Cosmos.PartitionKey, CancellationToken, Task<long>>? counter = null)
        {
            return Wait(cancellationToken => WriteAsync(new ValueTask<IClrCursor<TRow>?>(source), writer, write, fields, result, invalidate, counter, cancellationToken));
        }

        /// <summary>
        /// Applies a write to every row of an input, and opens how many rows it affected.
        /// </summary>
        /// <typeparam name="TRow">The input plan's row type.</typeparam>
        /// <typeparam name="TResult">The modify's own row type, which is one row count.</typeparam>
        /// <param name="source">The open of the rows describing what to write, or a completed <c>null</c>
        /// where the write reads none.</param>
        /// <param name="writer">Writes documents to the container.</param>
        /// <param name="write">What to do with each row, and how to read one.</param>
        /// <param name="fields">Reads a row's values out, boxed and in field order.</param>
        /// <param name="result">Wraps the count as the modify's own row.</param>
        /// <param name="invalidate">The container's lookup cache, cleared once the writes are done.</param>
        /// <param name="counter">Counts a partition; what a whole-partition delete answers with.</param>
        /// <param name="cancellationToken">The token the writes run under.</param>
        /// <returns>A cursor over exactly one row, carrying the number of documents affected.</returns>
        /// <remarks>
        /// <para>
        /// <b>The writes are the acquisition</b>, as a sort's drain is: every row is written here, under
        /// the open's token, and the cursor handed back holds only the count. That is when
        /// <c>ExecuteNonQuery</c> means a statement to have happened, and it leaves nothing for an
        /// advance to cancel half way — though a write already sent is not undone by cancelling the
        /// open that sent it.
        /// </para>
        /// <para>
        /// <b>Both operations build the document the row describes, and that is not a detail.</b> A
        /// delete needs an <c>id</c> and a partition key, and the partition key may be at a nested path
        /// that is not promoted to a column — so reading it out of the assembled document is the one
        /// route that works for every container, rather than one that works until a container declares
        /// <c>/inventory/sku</c> as its key.
        /// </para>
        /// <para>
        /// One request per row. The input is disposed once it has been drained, since this cursor is
        /// what owns it.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Any required argument is <c>null</c>.</exception>
        /// <exception cref="CosmosExecutionException">A row does not describe a document the operation can be applied to.</exception>
        public static async ValueTask<IClrCursor<TResult>> WriteAsync<TRow, TResult>(
            ValueTask<IClrCursor<TRow>?> source,
            ICosmosItemWriter writer,
            CosmosWrite write,
            Func<TRow, object?[]> fields,
            Func<long, TResult> result,
            CosmosLookupCache? invalidate,
            Func<Microsoft.Azure.Cosmos.PartitionKey, CancellationToken, Task<long>>? counter,
            CancellationToken cancellationToken)
        {
            if (writer is null)
                throw new ArgumentNullException(nameof(writer));
            if (write is null)
                throw new ArgumentNullException(nameof(write));
            if (fields is null)
                throw new ArgumentNullException(nameof(fields));
            if (result is null)
                throw new ArgumentNullException(nameof(result));

            var input = await source.ConfigureAwait(false);

            try
            {
                return new SingleCursor<TResult>(result(await ApplyAsync(input, writer, write, fields, counter, cancellationToken).ConfigureAwait(false)));
            }
            finally
            {
                if (input is not null)
                    await input.DisposeAsync().ConfigureAwait(false);

                // The container changed, so what the lookup cache remembers about it may be wrong.
                // Cleared whatever was written, and however far the writes got: goodwill is cheap
                // here, and a write from outside the process is the TTL's problem rather than this
                // line's.
                invalidate?.Clear();
            }
        }

        /// <summary>
        /// The writes themselves, answering how many documents they affected.
        /// </summary>
        static async ValueTask<long> ApplyAsync<TRow>(
            IClrCursor<TRow>? source,
            ICosmosItemWriter writer,
            CosmosWrite write,
            Func<TRow, object?[]> fields,
            Func<Microsoft.Azure.Cosmos.PartitionKey, CancellationToken, Task<long>>? counter,
            CancellationToken cancellationToken)
        {
            // The one write that does not read its rows, and whose input is never opened: the
            // predicate named the partition, so the rows the plan would have scanned are precisely the
            // rows the service will remove. The count comes from a COUNT(*) beforehand, which is as
            // racy against a concurrent writer as the scan it replaces — and is the only count
            // available, the operation reporting none.
            if (write.Operation == CosmosWriteOperation.DeletePartition)
            {
                if (write.PartitionKeyValues is not object?[] values)
                    throw new CosmosExecutionException("A whole-partition delete carries no partition key.");

                if (counter is null)
                    throw new CosmosExecutionException("A whole-partition delete needs something to count the partition with.");

                var partition = PartitionKeyOf(values);

                var count = await counter(partition, cancellationToken).ConfigureAwait(false);
                await writer.DeletePartitionAsync(partition, cancellationToken).ConfigureAwait(false);

                return count;
            }

            if (source is null)
                throw new CosmosExecutionException($"A '{write.Operation}' write has no rows to read.");

            var affected = 0L;

            while (await source.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = source.Current;
                var bytes = CosmosDocument.Build(write.ColumnNames, fields(row));

                using var document = JsonDocument.Parse(bytes);

                var partitionKey = PartitionKeyOf(document.RootElement, write);

                switch (write.Operation)
                {
                    case CosmosWriteOperation.Insert:
                        await writer.CreateItemAsync(bytes, partitionKey, cancellationToken).ConfigureAwait(false);
                        affected++;
                        break;

                    case CosmosWriteOperation.Delete:
                        {
                            if (CosmosDocument.Read(document.RootElement, "/" + Metadata.CosmosContainerMetadata.IdPropertyName) is not string id)
                                throw new CosmosExecutionException("A row being deleted carries no 'id', so the document it names cannot be identified.");

                            if (await writer.DeleteItemAsync(id, partitionKey, cancellationToken).ConfigureAwait(false))
                                affected++;

                            break;
                        }

                    case CosmosWriteOperation.Update:
                        {
                            // The document just built is the OLD one — the row's table columns hold
                            // what the scan read — and it is what identifies the target: the id, and
                            // the partition key, which a replace cannot change.
                            if (CosmosDocument.Read(document.RootElement, "/" + Metadata.CosmosContainerMetadata.IdPropertyName) is not string id)
                                throw new CosmosExecutionException("A row being updated carries no 'id', so the document it names cannot be identified.");

                            var replacement = CosmosDocument.Build(write.ColumnNames, ApplySets(write, fields(row)));

                            if (await writer.ReplaceItemAsync(replacement, id, partitionKey, cancellationToken).ConfigureAwait(false))
                                affected++;

                            break;
                        }

                    default:
                        throw new CosmosExecutionException($"No write is defined for operation '{write.Operation}'.");
                }
            }

            return affected;
        }

        /// <summary>
        /// Runs something that awaits and blocks for its result, with the synchronization context
        /// suppressed before it starts.
        /// </summary>
        /// <typeparam name="T">The result.</typeparam>
        /// <param name="start">What to run, called here so that the context is suppressed before it starts.</param>
        /// <returns>The result.</returns>
        /// <remarks>
        /// <para>
        /// <b>This blocks a thread</b>, and it is the one place in the adapter that does: every synchronous
        /// open, and every synchronous advance that has to fetch a page, comes through here. The token is
        /// <see cref="CancellationToken.None"/>, because a synchronous caller has none to give.
        /// </para>
        /// <para>
        /// The context is suppressed <em>before</em> the call and not merely around the wait, because a
        /// continuation is captured at the moment of suspension, which is inside the call's synchronous
        /// phase — the reason <c>ClrCursors.Block</c> gives in <c>Apache.Calcite.Extensions</c>, which is
        /// internal there. A result that completed synchronously is read without a wait at all, which is
        /// what keeps a value already in a page from costing anything.
        /// </para>
        /// </remarks>
        internal static T Wait<T>(Func<CancellationToken, ValueTask<T>> start)
        {
            var context = SynchronizationContext.Current;
            if (context is null)
                return Wait(start(CancellationToken.None));

            SynchronizationContext.SetSynchronizationContext(null);

            try
            {
                return Wait(start(CancellationToken.None));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(context);
            }

            static T Wait(ValueTask<T> task)
            {
                // One that has not completed cannot be blocked on directly — its awaiter may be backed by
                // a recyclable source — so it goes through AsTask, which allocates.
                return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// The executor's values, each built into the plan's row as it is reached.
        /// </summary>
        sealed class SelectCursor<TRow>(IClrCursor<JsonElement> source, Func<JsonElement, TRow> rowBuilder) : ClrCursor<TRow>
        {

            TRow _current = default!;

            /// <inheritdoc />
            public override TRow Current => _current;

            /// <inheritdoc />
            public override bool Read()
            {
                if (source.Read() == false)
                    return false;

                _current = rowBuilder(source.Current);
                return true;
            }

            /// <inheritdoc />
            public override async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
            {
                if (await source.ReadAsync(cancellationToken).ConfigureAwait(false) == false)
                    return false;

                _current = rowBuilder(source.Current);
                return true;
            }

            /// <inheritdoc />
            public override void Dispose()
            {
                source.Dispose();
            }

            /// <inheritdoc />
            public override ValueTask DisposeAsync()
            {
                return source.DisposeAsync();
            }

        }

        /// <summary>
        /// One row, which is what a write answers with.
        /// </summary>
        sealed class SingleCursor<T>(T value) : ClrCursor<T>
        {

            int _state;

            /// <inheritdoc />
            public override T Current => value;

            /// <inheritdoc />
            public override bool Read()
            {
                if (_state > 0)
                {
                    _state = 2;
                    return false;
                }

                _state = 1;
                return true;
            }

            /// <inheritdoc />
            public override ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return new ValueTask<bool>(Read());
            }

            /// <inheritdoc />
            public override void Dispose()
            {

            }

        }

        /// <summary>
        /// Produces the values describing the replacement document: the old row with each
        /// <c>SET</c> column's value substituted from the trailing positions the planner appends.
        /// </summary>
        /// <remarks>
        /// The document column is the only column a <c>SET</c> can name, every other being
        /// <c>STORED</c>, so the other columns' old values are withheld rather than substituted: they
        /// are projections of a document the statement is replacing outright, and carrying them
        /// forward would describe a document the <c>SET</c> did not write. A new document that
        /// contradicts the old identity or placement is rejected loudly by the service, which is the
        /// correct fate for an update trying to rename or move a document.
        /// </remarks>
        static object?[] ApplySets(CosmosWrite write, object?[] values)
        {
            var updates = write.UpdateColumnNames
                ?? throw new CosmosExecutionException("An update carries no SET columns.");

            var count = write.ColumnNames.Length;
            var updated = new object?[count];
            Array.Copy(values, updated, Math.Min(count, values.Length));

            // Every other column is STORED, so the document column is the only thing a SET can name
            // and the old row's other values are never substituted into the replacement: they are
            // projections of a document the statement is replacing outright.
            for (var i = 0; i < count; i++)
                if (i != CosmosImplementor.DocumentColumnOrdinal)
                    updated[i] = null;

            for (var j = 0; j < updates.Length; j++)
            {
                var ordinal = Array.IndexOf(write.ColumnNames, updates[j]);
                if (ordinal < 0)
                    throw new CosmosExecutionException($"The SET column '{updates[j]}' is not a column of the table.");

                if (count + j >= values.Length)
                    throw new CosmosExecutionException($"The row carries no value for the SET column '{updates[j]}'.");

                updated[ordinal] = values[count + j];
            }

            return updated;
        }

        /// <summary>
        /// Builds the partition key a document belongs to.
        /// </summary>
        /// <remarks>
        /// A declared path the document does not carry is <em>absent</em> rather than null, and Cosmos
        /// distinguishes the two: an absent key is the "none" logical partition, which is a real place
        /// documents live and not an error. <see cref="PartitionKeyBuilder.AddNoneType"/> is how the SDK
        /// names it, and passing null instead would route to a different partition.
        /// </remarks>
        /// <param name="document">The document.</param>
        /// <param name="write">The write, which carries the declared paths.</param>
        /// <returns>The partition key.</returns>
        /// <summary>
        /// Builds a partition key from the values a predicate pinned.
        /// </summary>
        /// <remarks>
        /// The counterpart of the overload below, which reads them out of a document: here the
        /// predicate named them at planning time and no document was ever read.
        /// </remarks>
        static Microsoft.Azure.Cosmos.PartitionKey PartitionKeyOf(object?[] values)
        {
            var builder = new Microsoft.Azure.Cosmos.PartitionKeyBuilder();

            foreach (var value in values)
            {
                switch (value)
                {
                    case null:
                        builder.AddNullValue();
                        break;
                    case string s:
                        builder.Add(s);
                        break;
                    case bool b:
                        builder.Add(b);
                        break;
                    case double d:
                        builder.Add(d);
                        break;
                    case long l:
                        builder.Add(l);
                        break;
                    default:
                        throw new CosmosExecutionException($"A partition key value of type '{value.GetType().Name}' cannot be a partition key.");
                }
            }

            return builder.Build();
        }

        static Microsoft.Azure.Cosmos.PartitionKey PartitionKeyOf(JsonElement document, CosmosWrite write)
        {
            var builder = new Microsoft.Azure.Cosmos.PartitionKeyBuilder();

            foreach (var path in write.PartitionKeyPaths)
            {
                if (CosmosDocument.Contains(document, path) == false)
                {
                    builder.AddNoneType();
                    continue;
                }

                switch (CosmosDocument.Read(document, path))
                {
                    case null:
                        builder.AddNullValue();
                        break;
                    case string s:
                        builder.Add(s);
                        break;
                    case bool b:
                        builder.Add(b);
                        break;
                    case double d:
                        builder.Add(d);
                        break;
                    default:
                        throw new CosmosExecutionException($"The partition key path '{path}' holds a value that cannot be a partition key.");
                }
            }

            return builder.Build();
        }

    }

}
