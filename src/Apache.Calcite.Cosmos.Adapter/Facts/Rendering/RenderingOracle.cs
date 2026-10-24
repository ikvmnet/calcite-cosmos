using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// S as production asks it: one generate bundle a question, of one statement round's roots, imported from the node the
    /// round renders from with later rounds' atoms stated beneath their strata; remembered by its content across statements,
    /// plans and generations, and declined, never thrown, where S cannot answer. Nothing in planning asks it yet.
    /// </summary>
    /// <remarks>
    /// It decides whether an answer may be read and kept, never what it is: <c>DESIGN.md</c>, <i>The production oracle</i>.
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
        long _hits, _bundles, _declines;

        /// <param name="graph">The S it asks.</param>
        /// <param name="remembered">How many bundles' answers it remembers.</param>
        /// <param name="wait">How long a question waits for a generation where none answers.</param>
        /// <param name="limit">A run's iterations for its width; a test's stops a run short.</param>
        public RenderingOracle(RenderingGraph graph, int remembered = 4096, TimeSpan? wait = null, Func<int, int>? limit = null)
        {
            _graph = graph;
            Memo = new RenderingAnswerMemo(remembered);
            _wait = wait ?? DefaultWait;
            _limit = limit ?? RenderingBundle.GenerateLimit;
        }

        /// <summary>Whether S may answer: no definition has thrown.</summary>
        public bool Available => _graph.Available;

        /// <summary>The first generation's build: defined, its rows hashed and one question asked.</summary>
        public Task Ready => _graph.Ready;

        /// <summary>The answers remembered.</summary>
        public RenderingAnswerMemo Memo { get; }

        /// <summary>Questions the memo answered, bundles opened, and questions declined, since the oracle was made.</summary>
        public (long Hits, long Bundles, long Declines) Counts => (Interlocked.Read(ref _hits), Interlocked.Read(ref _bundles), Interlocked.Read(ref _declines));

        /// <summary>Starts the first generation's build on its dedicated thread, once, and returns at once.</summary>
        public void Warm() => _graph.Warm();

        /// <summary>
        /// Generate mode's answer of each root, from one bundle: imported from <paramref name="root"/>, each atom
        /// <paramref name="beneath"/> names stated beneath its stratum, run, and read; the remembered answer where one is
        /// filed under the question's key. A decline is returned and counted, and never remembered.
        /// </summary>
        /// <param name="set">The set the statement is planned under.</param>
        /// <param name="roots">The pending conjuncts and the round's junctions.</param>
        /// <param name="opaques">The value each opaque predicate of the roots was read as (<see cref="PredicateStatement.Opaques"/>).</param>
        /// <param name="source">The statement, licence and round the import is the root of.</param>
        /// <param name="root">The node the round renders from, frozen.</param>
        /// <param name="rules">The rules the node ran; <c>null</c> where the statement knows nothing.</param>
        /// <param name="beneath">Each atom read where its conjunct was rendered: that stratum's source, node and rules.</param>
        public RenderingBundleAnswer Ask(RenderingSet set, IReadOnlyList<SqlPredicate> roots, IReadOnlyDictionary<long, SqlValue>? opaques, ImportSource source,
            Scope? root, PredicateRules? rules, IReadOnlyList<(SqlPredicate Atom, ImportSource Source, Scope? Node, PredicateRules? Rules)>? beneath = null)
        {
            var (started, allocated) = (Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
            RenderingBundleAnswer answer;
            var hit = false;
            if (_graph.TryCurrent(_wait, out var theory) == false)
                answer = Declined(roots, Unavailable, _graph.Failure?.Message ?? "no generation within " + _wait);
            else
            {
                // The import is spelled from the node alone: the declaration's mentions only count its defaults.
                var paths = roots.SelectMany(r => r.Paths).ToList();
                var key = RenderingQuestionKey.Of(roots, opaques, RenderingImporter.Import(null, source, root, rules, null, paths, beneath), set, RenderingAnswerMemo.DigestOf(theory));
                answer = Memo.GetOrAdd(key, () => Generated(key, theory, set, roots, opaques, source, root, rules, paths, beneath), out hit);
            }

            if (hit)
                Interlocked.Increment(ref _hits);
            else if (answer.Cost.Bundles > 0)
                Interlocked.Increment(ref _bundles);

            if (answer.Declined is not null)
                Interlocked.Increment(ref _declines);

            return answer with
            {
                Cost = (hit ? RenderingCost.None with { Hits = 1, Declines = answer.Cost.Declines } : answer.Cost) with
                {
                    Elapsed = Stopwatch.GetElapsedTime(started), Allocated = GC.GetAllocatedBytesForCurrentThread() - allocated,
                },
            };
        }

        /// <summary>One bundle's answers; declined on an engine failure, and running out of ids or epochs retires the generation.</summary>
        RenderingBundleAnswer Generated(RenderingQuestionKey key, RenderingTheory theory, RenderingSet set, IReadOnlyList<SqlPredicate> roots, IReadOnlyDictionary<long, SqlValue>? opaques,
            ImportSource source, Scope? root, PredicateRules? rules, List<JsonDocumentPath> paths, IReadOnlyList<(SqlPredicate, ImportSource, Scope?, PredicateRules?)>? beneath)
        {
            var interned = theory.Graph.Usage.InternedValues;
            RenderingBundle bundle;
            try
            {
                bundle = theory.Open(set);
            }
            catch (OverflowException e)
            {
                theory.RanOut = true;
                return Declined(roots, Engine, e.Message) with { Key = key };
            }

            using (bundle)
            {
                var asked = roots.DistinctBy(r => r.Digest).ToList();
                var terms = asked.Select(r => bundle.Write(r, opaques)).ToList();
                var candidates = 0;
                try
                {
                    RenderingImporter.Import(bundle, source, root, rules, null, paths, beneath);
                    foreach (var term in terms)
                        bundle.Generate(term);

                    bundle.RunGenerateWithin(_limit(RenderingBundle.WidthOf(asked)));
                    candidates = bundle.Failure is null ? (int)bundle.Scope.CountRows(theory.Candidate) : 0;
                }
                catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
                {
                    bundle.Decline(e);
                }

                var answers = asked.Select((r, i) => (r.Digest, Answer: bundle.ReadBest(terms[i]))).ToDictionary(a => a.Digest, a => a.Answer);
                var declined = bundle.Failure is not null ? Engine : answers.Values.Any(a => a.Kind == RenderingVerdictKind.Conflict) ? Conflict : null;
                var usage = theory.Graph.Usage;
                return new(answers, declined, key, new(0, 1, declined is null ? 0 : 1, bundle.Iterations, candidates, TimeSpan.Zero, 0, usage.InternedValues - interned, usage.FractionUsed));
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
