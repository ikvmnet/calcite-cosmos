using System;
using System.Collections.Generic;
using System.Threading;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The declared and service-guaranteed facts about a container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container has no row schema — two items may share nothing but <c>id</c>. What it does
    /// have is planner metadata: a partition key, an indexing policy, unique key constraints. This
    /// type carries that, and nothing inferred from sampling documents. An inferred key or
    /// collation would produce a silently incorrect plan rather than a slow one.
    /// </para>
    /// <para>
    /// Everything here originates from the container definition or is guaranteed by the service.
    /// </para>
    /// </remarks>
    public sealed class CosmosContainerMetadata
    {

        /// <summary>
        /// The property every item carries, unique within a logical partition.
        /// </summary>
        public const string IdPropertyName = "id";

        /// <summary>
        /// The service-maintained last-modified timestamp, in epoch seconds.
        /// </summary>
        /// <remarks>
        /// The only temporal value in a container whose encoding is defined rather than a matter
        /// of application convention.
        /// </remarks>
        public const string TimestampPropertyName = "_ts";

        /// <summary>
        /// The service-maintained entity tag used for optimistic concurrency.
        /// </summary>
        public const string ETagPropertyName = "_etag";

        readonly string _name;
        readonly string[] _partitionKeyPaths;
        readonly CosmosCompositeIndex[] _compositeIndexes;
        readonly string[] _includedPaths;
        readonly string[] _excludedPaths;
        readonly string[] _fullTextPaths;
        readonly string[] _vectorPaths;
        readonly string[] _geographyPaths;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The container name.</param>
        /// <param name="partitionKeyPaths">The partition key paths in policy form, outermost first. Cosmos permits up to three for a hierarchical key.</param>
        /// <param name="compositeIndexes">The composite indexes declared by the indexing policy.</param>
        /// <param name="includedPaths">The indexing policy's included path patterns.</param>
        /// <param name="excludedPaths">The indexing policy's excluded path patterns.</param>
        /// <param name="fullTextPaths">The paths the container declares full text searchable.</param>
        /// <param name="vectorPaths">The paths the container declares vector searchable.</param>
        /// <param name="geographyPaths">The paths the container declares spatial, where it reads them as geography.</param>
        /// <param name="statistics">What the service reports about the container's size, or <c>null</c> where it was not asked.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is <c>null</c> or empty.</exception>
        public CosmosContainerMetadata(
            string name,
            IEnumerable<string>? partitionKeyPaths = null,
            IEnumerable<CosmosCompositeIndex>? compositeIndexes = null,
            IEnumerable<string>? includedPaths = null,
            IEnumerable<string>? excludedPaths = null,
            IEnumerable<string>? fullTextPaths = null,
            IEnumerable<string>? vectorPaths = null,
            IEnumerable<string>? geographyPaths = null,
            CosmosContainerStatistics? statistics = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"'{nameof(name)}' cannot be null or empty.", nameof(name));

            _name = name;
            _partitionKeyPaths = partitionKeyPaths is null ? Array.Empty<string>() : new List<string>(partitionKeyPaths).ToArray();
            _compositeIndexes = compositeIndexes is null ? Array.Empty<CosmosCompositeIndex>() : new List<CosmosCompositeIndex>(compositeIndexes).ToArray();
            _includedPaths = includedPaths is null ? Array.Empty<string>() : new List<string>(includedPaths).ToArray();
            _statistics = new Lazy<CosmosContainerStatistics?>(() => statistics, LazyThreadSafetyMode.ExecutionAndPublication);
            _excludedPaths = excludedPaths is null ? Array.Empty<string>() : new List<string>(excludedPaths).ToArray();
            _fullTextPaths = fullTextPaths is null ? Array.Empty<string>() : new List<string>(fullTextPaths).ToArray();
            _vectorPaths = vectorPaths is null ? Array.Empty<string>() : new List<string>(vectorPaths).ToArray();
            _geographyPaths = geographyPaths is null ? Array.Empty<string>() : new List<string>(geographyPaths).ToArray();

            if (_partitionKeyPaths.Length > 3)
                throw new ArgumentException("A container may declare at most three partition key paths.", nameof(partitionKeyPaths));
        }

        /// <summary>
        /// Gets the indexing policy's included path patterns.
        /// </summary>
        public IReadOnlyList<string> IncludedPaths => _includedPaths;

        /// <summary>
        /// Gets the indexing policy's excluded path patterns.
        /// </summary>
        public IReadOnlyList<string> ExcludedPaths => _excludedPaths;

        /// <summary>
        /// Determines whether a path is covered by the container's index.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This bears on cost, never on legality: a predicate or sort over an unindexed path still
        /// runs, it just scans. The default policy indexes everything, so a container that declares
        /// no included or excluded paths reports every path as indexed.
        /// </para>
        /// <para>
        /// Where included and excluded patterns conflict the more precise wins — a deeper path
        /// beats a shallower one, and <c>/?</c> beats <c>/*</c> at the same depth. <c>id</c> and
        /// <c>_ts</c> are always indexed and cannot be excluded.
        /// </para>
        /// </remarks>
        /// <param name="policyPath">The path in policy form, such as <c>/inventory/quantity</c>.</param>
        /// <returns><c>true</c> if the path is indexed; otherwise <c>false</c>.</returns>
        public bool IsPathIndexed(string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return false;

            if (string.Equals(policyPath, "/" + IdPropertyName, StringComparison.Ordinal) ||
                string.Equals(policyPath, "/" + TimestampPropertyName, StringComparison.Ordinal))
                return true;

            // No declared policy means the default, which indexes every property.
            if (_includedPaths.Length == 0 && _excludedPaths.Length == 0)
                return true;

            var included = BestMatch(_includedPaths, policyPath);
            var excluded = BestMatch(_excludedPaths, policyPath);

            // A tie favours inclusion, as does the absence of any exclusion.
            return included >= excluded && included >= 0;
        }

        /// <summary>
        /// Returns the specificity of the closest matching pattern, or <c>-1</c> when none match.
        /// </summary>
        static int BestMatch(IReadOnlyList<string> patterns, string path)
        {
            var best = -1;

            foreach (var pattern in patterns)
            {
                var score = Specificity(pattern, path);
                if (score > best)
                    best = score;
            }

            return best;
        }

        /// <summary>
        /// Scores how precisely a policy pattern matches a path.
        /// </summary>
        /// <remarks>
        /// A <c>/?</c> pattern addresses one scalar and must match exactly; a <c>/*</c> pattern
        /// addresses a subtree and matches any path beneath it. Depth is the primary ranking, with
        /// <c>/?</c> outranking <c>/*</c> at equal depth.
        /// </remarks>
        static int Specificity(string pattern, string path)
        {
            if (string.IsNullOrEmpty(pattern))
                return -1;

            var exact = pattern.EndsWith("/?", StringComparison.Ordinal);
            var subtree = pattern.EndsWith("/*", StringComparison.Ordinal);

            var prefix = exact || subtree ? pattern.Substring(0, pattern.Length - 2) : pattern;

            // The root pattern "/*" reduces to an empty prefix and matches everything.
            if (prefix.Length == 0)
                return subtree || exact ? 0 : -1;

            if (exact)
            {
                if (string.Equals(prefix, path, StringComparison.Ordinal) == false)
                    return -1;
            }
            else if (string.Equals(prefix, path, StringComparison.Ordinal) == false &&
                     path.StartsWith(prefix + "/", StringComparison.Ordinal) == false)
            {
                return -1;
            }

            var depth = 0;
            foreach (var c in prefix)
                if (c == '/')
                    depth++;

            // Two ranks per level leaves room for /? to outrank /* at the same depth.
            return (depth * 2) + (exact ? 1 : 0);
        }

        /// <summary>
        /// Gets the container name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets the partition key paths in policy form, outermost first.
        /// </summary>
        public IReadOnlyList<string> PartitionKeyPaths => _partitionKeyPaths;

        /// <summary>
        /// Gets the composite indexes declared by the indexing policy.
        /// </summary>
        public IReadOnlyList<CosmosCompositeIndex> CompositeIndexes => _compositeIndexes;

        /// <summary>
        /// Gets the paths the container declares full text searchable.
        /// </summary>
        /// <remarks>
        /// The union of the container's full text policy and the indexing policy's full text
        /// indexes, rather than either alone — see <see cref="IsPathFullTextSearchable"/> for why
        /// the two are read together.
        /// </remarks>
        public IReadOnlyList<string> FullTextPaths => _fullTextPaths;

        /// <summary>
        /// Gets the paths the container declares vector searchable.
        /// </summary>
        /// <remarks>
        /// The union of the container's vector embedding policy and the indexing policy's vector
        /// indexes — see <see cref="IsPathVectorSearchable"/>.
        /// </remarks>
        public IReadOnlyList<string> VectorPaths => _vectorPaths;

        /// <summary>
        /// Gets the paths the container declares spatial, where it reads them as geography.
        /// </summary>
        /// <remarks>
        /// Empty for a container whose <c>geospatialConfig</c> says <c>Geometry</c>, which is planar
        /// and which Calcite's own <c>ST_*</c> already describe correctly — see
        /// <see cref="IsPathGeography"/>.
        /// </remarks>
        public IReadOnlyList<string> GeographyPaths => _geographyPaths;

        /// <summary>
        /// Determines whether the container declares a path searchable by the full text functions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A legality test rather than a cost estimate, and the second one of those here after
        /// <see cref="IsSortSupported"/>. A full text predicate over a path the container declares
        /// nothing about was measured against a real account as a bodyless 400 that names neither
        /// the path nor the function, so pushing one is a defect rather than a pessimisation.
        /// </para>
        /// <para>
        /// <b>Why the policy and the index together.</b> Full text search takes two declarations —
        /// a container policy naming the searchable paths and their language, and a full text index
        /// over them — and the reference has moved on what each is for: it now describes the index
        /// as what a query <em>benefits from</em> rather than what it requires. The measurement
        /// here says a path with neither declaration is refused. Those agree on exactly one thing,
        /// so that is what this asks: has the container said anything at all about this path. It
        /// declines least, and it still catches the case that was diagnosed.
        /// </para>
        /// <para>
        /// Wildcards do not enter into it. The reference is explicit that <c>*</c> and <c>[]</c> are
        /// not accepted in a full text policy or index, so a declared path is a literal one and
        /// comparison is exact — unlike <see cref="IsPathIndexed"/>, where the patterns are the
        /// whole problem.
        /// </para>
        /// </remarks>
        /// <param name="policyPath">The path in policy form, such as <c>/description</c>.</param>
        /// <returns><c>true</c> where the container declares the path; otherwise <c>false</c>.</returns>
        public bool IsPathFullTextSearchable(string policyPath) => Declares(_fullTextPaths, policyPath);

        /// <summary>
        /// Determines whether the container declares a path searchable by <c>VECTORDISTANCE</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same test as <see cref="IsPathFullTextSearchable"/> over the vector declarations, and
        /// it reads the union for a sharper reason. The reference requires a <em>vector embedding
        /// policy</em> to perform a vector search at all, and treats the vector <em>index</em> as
        /// optional throughout — the function's own brute force argument is documented as using
        /// "any index defined on the vector property, if it exists". So an index without a policy
        /// cannot occur, a policy without an index runs and is merely slow, and the case worth
        /// declining is neither.
        /// </para>
        /// </remarks>
        /// <param name="policyPath">The path in policy form, such as <c>/embedding</c>.</param>
        /// <returns><c>true</c> where the container declares the path; otherwise <c>false</c>.</returns>
        public bool IsPathVectorSearchable(string policyPath) => Declares(_vectorPaths, policyPath);

        /// <summary>
        /// Determines whether the container declares a path spatial, and reads it as geography.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This decides a <em>type</em> rather than a legality, which is what separates it from the
        /// two tests above. A path this answers for is promoted to a column typed <c>GEOGRAPHY</c>,
        /// and one it declines stays inside the map column as it always was. The consequence of
        /// getting it wrong is not a refused query, it is a column that means metres claiming to
        /// mean degrees or the reverse.
        /// </para>
        /// <para>
        /// <b>The coordinate system is the container's, not the path's.</b> <c>geospatialConfig</c>
        /// is declared once for a container and applies to everything in it, so a container reading
        /// <c>Geometry</c> contributes no geography paths at all however many spatial indexes it
        /// declares. Its values are planar, and Calcite's own <c>ST_*</c> are already correct over
        /// them; typing them <c>GEOGRAPHY</c> would take a working query away. Absent config means
        /// geography, which is the service's own default rather than a guess.
        /// </para>
        /// </remarks>
        /// <param name="policyPath">The path in policy form, such as <c>/location</c>.</param>
        /// <returns><c>true</c> where the container declares the path and reads it as geography; otherwise <c>false</c>.</returns>
        public bool IsPathGeography(string policyPath) => Declares(_geographyPaths, policyPath);

        /// <summary>
        /// Determines whether a declared path list names the given path.
        /// </summary>
        static bool Declares(string[] paths, string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return false;

            foreach (var path in paths)
                if (string.Equals(path, policyPath, StringComparison.Ordinal))
                    return true;

            return false;
        }

        Lazy<CosmosContainerStatistics?> _statistics;

        /// <summary>
        /// Gets what the service reports about the container's size, or <c>null</c> where nothing asked
        /// or the account did not answer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A measurement rather than a declaration, and optional for that reason: a container built from
        /// a definition alone — which is what every test that does not need a service uses — has none,
        /// and the planner compares plans without a row count exactly as it did before.
        /// </para>
        /// <para>
        /// <b>Fetched when it is first asked for, not when the schema is built.</b> It costs two round
        /// trips per container, and a schema exposes every container of a database — or, at the account
        /// level, of every database — so paying for all of them to plan against one is the wrong trade.
        /// Flink's FLIP-231 makes the same call for the same reason, collecting connector statistics
        /// during optimisation rather than at catalog registration; Drill goes further and keeps them in
        /// a metastore that an explicit <c>ANALYZE</c> populates.
        /// </para>
        /// <para>
        /// The fetch blocks, because Calcite's <c>getStatistic</c> is synchronous and there is nowhere to
        /// await. That is the one place this adapter does block — the planning path, once per container —
        /// and it is why the data path refuses to.
        /// </para>
        /// <para>
        /// <b>And it expires.</b> A row count is a measurement of something that keeps changing, so a
        /// schema living for the life of a process would otherwise plan for ever against the count
        /// it happened to read first. What expiry costs is a round trip; what it buys is a number
        /// that still describes the container. The capability beside it does <em>not</em> expire and
        /// should not: it changes only when someone enables a preview on the account, which no
        /// running process can observe happening.
        /// </para>
        /// </remarks>
        public CosmosContainerStatistics? Statistics
        {
            get
            {
                if (_statisticsProvider is null)
                    return _statistics.Value;

                lock (_statisticsGate)
                {
                    var now = (_time ?? TimeProvider.System).GetUtcNow();

                    if (_statisticsFetched is DateTimeOffset fetched && now - fetched < _statisticsTimeToLive)
                        return _statisticsValue;

                    _statisticsValue = _statisticsProvider();
                    _statisticsFetched = now;

                    return _statisticsValue;
                }
            }
        }

        /// <summary>
        /// How long a fetched row count is believed before it is read again.
        /// </summary>
        /// <remarks>
        /// Five minutes, and the number is a judgement rather than a measurement — but not an
        /// arbitrary one. The count the service reports already lags: measured, it reports zero
        /// immediately after documents are written, so expiring it every few seconds would spend
        /// round trips re-reading a number that had not caught up. What a stale count costs is a
        /// worse plan, never a wrong answer, which is what makes a default defensible at all where
        /// this adapter refuses to guess about semantics.
        /// </remarks>
        public static readonly TimeSpan DefaultStatisticsTimeToLive = TimeSpan.FromMinutes(5);

        readonly object _statisticsGate = new();
        Func<CosmosContainerStatistics?>? _statisticsProvider;
        CosmosContainerStatistics? _statisticsValue;
        DateTimeOffset? _statisticsFetched;
        TimeSpan _statisticsTimeToLive = DefaultStatisticsTimeToLive;
        TimeProvider? _time;

        /// <summary>
        /// Returns the same metadata carrying the given statistics.
        /// </summary>
        /// <remarks>
        /// The declaration and the measurement are read from different places — the definition and a
        /// response header — so they are attached in two steps rather than threaded through every
        /// constructor call.
        /// </remarks>
        /// <param name="statistics">What the service reports, or <c>null</c>.</param>
        /// <returns>The metadata.</returns>
        public CosmosContainerMetadata WithStatistics(CosmosContainerStatistics? statistics)
        {
            return statistics is null
                ? this
                : new CosmosContainerMetadata(_name, _partitionKeyPaths, _compositeIndexes, _includedPaths, _excludedPaths, _fullTextPaths, _vectorPaths, _geographyPaths, statistics);
        }

        Lazy<bool> _partitionKeyDelete = new(() => false, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Gets whether the account will delete a whole logical partition in one request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A capability rather than a declaration: the operation is a preview the service enables
        /// per account, so nothing in the container definition says whether it is available and
        /// only asking can answer. Asked at most once, and only where a rule needs it — a plan with
        /// no whole-partition <c>DELETE</c> in it never pays for the question.
        /// </para>
        /// <para>
        /// False where nothing supplied a probe, which is the safe answer: the statement then plans
        /// as the scan and per-document delete it has always been.
        /// </para>
        /// </remarks>
        public bool SupportsPartitionKeyDelete => _partitionKeyDelete.Value;

        /// <summary>
        /// Returns the same metadata whose whole-partition delete capability is probed on first use.
        /// </summary>
        /// <param name="probe">Answers whether the account will accept the operation.</param>
        /// <returns>The metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="probe"/> is <c>null</c>.</exception>
        public CosmosContainerMetadata WithPartitionKeyDeleteProbe(Func<bool> probe)
        {
            if (probe is null)
                throw new ArgumentNullException(nameof(probe));

            var metadata = new CosmosContainerMetadata(_name, _partitionKeyPaths, _compositeIndexes, _includedPaths, _excludedPaths, _fullTextPaths, _vectorPaths, _geographyPaths);
            metadata._statistics = _statistics;
            metadata._statisticsProvider = _statisticsProvider;
            metadata._statisticsTimeToLive = _statisticsTimeToLive;
            metadata._time = _time;

            // Not expiring, and deliberately: a capability changes when someone enables a preview
            // on the account, which is not something a running process can observe happening.
            metadata._partitionKeyDelete = new Lazy<bool>(probe, LazyThreadSafetyMode.ExecutionAndPublication);
            return metadata;
        }

        /// <summary>
        /// Returns the same metadata whose statistics are fetched on first use.
        /// </summary>
        /// <remarks>
        /// The provider is invoked on first use and then again whenever the answer has expired, and
        /// a container nothing plans against never invokes it at all — which is the point. A
        /// provider returning <c>null</c> leaves the planner where it would have been without one.
        /// </remarks>
        /// <param name="provider">Fetches the statistics.</param>
        /// <param name="timeToLive">How long an answer is believed, or <c>null</c> for <see cref="DefaultStatisticsTimeToLive"/>.</param>
        /// <param name="time">The clock, replaceable for tests.</param>
        /// <returns>The metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeToLive"/> is not positive.</exception>
        public CosmosContainerMetadata WithStatisticsProvider(Func<CosmosContainerStatistics?> provider, TimeSpan? timeToLive = null, TimeProvider? time = null)
        {
            if (provider is null)
                throw new ArgumentNullException(nameof(provider));

            if (timeToLive is TimeSpan span && span <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeToLive), "A statistics time to live must be positive.");

            var metadata = new CosmosContainerMetadata(_name, _partitionKeyPaths, _compositeIndexes, _includedPaths, _excludedPaths, _fullTextPaths, _vectorPaths, _geographyPaths);
            metadata._statisticsProvider = provider;
            metadata._statisticsTimeToLive = timeToLive ?? DefaultStatisticsTimeToLive;
            metadata._time = time;
            metadata._partitionKeyDelete = _partitionKeyDelete;
            return metadata;
        }

        /// <summary>
        /// Determines whether an <c>ORDER BY</c> over the given keys is legal against this container.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a legality test, not a cost estimate. The distinction matters:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// A sort on a single property is always legal. If that property happens not to be
        /// indexed the query is more expensive, but it still runs.
        /// </description></item>
        /// <item><description>
        /// A sort on two or more properties requires a matching composite index. Without one the
        /// service rejects the query outright, so pushing it down would be a defect rather than a
        /// pessimisation.
        /// </description></item>
        /// </list>
        /// <para>
        /// Conversion rules must consult this before converting a <c>Sort</c>.
        /// </para>
        /// </remarks>
        /// <param name="keys">The requested sort keys, in order.</param>
        /// <returns><c>true</c> if the sort may be pushed down; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <c>null</c>.</exception>
        public bool IsSortSupported(IReadOnlyList<CosmosSortKey> keys)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            if (keys.Count <= 1)
                return true;

            foreach (var index in _compositeIndexes)
                if (index.Supports(keys))
                    return true;

            return false;
        }

    }

}
