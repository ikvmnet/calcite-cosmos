using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Extensions.Runtime;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// An <see cref="ICosmosQueryExecutor"/> bound to a single container.
    /// </summary>
    /// <remarks>
    /// Also an <see cref="ICosmosItemWriter"/>. The two are separate interfaces because they are
    /// separate capabilities, and one class because they are the same container.
    /// </remarks>
    public sealed class CosmosQueryExecutor : ICosmosQueryExecutor, ICosmosItemWriter
    {

        readonly Container _container;
        readonly bool _indexMetrics;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container to execute against.</param>
        /// <param name="indexMetrics">
        /// Whether to ask the service which indexes each statement used. Off by default: the service
        /// computes it per query and it is a diagnostic rather than something the adapter acts on.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public CosmosQueryExecutor(Container container, bool indexMetrics = false)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _indexMetrics = indexMetrics;
        }

        /// <summary>
        /// Records what a response cost.
        /// </summary>
        /// <remarks>
        /// Per response rather than per execution: a query spanning continuations is charged per page.
        /// </remarks>
        void Report(double charge, string kind)
        {
            var container = new KeyValuePair<string, object?>("cosmos.container", _container.Id);
            var which = new KeyValuePair<string, object?>("cosmos.request_kind", kind);

            CosmosInstrumentation.RequestCharge.Record(charge, container, which);
            CosmosInstrumentation.Responses.Add(1, container, which);
        }

        /// <summary>
        /// Reads one document directly, opening it if it exists and an empty cursor if it does not.
        /// </summary>
        /// <remarks>
        /// A missing document is an empty result rather than an error: the query this stands in for
        /// would have returned no rows, and a read that answers "no such document" is that answer. The
        /// whole response is the acquisition, so it is read here, under the open's token, and the
        /// cursor has nothing left to fetch.
        /// </remarks>
        async ValueTask<IClrCursor<JsonElement>> ReadItemAsync(string id, PartitionKey partitionKey, CancellationToken cancellationToken)
        {
            using var response = await _container.ReadItemStreamAsync(id, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.PointRead);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new BufferedCursor(Array.Empty<JsonElement>());

            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Cloned for the same reason the query path clones: the element belongs to the document,
            // which is disposed here and returns its buffer to the pool.
            return new BufferedCursor(new[] { document.RootElement.Clone() });
        }

        /// <summary>
        /// Reads a set of documents directly, opening those that exist.
        /// </summary>
        /// <remarks>
        /// A missing id is simply absent from the result, which is the answer the query this stands
        /// in for would have given — the same stance the single read takes on a 404. One response,
        /// read whole at the open, as the single read is.
        /// </remarks>
        async ValueTask<IClrCursor<JsonElement>> ReadManyAsync(IReadOnlyList<string> ids, PartitionKey partitionKey, CancellationToken cancellationToken)
        {
            var items = new List<(string, PartitionKey)>(ids.Count);
            foreach (var id in ids)
                items.Add((id, partitionKey));

            using var response = await _container.ReadManyItemsStreamAsync(items, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.PointRead);

            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);

            // The same envelope a query page carries, and cloned for the same reason: the elements
            // belong to the document, which returns its buffer to the pool here.
            var elements = new List<JsonElement>();
            foreach (var element in document.RootElement.GetProperty("Documents").EnumerateArray())
                elements.Add(element.Clone());

            return new BufferedCursor(elements);
        }

        /// <summary>
        /// Builds the SDK query definition for a rendered statement.
        /// </summary>
        /// <param name="query">The statement and its bound parameters.</param>
        /// <returns>The query definition.</returns>
        public static QueryDefinition CreateDefinition(CosmosQuery query)
        {
            var definition = new QueryDefinition(query.Sql);

            foreach (var parameter in query.Parameters)
                definition = definition.WithParameter(parameter.Name, parameter.Value);

            return definition;
        }

        /// <summary>
        /// Builds a partition key from the values a predicate pinned.
        /// </summary>
        /// <remarks>
        /// Cosmos types partition key components, so each value is added by its JSON type. Anything
        /// else — an array or an object — cannot be a partition key, and yields no key rather than
        /// a wrong one.
        /// </remarks>
        /// <param name="values">One value per declared partition key path, in order.</param>
        /// <returns>The partition key, or <c>null</c> if the values cannot form one.</returns>
        public static PartitionKey? CreatePartitionKey(IReadOnlyList<object?>? values)
        {
            if (values is null || values.Count == 0)
                return null;

            var builder = new PartitionKeyBuilder();

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
                    case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                        builder.Add(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    default:
                        return null;
                }
            }

            return builder.Build();
        }

        /// <summary>
        /// Builds the request options for a statement.
        /// </summary>
        /// <remarks>
        /// A separate method because it is the only part of executing a statement that can be checked
        /// without a service, and each of the three things it decides is a decision rather than a
        /// default.
        /// </remarks>
        /// <param name="query">The statement being executed.</param>
        /// <param name="partitionKey">The partition key the statement resolved to, where it resolved to one.</param>
        /// <param name="indexMetrics">Whether to ask which indexes the statement used.</param>
        /// <returns>The options.</returns>
        public static QueryRequestOptions CreateRequestOptions(CosmosQuery query, PartitionKey? partitionKey, bool indexMetrics = false)
        {
            var options = new QueryRequestOptions();

            if (partitionKey is PartitionKey key)
            {
                options.PartitionKey = key;

                // One logical partition lives on one physical partition, so there is nothing for a
                // second worker to read. The SDK otherwise sizes its fan-out for a query that might
                // span every partition, which is machinery this statement has no use for.
                options.MaxConcurrency = 1;
            }

            // A page size, not a limit. The statement already says how many rows it wants; this stops
            // the service filling a default-sized page with rows the statement would discard.
            if (query.MaxItemCount is int maxItemCount)
                options.MaxItemCount = maxItemCount;

            // Which indexes the statement used, where the caller asked. A diagnostic rather than
            // something acted on.
            options.PopulateIndexMetrics = indexMetrics;

            return options;
        }

        /// <inheritdoc />
        public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
        {
            // An explicit key wins; otherwise use whatever the predicate pinned.
            var effective = partitionKey ?? CreatePartitionKey(query.PartitionKeyValues);

            // A statement that is exactly a lookup by id and a complete partition key is a read, not a
            // query: about 1 RU against 2.3 at best, and no query engine. The caller decided this is
            // such a statement; what arrives is the document rather than a projection, which is why the
            // row builder for this path differs.
            // The key must be complete: a prefix routes to a set of partitions and does not identify a
            // document, so ReadItem cannot use one.
            if (query.PointReadId is string id && query.PartitionKeyIsComplete && effective is PartitionKey readKey)
                return await ReadItemAsync(id, readKey, cancellationToken).ConfigureAwait(false);

            // The same recovery for a set of ids: ReadManyItemsAsync is charged as point reads,
            // and is gated by the same completeness the single read is.
            if (query.PointReadIds is { Count: > 0 } ids && query.PartitionKeyIsComplete && effective is PartitionKey manyKey)
                return await ReadManyAsync(ids, manyKey, cancellationToken).ConfigureAwait(false);

            var options = CreateRequestOptions(query, effective, _indexMetrics);

            // Started here and stopped when the cursor is disposed, so the span covers the statement
            // from its first request to the reader letting go of it, however many advances that took.
            var activity = CosmosInstrumentation.ActivitySource.StartActivity("cosmos.query");
            activity?.SetTag("db.query.text", query.Sql);
            activity?.SetTag("cosmos.container", _container.Id);

            // The stream iterator is used rather than the typed one so that results are read with
            // System.Text.Json. The SDK requires Newtonsoft.Json to be present, but nothing here
            // needs to go through it.
            var cursor = new FeedCursor(this, _container.GetItemQueryStreamIterator(CreateDefinition(query), requestOptions: options), activity);

            try
            {
                // The first page is the acquisition: a statement the service refuses fails the open
                // rather than the first advance, as a command's ExecuteReader does.
                await cursor.FetchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                cursor.Dispose();
                throw;
            }

            return cursor;
        }

        /// <inheritdoc />
        public async Task CreateItemAsync(byte[] document, PartitionKey partitionKey, CancellationToken cancellationToken = default)
        {
            if (document is null)
                throw new ArgumentNullException(nameof(document));

            using var stream = new System.IO.MemoryStream(document, writable: false);
            using var response = await _container.CreateItemStreamAsync(stream, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Write);

            // Surfaced rather than let through as a raw SDK exception, because a conflict is the one
            // failure a caller is likely to be handling deliberately: it is what INSERT means when a
            // document with that id is already in the partition.
            if (response.IsSuccessStatusCode == false)
                throw new CosmosExecutionException($"Creating a document in '{_container.Id}' failed with {(int)response.StatusCode} {response.StatusCode}. {response.ErrorMessage}".TrimEnd());
        }

        /// <inheritdoc />
        public async Task<bool> DeleteItemAsync(string id, PartitionKey partitionKey, CancellationToken cancellationToken = default)
        {
            if (id is null)
                throw new ArgumentNullException(nameof(id));

            using var response = await _container.DeleteItemStreamAsync(id, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Write);

            // Nothing to delete is not a failure. The rows were read before they were deleted, so a
            // document that is gone by the time the delete arrives was deleted by someone else — and
            // reporting a smaller count is the honest answer to that, rather than an error.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return false;

            if (response.IsSuccessStatusCode == false)
                throw new CosmosExecutionException($"Deleting document '{id}' from '{_container.Id}' failed with {(int)response.StatusCode} {response.StatusCode}. {response.ErrorMessage}".TrimEnd());

            return true;
        }

        /// <inheritdoc />
        public async Task<bool> DeletePartitionAsync(PartitionKey partitionKey, CancellationToken cancellationToken = default)
        {
            using var response = await _container.DeleteAllItemsByPartitionKeyStreamAsync(partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Write);

            if (response.IsSuccessStatusCode == false)
                throw new CosmosExecutionException($"Deleting the partition from '{_container.Id}' failed with {(int)response.StatusCode} {response.StatusCode}. {response.ErrorMessage}".TrimEnd());

            return true;
        }

        /// <inheritdoc />
        public async Task<bool> SupportsPartitionDeleteAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // A partition key value nothing can be stored under, so the call deletes nothing
                // whichever way it is answered. What is read is the refusal, not the effect: an
                // account without the capability answers 400 saying the feature is disabled.
                var probe = new PartitionKey("cosmos-adapter-capability-probe-" + Guid.NewGuid().ToString("n"));

                using var response = await _container.DeleteAllItemsByPartitionKeyStreamAsync(probe, cancellationToken: cancellationToken).ConfigureAwait(false);

                return response.IsSuccessStatusCode;
            }
            catch (CosmosException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<bool> ReplaceItemAsync(byte[] document, string id, PartitionKey partitionKey, CancellationToken cancellationToken = default)
        {
            if (document is null)
                throw new ArgumentNullException(nameof(document));
            if (id is null)
                throw new ArgumentNullException(nameof(id));

            using var stream = new System.IO.MemoryStream(document, writable: false);
            using var response = await _container.ReplaceItemStreamAsync(stream, id, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Write);

            // As for a delete: the rows were read before they were replaced, so a document that is
            // gone by the time the replace arrives was deleted by someone else, and a smaller count
            // is the honest answer rather than an error.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return false;

            if (response.IsSuccessStatusCode == false)
                throw new CosmosExecutionException($"Replacing document '{id}' in '{_container.Id}' failed with {(int)response.StatusCode} {response.StatusCode}. {response.ErrorMessage}".TrimEnd());

            return true;
        }


        /// <summary>
        /// A statement's results, read a page at a time under the token of the advance that needs the
        /// page.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The page the cursor is reading stays parsed until the next one is fetched, and each value is
        /// cloned out of it as it is reached — the element belongs to the page's document, which returns
        /// its buffer to the pool when it is disposed, and a value handed up the plan may be held for
        /// longer than that.
        /// </para>
        /// <para>
        /// <b><see cref="Read"/> blocks only where a page has to be fetched.</b> A value already in the
        /// page is reached without waiting, so a synchronous reader of a Cosmos plan waits once per round
        /// trip rather than once per row, which is what the sequence convention's bridge cost.
        /// </para>
        /// </remarks>
        sealed class FeedCursor : ClrCursor<JsonElement>
        {

            readonly CosmosQueryExecutor _executor;
            readonly FeedIterator _iterator;
            readonly System.Diagnostics.Activity? _activity;

            JsonDocument? _page;
            JsonElement.ArrayEnumerator _values;
            bool _hasValues;
            JsonElement _current;

            // Accumulated across continuations. The per-response measurement is what a collector
            // aggregates; this is what a reader of one trace wants, which is what the whole statement
            // cost rather than what its third page did.
            double _charge;
            int _pages;
            bool _finished;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            public FeedCursor(CosmosQueryExecutor executor, FeedIterator iterator, System.Diagnostics.Activity? activity)
            {
                _executor = executor;
                _iterator = iterator;
                _activity = activity;
            }

            /// <inheritdoc />
            public override JsonElement Current => _current;

            /// <inheritdoc />
            public override bool Read()
            {
                while (true)
                {
                    if (TryAdvance())
                        return true;

                    if (_iterator.HasMoreResults == false)
                        return Finish();

                    CosmosCursors.Wait(FetchAsync);
                }
            }

            /// <inheritdoc />
            public override async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    if (TryAdvance())
                        return true;

                    if (_iterator.HasMoreResults == false)
                        return Finish();

                    await FetchAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            /// <summary>
            /// Moves to the next value of the page already fetched, if it has one.
            /// </summary>
            bool TryAdvance()
            {
                if (_hasValues == false || _values.MoveNext() == false)
                    return false;

                // Clone: the element is owned by the page's document, which is disposed when the next
                // page arrives and returns its buffer to the pool.
                _current = _values.Current.Clone();
                return true;
            }

            /// <summary>
            /// Fetches the next page, under the token of whoever needed it.
            /// </summary>
            public async ValueTask<bool> FetchAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The page before is done with: every value was cloned out of it as it was reached.
                _page?.Dispose();
                _page = null;
                _hasValues = false;

                if (_iterator.HasMoreResults == false)
                    return false;

                _pages++;

                using var response = await _iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

                _executor.Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Query);
                _charge += response.Headers.RequestCharge;

                response.EnsureSuccessStatusCode();

                // Reported on the span rather than as a measurement: it is a paragraph of text naming
                // the indexes the service considered, which is a thing to read and not a thing to
                // aggregate. Present only on the first page, and only where it was asked for.
                if (response.IndexMetrics is string metrics && metrics.Length > 0)
                    _activity?.SetTag("cosmos.index_metrics", metrics);

                _page = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (_page.RootElement.TryGetProperty("Documents", out var documents))
                {
                    _values = documents.EnumerateArray();
                    _hasValues = true;
                }

                return true;
            }

            /// <summary>
            /// Records that the statement was read to its end.
            /// </summary>
            /// <remarks>
            /// Set at the end rather than incrementally: a span carries the totals it finished with, and a
            /// cursor disposed before it gets here is a span without them — which is itself the signal
            /// that the caller stopped reading.
            /// </remarks>
            bool Finish()
            {
                if (_finished == false)
                {
                    _finished = true;
                    _activity?.SetTag("cosmos.request_charge", _charge);
                    _activity?.SetTag("cosmos.pages", _pages);
                }

                return false;
            }

            /// <inheritdoc />
            public override void Dispose()
            {
                _page?.Dispose();
                _page = null;
                _hasValues = false;

                _iterator.Dispose();
                _activity?.Dispose();
            }

        }

        /// <summary>
        /// Values already read whole at the open, which is what a point read and a batch of them are.
        /// </summary>
        sealed class BufferedCursor(IReadOnlyList<JsonElement> values) : ClrCursor<JsonElement>
        {

            int _index = -1;

            /// <inheritdoc />
            public override JsonElement Current => values[_index];

            /// <inheritdoc />
            public override bool Read()
            {
                if (_index + 1 >= values.Count)
                {
                    _index = values.Count;
                    return false;
                }

                _index++;
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

    }

}
