using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// The reasonings one harness plans in, each a schema of its own as a host's data source makes one (spec §6.3): each
    /// made here with its S build awaited, so that no first plan's question waits past its bound and declines; their
    /// counts read across all of them; and, while the harness plans (<see cref="Planning"/>), every table it builds over
    /// a fixture held to one of them (<see cref="TestReasoning.Checked"/>), so that a fixture that made a reasoning of its
    /// own, or was bound to another, fails its plan instead of asking S where no count reads it.
    /// </summary>
    internal sealed class HarnessReasonings
    {

        readonly List<CosmosReasoning> _made = [];

        /// <summary>
        /// Makes a reasoning and awaits its S build, as a harness does before its first plan.
        /// </summary>
        /// <returns>The reasoning.</returns>
        public CosmosReasoning Make()
        {
            var reasoning = new CosmosReasoning();
            reasoning.Rendering.Ready.Wait();
            lock (_made)
                _made.Add(reasoning);

            return reasoning;
        }

        /// <summary>
        /// Whether the harness made the given reasoning.
        /// </summary>
        /// <param name="reasoning">The reasoning, or <c>null</c>.</param>
        public bool Made(CosmosReasoning? reasoning)
        {
            lock (_made)
                return _made.Any(r => ReferenceEquals(r, reasoning));
        }

        /// <summary>
        /// Gets the questions remembered, bundles opened and questions declined, summed over every reasoning the harness made.
        /// </summary>
        public (long Hits, long Bundles, long Declines) Counts
        {
            get
            {
                lock (_made)
                    return _made.Select(r => r.Rendering.Counts).Aggregate((0L, 0L, 0L), (a, c) => (a.Item1 + c.Hits, a.Item2 + c.Bundles, a.Item3 + c.Declines));
            }
        }

        /// <summary>
        /// Holds every table a harness builds over a fixture in this flow to the harness's reasonings, until disposed.
        /// </summary>
        public System.IDisposable Planning() => TestReasoning.Within(this);

    }

}
