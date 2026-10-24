using System;
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
        public const double SuccessorUsed = 0.4;

        /// <summary>The values interned past which a generation's successor is built: four fifths of F's limit.</summary>
        public const long SuccessorInterned = 800_000;

        readonly Func<RenderingDefinition> _define;
        readonly Func<GraphUsage, bool> _ageing;
        readonly object _starting = new();
        volatile RenderingTheory? _current;
        volatile Exception? _failure;
        Task? _first, _building;
        int _generations;

        /// <param name="define">The definition every generation is built from.</param>
        /// <param name="ageing">Whether a generation's graph is far enough gone to build its successor; a test forces it.</param>
        public RenderingGraph(Func<RenderingDefinition> define, Func<GraphUsage, bool>? ageing = null)
        {
            _define = define;
            _ageing = ageing ?? (usage => usage.FractionUsed >= SuccessorUsed || usage.InternedValues >= SuccessorInterned);
        }

        /// <summary>What a definition threw, which leaves S unavailable for the process: the same rows would throw again.</summary>
        public Exception? Failure => _failure;

        /// <summary>Whether S may answer: no definition has thrown.</summary>
        public bool Available => _failure is null;

        /// <summary>How many generations have been defined.</summary>
        public int Generations => Volatile.Read(ref _generations);

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
                Start(null);
        }

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
        /// <paramref name="wait"/>; <c>false</c> past it, or where a definition threw.
        /// </summary>
        internal bool TryCurrent(TimeSpan wait, [NotNullWhen(true)] out RenderingTheory? theory)
        {
            theory = _current;
            if (_failure is not null)
                return false;

            if (theory is { RanOut: false })
            {
                if (_ageing(theory.Graph.Usage))
                    Start(theory);

                return true;
            }

            theory = Start(theory).Wait(wait) ? _current : null;
            return theory is { RanOut: false };
        }

        /// <summary>The build that replaces <paramref name="from"/>, started where none runs; none where another has replaced it, or a definition threw.</summary>
        Task Start(RenderingTheory? from)
        {
            lock (_starting)
            {
                if (_failure is not null || _current != from)
                    return Task.CompletedTask;

                if (_building is { IsCompleted: false })
                    return _building;

                _building = Task.Factory.StartNew(Build, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Volatile.Write(ref _first, _first ?? _building);
                return _building;
            }
        }

        void Build()
        {
            try
            {
                var theory = RenderingTheory.Define(_define());
                RenderingAnswerMemo.DigestOf(theory);
                Exercise(theory);
                _current = theory;
                Interlocked.Increment(ref _generations);
            }
            catch (Exception e)
            {
                _failure = e;
            }
        }

        /// <summary>One question through generate mode, so that a plan never pays the first one a generation's rules are run for.</summary>
        static void Exercise(RenderingTheory theory)
        {
            var label = JsonDocumentPath.Root.Property("label");
            using var bundle = theory.Open(RenderingSet.Production);
            var root = bundle.Write(new SqlPredicate.Compare("<>", new SqlValue.Text(label), SqlValue.Of("bikes")));
            bundle.Import(new ImportSource("warm", "warm", 0), label, PathValues.Anything, JsonValues.AllBands);
            bundle.Generate(root);
            bundle.RunGenerate(1);
            bundle.ReadBest(root);
        }

    }

}
