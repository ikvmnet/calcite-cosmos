using System;
using System.Collections.Generic;
using System.Threading;

using Apache.Calcite.Cosmos.Facts;

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
        readonly bool _readsGeography;
        readonly IReadOnlyList<string>[] _uniqueKeys;
        readonly string[] _computedProperties;
        readonly string[] _includedPatterns;
        readonly string[] _excludedPatterns;

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
        /// <param name="readsGeography">Whether the container reads coordinates as geography rather than as a plane.</param>
        /// <param name="statistics">What the service reports about the container's size, or <c>null</c> where it was not asked.</param>
        /// <param name="uniqueKeys">The path sets of the container's unique key policy, each in policy form.</param>
        /// <param name="computedProperties">The names of the container's computed properties.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is <c>null</c> or empty.</exception>
        public CosmosContainerMetadata(
            string name,
            IEnumerable<string>? partitionKeyPaths = null,
            IEnumerable<CosmosCompositeIndex>? compositeIndexes = null,
            IEnumerable<string>? includedPaths = null,
            IEnumerable<string>? excludedPaths = null,
            IEnumerable<string>? fullTextPaths = null,
            IEnumerable<string>? vectorPaths = null,
            bool readsGeography = true,
            CosmosContainerStatistics? statistics = null,
            IEnumerable<IReadOnlyList<string>>? uniqueKeys = null,
            IEnumerable<string>? computedProperties = null)
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
            _readsGeography = readsGeography;
            _uniqueKeys = uniqueKeys is null ? Array.Empty<IReadOnlyList<string>>() : new List<IReadOnlyList<string>>(uniqueKeys).ToArray();
            _computedProperties = computedProperties is null ? Array.Empty<string>() : new List<string>(computedProperties).ToArray();
            _includedPatterns = _includedPaths.Length == 0 && _excludedPaths.Length == 0 ? DefaultIncludedPatterns : Array.ConvertAll(_includedPaths, Unquoted);
            _excludedPatterns = WithServiceExclusions(Array.ConvertAll(_excludedPaths, Unquoted), _includedPatterns);

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
        /// Gets the names of the container's computed properties.
        /// </summary>
        /// <remarks>
        /// A computed property is addressed like a top-level property, and indexed only where an included path names it:
        /// the wildcard that indexes every other path does not reach one. See <see cref="IsPathIndexed"/>.
        /// </remarks>
        public IReadOnlyList<string> ComputedProperties => _computedProperties;

        /// <summary>
        /// Determines whether a path is covered by the container's index.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Cost for a filter, legality for a sort.</b> A predicate over an unindexed path still runs, it just scans —
        /// and so do <c>MIN</c> and <c>MAX</c>. An <c>ORDER BY</c> does not: measured on Azure, ordering by a path the
        /// policy excludes is a 400, "The index path corresponding to the specified order-by item is excluded", and the
        /// same for a computed property no included path names. The emulator orders by either, which is why it showed
        /// nothing. So <see cref="IsSortSupported"/> asks this of every key it is handed.
        /// </para>
        /// <para>
        /// <b>The service's own defaults, policy or none.</b> The default policy indexes every path, and every policy
        /// the service hands back excludes <c>/"_etag"/?</c> unless it names <c>_etag</c> itself — both read into the
        /// patterns when the metadata is made (<see cref="ServiceExcludedPatterns"/>), so that what a declared policy
        /// leaves out, the emulator reading none of it back, is the service's and not a case here. A computed property
        /// is reached by no wildcard: only an included path naming one indexes it. <c>id</c> and <c>_ts</c> are always
        /// indexed and cannot be excluded.
        /// </para>
        /// <para>
        /// Where included and excluded patterns conflict the more precise wins — a deeper path beats a shallower one, and
        /// <c>/?</c> beats <c>/*</c> at the same depth. A segment the policy quotes, as the service writes
        /// <c>/"_etag"/?</c>, is the segment unquoted.
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

            // A computed property is reached by no wildcard: only a pattern naming it includes it.
            var named = Array.IndexOf(_computedProperties, FirstSegment(policyPath)) >= 0;

            var included = BestMatch(_includedPatterns, policyPath, named ? 2 : 0);
            var excluded = BestMatch(_excludedPatterns, policyPath, 0);

            // A tie favours inclusion, as does the absence of any exclusion.
            return included >= excluded && included >= 0;
        }

        /// <summary>
        /// The included patterns of a container that declares no policy: the service's default, every path.
        /// </summary>
        static readonly string[] DefaultIncludedPatterns = { "/*" };

        /// <summary>
        /// The patterns the service excludes from every policy it hands back, unquoted: <c>/"_etag"/?</c>.
        /// </summary>
        /// <remarks>
        /// Measured on the probe account: the default policy, and every policy read back, excludes it, and an
        /// <c>ORDER BY</c> over it is a 400. The emulator reads none of that back, and a declared policy need not
        /// say it, so it is added to whatever is declared — unless an included pattern names the path, which is the
        /// one way a policy asks for it.
        /// </remarks>
        static readonly string[] ServiceExcludedPatterns = { "/" + ETagPropertyName + "/?" };

        /// <summary>
        /// Returns the declared excluded patterns with the service's own beside them, each one an included pattern does
        /// not name.
        /// </summary>
        static string[] WithServiceExclusions(string[] excluded, string[] included)
        {
            var patterns = new List<string>(excluded);

            foreach (var pattern in ServiceExcludedPatterns)
                if (patterns.Contains(pattern) == false && Array.Exists(included, i => i.Length > 1 && FirstSegment(i) == FirstSegment(pattern)) == false)
                    patterns.Add(pattern);

            return patterns.ToArray();
        }

        /// <summary>
        /// Returns a policy pattern with each quoted segment unquoted, as the service quotes <c>/"_etag"/?</c>.
        /// </summary>
        static string Unquoted(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern.IndexOf('"') < 0)
                return pattern;

            var segments = pattern.Split('/');
            for (var i = 0; i < segments.Length; i++)
                if (segments[i].Length >= 2 && segments[i][0] == '"' && segments[i][^1] == '"')
                    segments[i] = segments[i][1..^1];

            return string.Join("/", segments);
        }

        /// <summary>
        /// Returns the first segment of a policy-form path.
        /// </summary>
        static string FirstSegment(string policyPath)
        {
            var end = policyPath.IndexOf('/', 1);
            return end < 0 ? policyPath[1..] : policyPath[1..end];
        }

        /// <summary>
        /// Returns the specificity of the closest matching pattern, or <c>-1</c> when none match.
        /// </summary>
        /// <param name="patterns">The patterns, unquoted.</param>
        /// <param name="path">The path.</param>
        /// <param name="least">The least specificity that counts: two leaves out the root wildcard.</param>
        static int BestMatch(IReadOnlyList<string> patterns, string path, int least)
        {
            var best = -1;

            foreach (var pattern in patterns)
            {
                var score = Specificity(pattern, path);
                if (score >= least && score > best)
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
        /// Gets whether the container reads coordinates as geography rather than as a plane.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>geospatialConfig</c> is declared once for a container and applies to everything in it,
        /// so this is a container-wide fact and not a per-path one. It decides whether an
        /// <c>CLR_ST_GEOG_*</c> call may be pushed at all: the Cosmos spelling is the unprefixed one, and
        /// what that spelling <em>means</em> at the service is whatever this says. Pushing a geodesic
        /// call into a container reading <c>Geometry</c> would render <c>ST_DISTANCE</c> and get a
        /// planar answer back with nothing said.
        /// </para>
        /// <para>
        /// Absent configuration is geography, which is the service's own default rather than a guess.
        /// </para>
        /// </remarks>
        public bool ReadsGeography => _readsGeography;

        /// <summary>
        /// Determines whether the container declares a path searchable by the full text functions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A cost input, and no longer a legality test. It was the second of those after
        /// <see cref="IsSortSupported"/>, on a measurement that a full text predicate over a path the
        /// container declares nothing about answers a bodyless 400. That does not reproduce: measured
        /// again against three accounts and four containers (#85), the service answers the predicates
        /// and the score over an undeclared path, over a container with no policy, and on an account
        /// without the full text capability. So the question this answers is whether the call is
        /// served by the full text index or by a scan, and <see cref="Rel.CosmosFilter"/> prices it.
        /// </para>
        /// <para>
        /// <b>Why the policy and the index together.</b> Full text search takes two declarations —
        /// a container policy naming the searchable paths and their language, and a full text index
        /// over them — and the reference describes the index as what a query <em>benefits from</em>
        /// rather than what it requires. Read as one list, this says whether the container has said
        /// anything at all about a path. A path in the policy and not in the index most likely scans
        /// too, and pricing it as one needs the two lists read apart, which <c>TODO.md</c> carries.
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
        /// Forgets a fetched row count, so that the next plan to ask reads it again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A time to live answers <em>eventually</em>; this answers <em>now</em>. The moment worth
        /// re-reading after is a bulk load, and only the caller knows when that finished — the clock
        /// does not, and five minutes of planning against the old number is exactly the cost the
        /// default was chosen to accept for want of being told.
        /// </para>
        /// <para>
        /// <b>It forgets rather than fetches</b>, for two reasons. A container nothing plans against
        /// should not be paid for, which is the same argument the provider is lazy for at all. And the
        /// count the service reports lags the writes that produced it — measured, it reports zero
        /// immediately after documents are written — so fetching at the moment a caller says the load
        /// is done would capture the number least likely to be right.
        /// </para>
        /// <para>
        /// Where no provider is attached this does nothing, which is the honest answer: metadata built
        /// from a container definition alone has no row count to forget and nothing to ask.
        /// </para>
        /// </remarks>
        public void RefreshStatistics()
        {
            lock (_statisticsGate)
                _statisticsFetched = null;
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
        /// Returns a copy carrying everything attached since construction, for a <c>With</c> to change
        /// the one thing it is about.
        /// </summary>
        /// <remarks>
        /// One place that knows every field, so a field added later is carried by every <c>With</c> or
        /// by none. <see cref="WithStatistics"/> once built its copy from the constructor's arguments
        /// alone and dropped the facts and the declared keys, which the self-join merge reads (#204).
        /// The fetched statistics are not carried: a copy asks its provider again on first use.
        /// </remarks>
        CosmosContainerMetadata Copy()
        {
            var metadata = new CosmosContainerMetadata(_name, _partitionKeyPaths, _compositeIndexes, _includedPaths, _excludedPaths, _fullTextPaths, _vectorPaths, _readsGeography, uniqueKeys: _uniqueKeys, computedProperties: _computedProperties);
            metadata._statistics = _statistics;
            metadata._statisticsProvider = _statisticsProvider;
            metadata._statisticsTimeToLive = _statisticsTimeToLive;
            metadata._time = _time;
            metadata._partitionKeyDelete = _partitionKeyDelete;
            metadata._facts = _facts;
            metadata._reasoning = _reasoning;
            metadata._declared = _declared;
            metadata._serviceTarget = _serviceTarget;
            return metadata;
        }

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
            if (statistics is null)
                return this;

            var metadata = Copy();
            metadata._statistics = new Lazy<CosmosContainerStatistics?>(() => statistics, LazyThreadSafetyMode.ExecutionAndPublication);

            // A measurement given outright replaces a provider rather than travelling beside it, since
            // Statistics asks a provider first and would never read this one.
            metadata._statisticsProvider = null;
            metadata._statisticsTimeToLive = DefaultStatisticsTimeToLive;
            metadata._time = null;
            return metadata;
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

            var metadata = Copy();

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

            var metadata = Copy();
            metadata._statisticsProvider = provider;
            metadata._statisticsTimeToLive = timeToLive ?? DefaultStatisticsTimeToLive;
            metadata._time = time;
            return metadata;
        }

        // Seeded rather than empty: what the service guarantees about id, _ts and _etag is true of
        // every container and is not delivered through any schema. A model's schema adds to these.
        JsonFactTheory _facts = ServiceFacts;

        // Unbound, so a constant: rules and their encoding, which compile nothing. A schema's reasoning holds the copy
        // bound to its graphs, and no static keyed on this one reaches it.
        static readonly JsonFactTheory ServiceFacts = new(CosmosServiceFacts.Rules);

        // Its schema's reasoning, once a schema or a table binds it; null until then, when its facts compile nothing.
        CosmosReasoning? _reasoning;

        /// <summary>
        /// Gets what is known about the documents this container holds, compiled to rules.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A schema is a source of facts rather than the thing facts are made of.</b> A model file's
        /// JSON Schema is one; what the service guarantees about the properties it maintains itself is
        /// another, and neither is privileged in the rules. They have to share one theory rather than
        /// sit in two, because a rule's body may be satisfied by a fact from the other source and only
        /// a context holding both fires it.
        /// </para>
        /// <para>
        /// <b>Which leaves the trust boundary to be stated rather than typed.</b> Every other member of
        /// this type comes from the container definition or is guaranteed by the service. A fact that
        /// came from a model file does not: it is trusted the way the partition key is trusted, so a
        /// document violating it is a data-integrity problem rather than something defended against per
        /// row — which means a schema wrong by one character drops rows, with no error and a plan that
        /// looks correct. Recorded in <c>DESIGN.md</c> under <em>A declaration is trusted, and that is a change in kind</em> rather than left to be discovered.
        /// </para>
        /// <para>
        /// Empty where nothing is known, which is every container today, and an empty theory proves
        /// nothing and costs nothing to ask.
        /// </para>
        /// <para>
        /// <b>Bound to the schema's reasoning once the schema holds the container.</b> Until then the theory
        /// is rules alone, and compiles nothing; the copy a schema or a table binds reasons in that schema's
        /// graphs, and is another object than the one handed in.
        /// </para>
        /// </remarks>
        public JsonFactTheory Facts => _facts;

        /// <summary>
        /// Gets what the container's schema learns as it plans, or <c>null</c> where no schema has bound
        /// the container yet.
        /// </summary>
        internal CosmosReasoning? Reasoning => _reasoning;

        /// <summary>
        /// Returns the same metadata knowing these facts as well as the ones it already knew.
        /// </summary>
        /// <remarks>
        /// Additive, because facts come from more than one place — a model file's schema, and what the
        /// service guarantees about the properties it maintains itself — and they have to end up in
        /// one theory. Two theories asked separately would lose any derivation whose rule is stated by
        /// one source and whose body is satisfied by the other.
        /// </remarks>
        /// <param name="facts">The rules to add.</param>
        /// <returns>The metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="facts"/> is <c>null</c>.</exception>
        public CosmosContainerMetadata WithFacts(IEnumerable<JsonFactRule> facts)
        {
            if (facts is null)
                throw new ArgumentNullException(nameof(facts));

            var combined = new List<JsonFactRule>(_facts.Rules);
            combined.AddRange(facts);

            if (combined.Count == _facts.Rules.Count)
                return this;

            var metadata = Copy();
            metadata._facts = new JsonFactTheory(combined, _reasoning?.Facts);
            return metadata;
        }

        /// <summary>
        /// Returns the same metadata reasoning in its schema's graphs: bound to the given reasoning where it
        /// is bound to none, and as it is where it already is.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What a schema learns hangs off the schema: the factory makes one reasoning per schema object and
        /// binds every container it reads to it, a host's <see cref="CosmosSchema"/> binds the containers
        /// handed to it unbound to one of its own, and a table over unbound metadata binds its own. A
        /// container that declares nothing more takes the reasoning's bound service facts, so those share
        /// one compiled context and one oracle.
        /// </para>
        /// <para>
        /// <b>The bound copy carries the reasoning wherever it goes.</b> Whoever holds it holds the schema's
        /// graphs, and its fetched statistics are not the original's: a refresh of one reaches only that
        /// one.
        /// </para>
        /// </remarks>
        /// <param name="reasoning">The schema's reasoning.</param>
        /// <returns>The metadata.</returns>
        internal CosmosContainerMetadata WithReasoning(CosmosReasoning reasoning)
        {
            if (reasoning is null)
                throw new ArgumentNullException(nameof(reasoning));

            if (_reasoning is not null)
                return this;

            var metadata = Copy();
            metadata._reasoning = reasoning;
            metadata._facts = ReferenceEquals(_facts, ServiceFacts) ? reasoning.ServiceFacts : _facts.In(reasoning.Facts);
            return metadata;
        }

        CosmosServiceTarget _serviceTarget;

        /// <summary>
        /// Gets the service the container's statements run on (<see cref="CosmosServiceTarget"/>), Azure unless a model or a
        /// host says otherwise; no plan reads it yet.
        /// </summary>
        public CosmosServiceTarget ServiceTarget => _serviceTarget;

        /// <summary>
        /// Returns the same metadata running its statements on the given service.
        /// </summary>
        /// <param name="target">The service.</param>
        /// <returns>The metadata.</returns>
        public CosmosContainerMetadata WithServiceTarget(CosmosServiceTarget target)
        {
            if (target == _serviceTarget)
                return this;

            var metadata = Copy();
            metadata._serviceTarget = target;
            return metadata;
        }

        CosmosConstraintSet? _constraints;
        IReadOnlyList<CosmosConstraint> _declared = Array.Empty<CosmosConstraint>();

        /// <summary>
        /// Gets what is true of this container's documents taken together — the keys a join can be read
        /// through — from every source that knows something.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Derived where it can be, declared where it cannot.</b> The service and the container definition
        /// state keys nobody has to vouch for — <c>id</c> with the partition key, a unique key policy with
        /// the partition key — and <see cref="CosmosConstraintSet.FromContainer"/> derives them from what
        /// was read. A model adds what only the application knows, through <see cref="WithConstraints"/>.
        /// </para>
        /// <para>
        /// A unique key policy can only be set when a container is created, so for an existing container
        /// a declaration is the only route to a key the service does not already state.
        /// </para>
        /// </remarks>
        public CosmosConstraintSet Constraints =>
            _constraints ??= new CosmosConstraintSet(CosmosConstraintSet.FromContainer(_partitionKeyPaths, _uniqueKeys)).With(_declared);

        /// <summary>
        /// Returns the same metadata knowing these constraints as well.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A declared constraint is trusted the way a schema is</b>, and a key is the stronger claim of
        /// the two: a schema says what each document holds and can be checked one document at a time, while
        /// a key says something about every pair of them. A key that is not one makes a join that pairs two
        /// documents read as one document paired with itself, and the rows the second contributed are
        /// dropped — with no error and a plan that looks correct. Nothing checks it, since checking would
        /// mean reading every document.
        /// </para>
        /// </remarks>
        /// <param name="constraints">The constraints, as declared.</param>
        /// <returns>The metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="constraints"/> is <c>null</c>.</exception>
        public CosmosContainerMetadata WithConstraints(IEnumerable<CosmosConstraint> constraints)
        {
            if (constraints is null)
                throw new ArgumentNullException(nameof(constraints));

            var combined = new List<CosmosConstraint>(_declared);
            foreach (var constraint in constraints)
                if (combined.Contains(constraint) == false)
                    combined.Add(constraint);

            if (combined.Count == _declared.Count)
                return this;

            var metadata = Copy();
            metadata._declared = combined;
            return metadata;
        }

        /// <summary>
        /// Determines whether an <c>ORDER BY</c> over the given keys is legal against this container.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a legality test, not a cost estimate, and a separate question from whether the order is one Calcite
        /// allows — that is <see cref="CosmosServiceOrder"/>'s, which also decides the keys handed here: a key the facts
        /// make one value is not among them, nor a key repeating an earlier one. Two conditions, both measured on Azure,
        /// where a statement breaking either is a 400; the emulator accepts both, and its answers say nothing of them:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>Every key's path is indexed</b> — see <see cref="IsPathIndexed"/>. <c>_etag</c> is excluded by default.
        /// A key with no path, a geodesic distance written out as the expression, is not asked.
        /// </description></item>
        /// <item><description>
        /// <b>Two keys or more need a composite index</b> over exactly those paths, in that order, whose directions
        /// match theirs or are their exact reverse — <c>ORDER BY c.a, c.a</c> included. A single key is served by the
        /// range index every indexed path has.
        /// </description></item>
        /// </list>
        /// <para>
        /// Conversion rules must consult this before converting a <c>Sort</c>.
        /// </para>
        /// </remarks>
        /// <param name="keys">The keys written, in order.</param>
        /// <returns><c>true</c> if the sort may be pushed down; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <c>null</c>.</exception>
        public bool IsSortSupported(IReadOnlyList<CosmosSortKey> keys)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            foreach (var key in keys)
                if (string.IsNullOrEmpty(key.Path) == false && IsPathIndexed(key.Path) == false)
                    return false;

            if (keys.Count <= 1)
                return true;

            foreach (var index in _compositeIndexes)
                if (index.Supports(keys))
                    return true;

            return false;
        }

    }

}
