using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    using NodeReadings = ConditionalWeakTable<Scope, ConcurrentDictionary<(PredicateRules Rules, JsonDocumentPath Path, bool Chars), (PathValues? Values, ValueSet<string>? Chars, string Spelled)>>;

    /// <summary>
    /// S as production asks it: one generate bundle a question, of one statement round's roots, imported from the node the
    /// round renders from with later rounds' atoms stated beneath their strata; remembered by its content across statements,
    /// plans and generations, and declined, never thrown, where S cannot answer. Every filter a plan pushes asks it.
    /// </summary>
    /// <remarks>
    /// It decides whether an answer may be read and kept, never what it is: <c>DESIGN.md</c>, <i>The production oracle</i>.
    /// Production's are each schema's (<c>CosmosReasoning.Rendering</c>), and what one learns — its graph's generations,
    /// the answers and the readings — is that schema's alone.
    /// </remarks>
    internal sealed class RenderingOracle
    {

        /// <summary>
        /// Why a question declined: no generation answered within the wait, or a definition threw; the engine failed the
        /// bundle, or ran out of ids or epochs; or a rule merged two terms or the run agreed and refuted one claim.
        /// </summary>
        public const string Unavailable = "unavailable", Engine = "engine", Conflict = "conflict";

        /// <summary>How long a question waits for a generation where none answers: the first's build, or a spent one's successor.</summary>
        public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

        readonly RenderingGraph _graph;
        readonly TimeSpan _wait;
        readonly Func<int, int> _limit;
        readonly Func<RenderingQuestionKey, RenderingQuestionKey> _keyOf;
        long _hits, _bundles, _declines;

        /// <param name="graph">The S it asks.</param>
        /// <param name="remembered">How many bundles' answers it remembers.</param>
        /// <param name="wait">How long a question waits for a generation where none answers.</param>
        /// <param name="limit">A run's iterations for its width; a test's stops a run short.</param>
        /// <param name="keyOf">How a key is filed; a plant's leaves out a value the import reads (G9), none in production.</param>
        public RenderingOracle(RenderingGraph graph, int remembered = 4096, TimeSpan? wait = null, Func<int, int>? limit = null, Func<RenderingQuestionKey, RenderingQuestionKey>? keyOf = null)
        {
            _graph = graph;
            Memo = new RenderingAnswerMemo(remembered);
            _wait = wait ?? DefaultWait;
            _limit = limit ?? RenderingBundle.GenerateLimit;
            _keyOf = keyOf ?? (key => key);
        }

        /// <summary>The S it asks.</summary>
        internal RenderingGraph Graph => _graph;

        /// <summary>Whether S may answer: no definition has thrown.</summary>
        public bool Available => _graph.Available;

        /// <summary>The first generation's build: defined, its rows hashed and one question asked.</summary>
        public Task Ready => _graph.Ready;

        /// <summary>The current generation's use of its graph; none before the first is built.</summary>
        public GraphUsage? Usage => _graph.Usage;

        /// <summary>The current generation's rows hashed, what every key names its theory by; none before the first is built.</summary>
        public string? Digest => _graph.Digest;

        /// <summary>The answers remembered.</summary>
        public RenderingAnswerMemo Memo { get; }

        /// <summary>
        /// What each frozen node was read to hold of a path, spelled for a key (spec §6.1's lever): keyed weakly by the
        /// node, so a node collected takes its own along, and kept no longer than the oracle.
        /// </summary>
        internal NodeReadings Readings { get; } = new();

        /// <summary>
        /// What each statement's terms were answered, by set, the theory it answered under and the question: Volcano reads one
        /// condition as many nodes, each a statement of the same terms whose rounds ask the same questions of the same frozen
        /// nodes, so a question asked before is handed its answer without its key built. A node names its import within its
        /// terms, so the question's conjuncts and junctions by digest, their strata's nodes and the opaque readings are all the
        /// key's import reads. Keyed weakly by the terms, which F's memory bounds, and kept no longer than the oracle.
        /// </summary>
        internal ConditionalWeakTable<StatementTerms, ConcurrentDictionary<(RenderingSet Set, string Theory, string Question), RenderingBundleAnswer>> Answered { get; } = new();

        /// <summary>Questions the memo answered, bundles opened, and questions declined, since the oracle was made.</summary>
        public (long Hits, long Bundles, long Declines) Counts => (Interlocked.Read(ref _hits), Interlocked.Read(ref _bundles), Interlocked.Read(ref _declines));

        /// <summary>Starts the first generation's build on its dedicated thread, once, and returns at once.</summary>
        /// <remarks>Every table's <c>toRel</c> calls it: a schema's first plan starts its build.</remarks>
        public void Warm() => _graph.Warm();

        /// <summary>
        /// Generate mode's answer of each root, from one bundle: imported from <paramref name="root"/>, each atom
        /// <paramref name="beneath"/> names stated beneath its stratum, run, and read; the remembered answer where one is
        /// filed under the question's key, whether a generation answers or not. A decline is returned and counted, never
        /// remembered, and nothing throws: a node its owner let go, and so disposed, declines too, so the caller keeps the
        /// node's owner reachable (<see cref="CosmosStatementScope.ContextFor"/>).
        /// </summary>
        /// <param name="set">The set the statement is planned under.</param>
        /// <param name="roots">The pending conjuncts and the round's junctions.</param>
        /// <param name="opaques">The value each opaque predicate of the roots was read as (<see cref="PredicateStatement.Opaques"/>).</param>
        /// <param name="source">The statement, licence and round the import is the root of.</param>
        /// <param name="root">The node the round renders from, frozen.</param>
        /// <param name="rules">The rules the node ran; <c>null</c> where the statement knows nothing.</param>
        /// <param name="beneath">Each atom read where its conjunct was rendered: that stratum's source, node and rules.</param>
        /// <param name="opened">
        /// Told of the bundle a miss opened, once its roots are read and before it goes, with each root's term, the import, the
        /// run, and the time from opening it to its roots' answers: what the shadow judges and the cost instrument counts,
        /// read off the one bundle that answered.
        /// </param>
        public RenderingBundleAnswer Ask(RenderingSet set, IReadOnlyList<SqlPredicate> roots, IReadOnlyDictionary<long, SqlValue>? opaques, ImportSource source,
            Scope? root, PredicateRules? rules, IReadOnlyList<(SqlPredicate Atom, ImportSource Source, Scope? Node, PredicateRules? Rules)>? beneath = null,
            Action<RenderingBundle, IReadOnlyList<Pred>, ImportReport, RenderingRun, TimeSpan>? opened = null)
        {
            var started = Stopwatch.GetTimestamp();
            var paths = roots.SelectMany(r => r.Paths).ToList();
            RenderingBundleAnswer? answer = null;
            var hit = false;
            ImportReport? import = null;
            try
            {
                // The import is spelled from the node alone: the declaration's mentions only count its defaults.
                import = RenderingImporter.Import(null, source, root, rules, null, paths, beneath, Readings);
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                answer = Declined(roots, Engine, e.GetType().Name + ": " + e.Message);
            }

            // A remembered answer needs no generation: every generation defined alike hashes alike, so a hit is served while
            // none answers, the first built or a successor building.
            if (import is not null && _graph.Digest is { } digest)
                answer = Memo.GetOrAdd(_keyOf(RenderingQuestionKey.Of(roots, opaques, import, set, digest)), null, out hit);

            if (import is not null && answer is null)
            {
                if (_graph.TryCurrent(_wait, out var theory) == false)
                    answer = Declined(roots, Unavailable, _graph.Failure?.Message ?? "no generation within " + _wait);
                else
                {
                    var key = _keyOf(RenderingQuestionKey.Of(roots, opaques, import, set, RenderingAnswerMemo.DigestOf(theory)));
                    answer = Memo.GetOrAdd(key, () => Generated(key, theory, set, roots, opaques, source, root, rules, paths, beneath, opened), out hit)!;
                }
            }

            // A waiter handed an answer another asker's bundle declined counts the decline alone.
            hit &= answer!.Declined is null;
            if (hit)
                Interlocked.Increment(ref _hits);
            else if (answer.Cost.Bundles > 0)
                Interlocked.Increment(ref _bundles);

            if (answer.Declined is not null)
                Interlocked.Increment(ref _declines);

            return answer with { Cost = (hit ? RenderingCost.None with { Hits = 1 } : answer.Cost) with { Elapsed = Stopwatch.GetElapsedTime(started) } };
        }

        /// <summary>
        /// One bundle's answers: declined on an engine failure, from opening the bundle to reading it, and a conflict, and
        /// running out of ids or epochs retires the generation.
        /// </summary>
        RenderingBundleAnswer Generated(RenderingQuestionKey key, RenderingTheory theory, RenderingSet set, IReadOnlyList<SqlPredicate> roots, IReadOnlyDictionary<long, SqlValue>? opaques,
            ImportSource source, Scope? root, PredicateRules? rules, List<JsonDocumentPath> paths, IReadOnlyList<(SqlPredicate, ImportSource, Scope?, PredicateRules?)>? beneath,
            Action<RenderingBundle, IReadOnlyList<Pred>, ImportReport, RenderingRun, TimeSpan>? opened)
        {
            var started = Stopwatch.GetTimestamp();
            RenderingBundleAnswer Failed(Exception e)
            {
                if (e is OverflowException)
                    theory.RanOut = true;

                return Declined(roots, Engine, e.GetType().Name + ": " + e.Message) with { Key = key, Cost = new(0, 1, 1, 0, TimeSpan.Zero) };
            }

            try
            {
                using var bundle = theory.Open(set);
                var asked = roots.DistinctBy(r => r.Digest).ToList();
                var terms = asked.Select(r => bundle.Write(r, opaques)).ToList();
                var import = RenderingImporter.Import(bundle, source, root, rules, null, paths, beneath, Readings);
                foreach (var term in terms)
                    bundle.Generate(term);

                var run = bundle.RunGenerateWithin(_limit(RenderingBundle.WidthOf(asked)));
                var answers = asked.Select((r, i) => (r.Digest, Answer: bundle.ReadBest(terms[i]))).ToDictionary(a => a.Digest, a => a.Answer);
                if (bundle.Failure is { } failure)
                    return Failed(failure);

                opened?.Invoke(bundle, terms, import, run, Stopwatch.GetElapsedTime(started));

                var declined = answers.Values.Any(a => a.Kind == RenderingVerdictKind.Conflict) ? Conflict : null;
                return new(answers, declined, key, new(0, 1, declined is null ? 0 : 1, bundle.Iterations, TimeSpan.Zero));
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                return Failed(e);
            }
        }

        static RenderingBundleAnswer Declined(IReadOnlyList<SqlPredicate> roots, string reason, string why) => new(roots.DistinctBy(r => r.Digest)
            .ToDictionary(r => r.Digest, _ => new RenderingAnswer(RenderingVerdictKind.Unproven, null, null, null, "declined: " + reason + ": " + why)), reason, null, RenderingCost.None with { Declines = 1 });

        /// <summary>
        /// What one bundle answered of each root, by its digest; why it declined, where it did, in which case no answer is
        /// read as S's; the key it is filed under, none where S was unavailable; and what the question cost, a remembered
        /// answer's its lookup alone.
        /// </summary>
        internal sealed record RenderingBundleAnswer(IReadOnlyDictionary<string, RenderingAnswer> Roots, string? Declined, RenderingQuestionKey? Key, RenderingCost Cost)
        {

            /// <summary>The answer of one root.</summary>
            public RenderingAnswer Of(SqlPredicate root) => Roots[root.Digest];

        }

    }

}
