using System;
using System.Collections.Generic;
using System.Threading;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The cost instrument (spec §6.4): what each question a flow asks S costs, summed where a test measures the flow, which
    /// production never does. Read past one <c>volatile bool</c>, and only where S is asked.
    /// </summary>
    internal static class CosmosRenderingCost
    {

        static readonly AsyncLocal<List<RenderingCostSample>?> Flow = new();
        static readonly AsyncLocal<Func<TimeSpan>?> Clock = new();

        /// <summary>Whether any flow has armed the instrument; once one has, a question also reads the flow's.</summary>
        internal static volatile bool Armed;

        /// <summary>Whether this flow is measured.</summary>
        internal static bool Measuring => Armed && Flow.Value is not null;

        /// <summary>
        /// Records every question the flow asks S until disposed, into the list handed back, in order; and, where a test
        /// hands it the thread's own processor clock, the processor time each question took, which a host's contention
        /// stretches far less than its wall time.
        /// </summary>
        internal static IDisposable Measure(out IReadOnlyList<RenderingCostSample> samples, Func<TimeSpan>? thread = null)
        {
            var arming = new Restore(Flow.Value, Clock.Value);
            var list = new List<RenderingCostSample>();
            (Flow.Value, Clock.Value, samples, Armed) = (list, thread, list, true);
            return arming;
        }

        /// <summary>The thread's processor clock the flow's measure was handed; none where it was handed none.</summary>
        internal static Func<TimeSpan>? ThreadClock => Clock.Value;

        /// <summary>Adds a question's cost to the flow's, where it is measured.</summary>
        internal static void Add(RenderingCostSample sample)
        {
            if (Flow.Value is { } list)
                lock (list)
                    list.Add(sample);
        }

        /// <summary>Puts the flow's list and clock back as <see cref="Measure"/> found them.</summary>
        sealed class Restore(List<RenderingCostSample>? was, Func<TimeSpan>? clock) : IDisposable
        {

            public void Dispose() => (Flow.Value, Clock.Value) = (was, clock);

        }

    }

}
