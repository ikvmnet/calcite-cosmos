using System;
using System.Threading;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// S one generation at a time: a theory defined anew from the same definition once the last is spent, as the facts
    /// graph's generations are (<see cref="JsonFactGenerations"/>).
    /// </summary>
    /// <remarks>
    /// A graph's ids and epochs are finite and its pools never shrink (Arch §2.12). A generation retires before it hands
    /// out another bundle once it has used half of either, which leaves headroom for the bundles open on it, or interned
    /// the facts graph's limit of values; and at once where a bundle found it out of either. Nothing disposes it: the
    /// bundles in flight finish on it, and it is collected with the last.
    /// </remarks>
    internal sealed class RenderingGraph
    {

        readonly Func<RenderingDefinition> _define;
        readonly Func<GraphUsage, bool> _spent;
        readonly object _starting = new();
        RenderingTheory? _current;
        int _started;

        /// <param name="define">The definition every generation is built from.</param>
        /// <param name="spent">Whether a generation's graph is spent; a test forces it.</param>
        public RenderingGraph(Func<RenderingDefinition> define, Func<GraphUsage, bool>? spent = null)
        {
            _define = define;
            _spent = spent ?? (usage => usage.FractionUsed >= 0.5 || usage.InternedValues >= JsonFactGenerations.DefaultInternedLimit);
        }

        /// <summary>Gets how many generations have been started.</summary>
        public int Started => Volatile.Read(ref _started);

        /// <summary>Opens a bundle under a set on the current generation, starting one where that is spent.</summary>
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

        RenderingTheory Current()
        {
            lock (_starting)
            {
                if (_current is null || _current.RanOut || _spent(_current.Graph.Usage))
                {
                    _current = RenderingTheory.Define(_define());
                    Interlocked.Increment(ref _started);
                }

                return _current;
            }
        }

    }

}
