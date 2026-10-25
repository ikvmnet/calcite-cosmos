using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// S one generation at a time, each defined from the same definition on a dedicated thread, never one that plans: its
    /// rows hashed and one question asked before it answers. A successor is built once the current has used two fifths of
    /// its ids or epochs, or interned 800,000 values, and swapped in when ready; the current answers until then, past F's
    /// gauge too, which stands well short of the engine's own limits.
    /// </summary>
    /// <remarks>
    /// Why it retires where it does, and what a definition that throws leaves: <c>DESIGN.md</c>, <i>Generations, as F's</i>
    /// and <i>The production oracle</i>.
    /// </remarks>
    internal sealed class RenderingGraph
    {

        /// <summary>The share of its ids or epochs past which a generation's successor is built: four fifths of F's gauge.</summary>
        public const double SuccessorUsed = 0.8 * JsonFactGenerations.DefaultUsedLimit;

        /// <summary>The values interned past which a generation's successor is built: four fifths of F's limit.</summary>
        public const long SuccessorInterned = JsonFactGenerations.DefaultInternedLimit * 4 / 5;

        /// <summary>How long after a successor's build failed the next is started.</summary>
        public static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(10);

        readonly Func<RenderingDefinition> _define;
        readonly Func<GraphUsage, bool> _ageing;
        readonly TimeSpan _retry;
        readonly Action<SqlPredicate, RenderingAnswer>? _exercised;
        readonly object _starting = new();
        volatile RenderingTheory? _current;
        volatile Exception? _failure;
        volatile string? _digest;
        Task? _first, _building;
        long _waitedSince, _retryAt;
        int _generations, _successorFailures;

        /// <param name="define">The definition every generation is built from.</param>
        /// <param name="ageing">Whether a generation's graph is far enough gone to build its successor; a test forces it.</param>
        /// <param name="retry">How long after a successor's build failed the next is started.</param>
        /// <param name="exercised">
        /// Told the warm-up question and its answer on the build's thread, which production prints so that its printer is
        /// warm too; what it throws fails the generation, as the question's own failure does.
        /// </param>
        public RenderingGraph(Func<RenderingDefinition> define, Func<GraphUsage, bool>? ageing = null, TimeSpan? retry = null, Action<SqlPredicate, RenderingAnswer>? exercised = null)
        {
            _define = define;
            _ageing = ageing ?? (usage => usage.FractionUsed >= SuccessorUsed || usage.InternedValues >= SuccessorInterned);
            _retry = retry ?? DefaultRetry;
            _exercised = exercised;
        }

        /// <summary>
        /// What the first generation's build threw, which leaves this graph's S unavailable for its life; a definition that
        /// throws throws for every graph alike, its rows being the assembly's.
        /// </summary>
        public Exception? Failure => _failure;

        /// <summary>Whether S may answer: the first definition did not throw.</summary>
        public bool Available => _failure is null;

        /// <summary>How many generations have been defined.</summary>
        public int Generations => Volatile.Read(ref _generations);

        /// <summary>How many successors' builds failed while the current answered on.</summary>
        public int SuccessorFailures => Volatile.Read(ref _successorFailures);

        /// <summary>The current generation's rows hashed, which every generation defined alike shares; none before the first.</summary>
        public string? Digest => _digest;

        /// <summary>Whether the first generation's build was started: warmed, awaited or asked.</summary>
        internal bool Started => Volatile.Read(ref _first) is not null;

        /// <summary>The first generation's build, started where it was not: done once a generation answers or the definition threw.</summary>
        public Task Ready
        {
            get
            {
                Warm();
                return Volatile.Read(ref _first)!;
            }
        }

        /// <summary>Starts the first generation's build, once; it returns at once.</summary>
        public void Warm()
        {
            if (Volatile.Read(ref _first) is null)
                Start(null, waiting: false);
        }

        /// <summary>The current generation's use of its graph, read without a lock; none before the first is built.</summary>
        public GraphUsage? Usage => _current?.Graph.Usage;

        /// <summary>Opens a bundle under a set on the current generation, waiting for one where none answers.</summary>
        /// <exception cref="InvalidOperationException">A definition threw.</exception>
        public RenderingBundle Open(RenderingSet set)
        {
            var theory = Current();
            try
            {
                return theory.Open(set);
            }
            catch (OverflowException)
            {
                // Out of ids or epochs before the bundle held a scope: the next generation has both.
                theory.RanOut = true;
                return Current().Open(set);
            }
        }

        /// <summary>The generation a bundle opened now would be a child of, waiting as long as it takes for one.</summary>
        /// <exception cref="InvalidOperationException">A definition threw.</exception>
        internal RenderingTheory Current() => TryCurrent(Timeout.InfiniteTimeSpan, out var theory) ? theory : throw new InvalidOperationException("S is unavailable.", _failure);

        /// <summary>
        /// The generation a bundle opened now would be a child of, read without a lock, and its successor started where it
        /// is ageing. Where none answers — none built yet, or the current ran out — waits for the build at most
        /// <paramref name="wait"/> from when a question first waited on it, so that a plan of many questions waits once;
        /// <c>false</c> past it, or where the first definition threw.
        /// </summary>
        internal bool TryCurrent(TimeSpan wait, [NotNullWhen(true)] out RenderingTheory? theory)
        {
            theory = _current;
            if (_failure is not null)
                return false;

            if (theory is { RanOut: false })
            {
                if (_ageing(theory.Graph.Usage))
                    Start(theory, waiting: false);

                return true;
            }

            var (build, since) = Start(theory, waiting: true);
            var left = wait == Timeout.InfiniteTimeSpan ? wait : wait - Stopwatch.GetElapsedTime(since);
            theory = build.Wait(left == Timeout.InfiniteTimeSpan || left > TimeSpan.Zero ? left : TimeSpan.Zero) ? _current : null;
            return theory is { RanOut: false };
        }

        /// <summary>
        /// The build that replaces <paramref name="from"/>, started where none runs, and when a question first waited on it;
        /// none where another has replaced it, the first definition threw, or a successor failed within the retry.
        /// </summary>
        (Task Build, long Since) Start(RenderingTheory? from, bool waiting)
        {
            lock (_starting)
            {
                if (_failure is not null || _current != from)
                    return (Task.CompletedTask, 0);

                if (_building is not { IsCompleted: false })
                {
                    if (from is not null && Stopwatch.GetTimestamp() < _retryAt)
                        return (Task.CompletedTask, 0);

                    // Defined under no caller's context, so that a planning thread's culture and async locals stay its own.
                    var flow = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
                    try
                    {
                        _building = Task.Factory.StartNew(Build, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    }
                    finally
                    {
                        flow?.Undo();
                    }

                    _waitedSince = 0;
                    Volatile.Write(ref _first, _first ?? _building);
                }

                if (waiting && _waitedSince == 0)
                    _waitedSince = Stopwatch.GetTimestamp();

                return (_building, _waitedSince);
            }
        }

        void Build()
        {
            try
            {
                var theory = RenderingTheory.Define(_define());
                var digest = RenderingAnswerMemo.DigestOf(theory);
                Exercise(theory);

                // Published under the lock builds start under, so that an asker holding the generation it replaces starts none.
                lock (_starting)
                    (_current, _digest) = (theory, digest);

                Interlocked.Increment(ref _generations);
            }
            catch (Exception e)
            {
                // A successor is the definition that built once, so its failure is no row's: the current answers on, and the
                // next is started after the retry.
                lock (_starting)
                    if (_current is null)
                        _failure = e;
                    else
                        (_successorFailures, _retryAt) = (_successorFailures + 1, Stopwatch.GetTimestamp() + (long)(_retry.TotalSeconds * Stopwatch.Frequency));
            }
        }

        /// <summary>
        /// One question through generate mode, so that a plan never pays the first one a generation's rules are run for; a
        /// definition whose generate mode fails or conflicts on it is no generation.
        /// </summary>
        void Exercise(RenderingTheory theory)
        {
            var label = JsonDocumentPath.Root.Property("label");
            var question = new SqlPredicate.Compare("<>", new SqlValue.Text(label), SqlValue.Of("bikes"));
            using var bundle = theory.Open(RenderingSet.Production);
            var root = bundle.Write(question);
            bundle.Import(new ImportSource("warm", "warm", 0), label, PathValues.Anything, JsonValues.AllBands);
            bundle.Generate(root);
            bundle.RunGenerate(1);
            if (bundle.ReadBest(root) is var answer && (bundle.Failure is not null || answer.Kind == RenderingVerdictKind.Conflict))
                throw new InvalidOperationException("S's generate mode failed its warm-up question: " + answer.Reason, bundle.Failure);

            _exercised?.Invoke(question, answer);
        }

    }

}
