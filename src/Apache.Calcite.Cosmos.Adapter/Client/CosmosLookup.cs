using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Extensions.Runtime;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Joins a sequence to a container by fetching only the documents its keys could match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relational join is not expressible in Cosmos, so without this both sides are read whole and
    /// joined in process. Here one side pays for the other: a batch of build rows contributes its
    /// distinct keys, and the statement run against the container carries them, so the service returns
    /// the documents that could join rather than all of them.
    /// </para>
    /// <para>
    /// This is Flink's lookup join rather than anything invented here. The correlation variable Calcite
    /// uses to express the shape is consumed by the rule and never reaches this point; what arrives is
    /// key values, which is what <c>asyncLookup(RowData)</c> receives there.
    /// </para>
    /// <para>
    /// The statement is rendered once, with a fixed number of key parameters. Only their values change
    /// per batch — which is why the batch size is fixed and a short batch pads rather than re-renders.
    /// </para>
    /// </remarks>
    public static class CosmosLookup
    {

        /// <summary>
        /// Reduces a key to a form two sides of a join can be compared in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two sides arrive by different routes — one from whatever the build side is, the other
        /// from JSON — so the same key can be an <see cref="int"/> on one side and a
        /// <see cref="long"/> or <see cref="double"/> on the other. Comparing the boxes directly would
        /// silently drop matching rows, which is the worst kind of wrong answer this could give.
        /// </para>
        /// <para>
        /// Every number is therefore compared as a <see cref="double"/>. That is what Cosmos stores —
        /// JSON has one numeric type — so it loses nothing the service was preserving. Types outside
        /// this set never arrive, because the rule declines a key that is not a string, a boolean, or a
        /// number.
        /// </para>
        /// </remarks>
        /// <param name="key">The key as either side produced it.</param>
        /// <returns>The comparable form, or <c>null</c> where the key is absent.</returns>
        public static object? Normalize(object? key)
        {
            return key switch
            {
                null => null,
                string or bool => key,
                sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                    => Convert.ToDouble(key, CultureInfo.InvariantCulture),
                _ => key,
            };
        }

        /// <summary>
        /// Builds the statement for one batch by binding its keys.
        /// </summary>
        /// <remarks>
        /// Padded to the number of parameters the statement was rendered with, by repeating a key it
        /// already carries. <c>k IN (a, b, b, b)</c> selects what <c>k IN (a, b)</c> selects, so the
        /// padding costs a longer statement and changes no answer — where re-rendering per batch would
        /// mean a statement that varies with the data.
        /// </remarks>
        /// <param name="query">The statement, rendered with <paramref name="parameterCount"/> key parameters.</param>
        /// <param name="prefix">The key parameters' name prefix.</param>
        /// <param name="parameterCount">How many key parameters the statement carries.</param>
        /// <param name="keys">The batch's distinct keys, at most <paramref name="parameterCount"/> of them.</param>
        /// <returns>The statement with every parameter bound.</returns>
        public static CosmosQuery Bind(CosmosQuery query, string prefix, int parameterCount, IReadOnlyList<object?> keys)
        {
            if (keys.Count == 0 || keys.Count > parameterCount)
                throw new ArgumentException($"A batch carries between 1 and {parameterCount} keys; {keys.Count} were supplied.", nameof(keys));

            var parameters = new List<CosmosParameter>(query.Parameters.Count + parameterCount);
            parameters.AddRange(query.Parameters);

            for (var i = 0; i < parameterCount; i++)
                parameters.Add(new CosmosParameter(prefix + i.ToString(CultureInfo.InvariantCulture), keys[i < keys.Count ? i : keys.Count - 1]));

            return query with { Parameters = parameters };
        }

        /// <summary>
        /// Joins <paramref name="build"/> to a container on one equality, fetching per batch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An inner join, and only an inner join. Anything else declines at the rule and is joined in
        /// process as before, which is what every other adapter does with every join.
        /// </para>
        /// <para>
        /// Rows are emitted in build order within a batch. The join is unordered, so this is not a
        /// promise — it is what falls out of probing in the order the batch was read, and keeping it
        /// costs nothing.
        /// </para>
        /// <para>
        /// <b>Nothing is acquired at the open.</b> A batch is read and fetched by the advance that runs
        /// out of rows, under that advance's token, so a join whose reader stops early never asks for the
        /// batches it did not reach. That makes this the open for both bodies: the synchronous one hands
        /// it an input opened synchronously, and <see cref="JoinAsync{TBuild, TProbe, TResult}"/> awaits
        /// the input's open and then calls this. A synchronous advance reads the build side with
        /// <c>Read</c> and blocks only for the fetch.
        /// </para>
        /// </remarks>
        /// <typeparam name="TBuild">The build side's row type.</typeparam>
        /// <typeparam name="TProbe">The container's row type.</typeparam>
        /// <typeparam name="TResult">The joined row type.</typeparam>
        /// <param name="build">The side whose keys are pushed down, already opened; the join owns it.</param>
        /// <param name="executor">Executes the statement.</param>
        /// <param name="query">The statement, rendered with <paramref name="batchSize"/> key parameters.</param>
        /// <param name="prefix">The key parameters' name prefix.</param>
        /// <param name="batchSize">How many build rows one fetch serves, and how many key parameters the statement carries.</param>
        /// <param name="buildKey">Reads the join key from a build row.</param>
        /// <param name="rowBuilder">Builds a container row from the JSON value it arrived as.</param>
        /// <param name="probeKey">Reads the join key from a container row.</param>
        /// <param name="resultSelector">Combines a matching pair.</param>
        /// <param name="cacheSize">
        /// How many keys to remember for the length of this join, or zero to remember none. Reference
        /// data is looked up repeatedly by definition, and a remembered key costs no request units at
        /// all.
        /// </param>
        /// <param name="shared">
        /// The container's cache across executions, or <c>null</c> where none is configured. Consulted
        /// beneath the per-join cache and populated by every fetch; see <c>DESIGN.md</c> under
        /// <em>The lookup join's caches</em>.
        /// </param>
        /// <returns>The joined rows.</returns>
        /// <exception cref="ArgumentNullException">Any required argument is <c>null</c>.</exception>
        public static IClrCursor<TResult> Join<TBuild, TProbe, TResult>(
            IClrCursor<TBuild> build,
            ICosmosQueryExecutor executor,
            CosmosQuery query,
            string prefix,
            int batchSize,
            Func<TBuild, object?> buildKey,
            Func<JsonElement, TProbe> rowBuilder,
            Func<TProbe, object?> probeKey,
            Func<TBuild, TProbe, TResult> resultSelector,
            int cacheSize = 0,
            CosmosLookupCache? shared = null)
        {
            if (build is null)
                throw new ArgumentNullException(nameof(build));
            if (executor is null)
                throw new ArgumentNullException(nameof(executor));
            if (buildKey is null)
                throw new ArgumentNullException(nameof(buildKey));
            if (rowBuilder is null)
                throw new ArgumentNullException(nameof(rowBuilder));
            if (probeKey is null)
                throw new ArgumentNullException(nameof(probeKey));
            if (resultSelector is null)
                throw new ArgumentNullException(nameof(resultSelector));
            if (batchSize < 1)
                throw new ArgumentOutOfRangeException(nameof(batchSize));

            return new LookupCursor<TBuild, TProbe, TResult>(build, executor, query, prefix, batchSize, buildKey, rowBuilder, probeKey, resultSelector, cacheSize, shared);
        }

        /// <summary>
        /// Awaits the build side's open, and joins it as <see cref="Join{TBuild, TProbe, TResult}"/> does.
        /// </summary>
        /// <remarks>
        /// The awaiting body's open. The token is the open's, and nothing here uses it: the join acquires
        /// nothing at its open, and each fetch runs under the token of the advance that needs it.
        /// </remarks>
        public static async ValueTask<IClrCursor<TResult>> JoinAsync<TBuild, TProbe, TResult>(
            ValueTask<IClrCursor<TBuild>> build,
            ICosmosQueryExecutor executor,
            CosmosQuery query,
            string prefix,
            int batchSize,
            Func<TBuild, object?> buildKey,
            Func<JsonElement, TProbe> rowBuilder,
            Func<TProbe, object?> probeKey,
            Func<TBuild, TProbe, TResult> resultSelector,
            int cacheSize,
            CosmosLookupCache? shared,
            CancellationToken cancellationToken)
        {
            return Join(await build.ConfigureAwait(false), executor, query, prefix, batchSize, buildKey, rowBuilder, probeKey, resultSelector, cacheSize, shared);
        }

        /// <summary>
        /// The cursor of <see cref="Join{TBuild, TProbe, TResult}"/>: a batch of build rows at a time,
        /// fetched once and then paired up row by row.
        /// </summary>
        sealed class LookupCursor<TBuild, TProbe, TResult> : ClrCursor<TResult>
        {

            readonly IClrCursor<TBuild> _build;
            readonly ICosmosQueryExecutor _executor;
            readonly CosmosQuery _query;
            readonly string _prefix;
            readonly int _batchSize;
            readonly Func<TBuild, object?> _buildKey;
            readonly Func<JsonElement, TProbe> _rowBuilder;
            readonly Func<TProbe, object?> _probeKey;
            readonly Func<TBuild, TProbe, TResult> _resultSelector;
            readonly int _cacheSize;
            readonly CosmosLookupCache? _shared;

            // Held for the length of this join and no longer. A cache that outlived one execution
            // would have to answer for staleness, and this one cannot be stale in a way the join was
            // not already: the container is read across many requests either way, and remembering an
            // answer makes the result more self-consistent rather than less.
            readonly Dictionary<object, List<TProbe>>? _cache;

            // The identity the shared cache remembers this statement's answers under, computed once:
            // the batches differ only in their keys.
            readonly string? _statement;

            readonly List<TBuild> _batch;

            // What the current batch resolves to, whether remembered or fetched.
            readonly Dictionary<object, List<TProbe>> _lookup = new();

            // Where the pairing of the current batch has got to: the build row, and the next of its
            // matches.
            int _row;
            List<TProbe>? _matches;
            int _match;

            bool _buildDone;
            TResult _current = default!;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            public LookupCursor(
                IClrCursor<TBuild> build,
                ICosmosQueryExecutor executor,
                CosmosQuery query,
                string prefix,
                int batchSize,
                Func<TBuild, object?> buildKey,
                Func<JsonElement, TProbe> rowBuilder,
                Func<TProbe, object?> probeKey,
                Func<TBuild, TProbe, TResult> resultSelector,
                int cacheSize,
                CosmosLookupCache? shared)
            {
                _build = build;
                _executor = executor;
                _query = query;
                _prefix = prefix;
                _batchSize = batchSize;
                _buildKey = buildKey;
                _rowBuilder = rowBuilder;
                _probeKey = probeKey;
                _resultSelector = resultSelector;
                _cacheSize = cacheSize;
                _shared = shared;
                _cache = cacheSize > 0 ? new Dictionary<object, List<TProbe>>() : null;
                _statement = shared is null ? null : CosmosLookupCache.Statement(query);
                _batch = new List<TBuild>(batchSize);
            }

            /// <inheritdoc />
            public override TResult Current => _current;

            /// <inheritdoc />
            public override bool Read()
            {
                while (true)
                {
                    if (TryPair())
                        return true;

                    if (_buildDone)
                        return false;

                    _batch.Clear();
                    while (_batch.Count < _batchSize && _build.Read())
                        _batch.Add(_build.Current);

                    if (Filled() == false)
                        return false;

                    CosmosCursors.Wait(FetchAsync);
                }
            }

            /// <inheritdoc />
            public override async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    if (TryPair())
                        return true;

                    if (_buildDone)
                        return false;

                    _batch.Clear();
                    while (_batch.Count < _batchSize && await _build.ReadAsync(cancellationToken).ConfigureAwait(false))
                        _batch.Add(_build.Current);

                    if (Filled() == false)
                        return false;

                    await FetchAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            /// <summary>
            /// Notes whether the build side ran out while the batch was being read, and answers whether
            /// there is a batch to fetch.
            /// </summary>
            bool Filled()
            {
                if (_batch.Count < _batchSize)
                    _buildDone = true;

                return _batch.Count > 0;
            }

            /// <summary>
            /// Moves to the next matching pair of the current batch, if it has one.
            /// </summary>
            bool TryPair()
            {
                while (true)
                {
                    if (_matches is not null && _match < _matches.Count)
                    {
                        _current = _resultSelector(_batch[_row], _matches[_match++]);
                        return true;
                    }

                    if (_matches is not null)
                    {
                        _matches = null;
                        _row++;
                    }

                    if (_row >= _batch.Count)
                        return false;

                    if (Normalize(_buildKey(_batch[_row])) is object key && _lookup.TryGetValue(key, out var matches))
                    {
                        _matches = matches;
                        _match = 0;
                    }
                    else
                    {
                        _row++;
                    }
                }
            }

            /// <summary>
            /// Resolves the current batch's keys, fetching those nothing remembers.
            /// </summary>
            /// <remarks>
            /// A build row whose key is null matches nothing under an inner join — <c>null = x</c> is never
            /// true — so such rows contribute no key and take no part. A batch of only those fetches
            /// nothing at all, which is the whole point of doing this.
            /// </remarks>
            async ValueTask<bool> FetchAsync(CancellationToken cancellationToken)
            {
                _lookup.Clear();
                _row = 0;
                _matches = null;
                _match = 0;

                // Distinct, because the keys are data here rather than a predicate: a hundred build rows
                // over ten keys fetch ten. This is what the statement could not have done for itself.
                var keys = new List<object?>();
                var seen = new HashSet<object>();

                foreach (var row in _batch)
                {
                    if (Normalize(_buildKey(row)) is not object key || seen.Add(key) == false)
                        continue;

                    if (_cache is not null && _cache.TryGetValue(key, out var remembered))
                    {
                        _lookup[key] = remembered;
                        continue;
                    }

                    // Beneath the per-join cache: an answer another execution fetched, rebuilt through
                    // this plan's own row builder.
                    if (_shared is not null && _shared.TryGet(_statement!, key, out var held))
                    {
                        var rows = new List<TProbe>(held.Count);
                        foreach (var element in held)
                            rows.Add(_rowBuilder(element));

                        _lookup[key] = rows;

                        if (_cache is not null && _cache.Count < _cacheSize)
                            _cache[key] = rows;

                        continue;
                    }

                    keys.Add(key);
                }

                if (keys.Count == 0)
                    return true;

                var fetched = new Dictionary<object, List<TProbe>>();

                // What crossed the wire for each key, kept only where a shared cache will remember
                // it. The elements are already standalone: the executor clones what it yields.
                var elements = _shared is null ? null : new Dictionary<object, List<JsonElement>>();

                var cursor = await _executor.OpenAsync(Bind(_query, _prefix, _batchSize, keys), cancellationToken: cancellationToken).ConfigureAwait(false);

                await using (cursor.ConfigureAwait(false))
                {
                    while (await cursor.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var element = cursor.Current;
                        var probe = _rowBuilder(element);
                        if (Normalize(_probeKey(probe)) is not object key)
                            continue;

                        if (fetched.TryGetValue(key, out var rows) == false)
                            fetched[key] = rows = new List<TProbe>();

                        rows.Add(probe);

                        if (elements is not null)
                        {
                            if (elements.TryGetValue(key, out var held) == false)
                                elements[key] = held = new List<JsonElement>();

                            held.Add(element);
                        }
                    }
                }

                foreach (var key in keys)
                {
                    if (key is null)
                        continue;

                    // Absence is remembered too. A key the container has nothing for is the case a
                    // cache most needs to hold: without it, every batch mentioning that key asks
                    // again and is told nothing again.
                    var rows = fetched.TryGetValue(key, out var found) ? found : new List<TProbe>();

                    _lookup[key] = rows;

                    // Filled to the bound and then left alone, rather than evicted. Nothing here knows
                    // which key is worth keeping, and a wrong eviction costs a request — so the simple
                    // rule is the honest one, and the bound is what stops a large build side from
                    // being remembered whole.
                    if (_cache is not null && _cache.Count < _cacheSize)
                        _cache[key] = rows;

                    if (_shared is not null)
                        _shared.Set(_statement!, key, elements!.TryGetValue(key, out var held) ? held : Array.Empty<JsonElement>());
                }

                return true;
            }

            /// <inheritdoc />
            public override void Dispose()
            {
                _build.Dispose();
            }

            /// <inheritdoc />
            public override ValueTask DisposeAsync()
            {
                return _build.DisposeAsync();
            }

        }

    }

}
