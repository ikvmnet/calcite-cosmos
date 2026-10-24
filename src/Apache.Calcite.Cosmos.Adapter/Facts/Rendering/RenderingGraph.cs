using System;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// S one generation at a time: a theory defined anew from the same definition once the last is spent, as the facts
    /// graph's generations are (<see cref="JsonFactGenerations"/>).
    /// </summary>
    /// <remarks>Why it retires where it does: <c>DESIGN.md</c>, <i>Generations, as F's</i>.</remarks>
    internal sealed class RenderingGraph
    {

        readonly Func<RenderingDefinition> _define;
        readonly Func<GraphUsage, bool> _spent;
        readonly object _starting = new();
        RenderingTheory? _current;

        /// <param name="define">The definition every generation is built from.</param>
        /// <param name="spent">Whether a generation's graph is spent; a test forces it.</param>
        public RenderingGraph(Func<RenderingDefinition> define, Func<GraphUsage, bool>? spent = null)
        {
            _define = define;
            _spent = spent ?? (usage => usage.FractionUsed >= JsonFactGenerations.DefaultUsedLimit || usage.InternedValues >= JsonFactGenerations.DefaultInternedLimit);
        }

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

        /// <summary>The generation a bundle opened now would be a child of, starting one where the last is spent.</summary>
        internal RenderingTheory Current()
        {
            lock (_starting)
            {
                if (_current is null || _current.RanOut || _spent(_current.Graph.Usage))
                    _current = RenderingTheory.Define(_define());

                return _current;
            }
        }

    }

}
