using System;
using System.Threading;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// S at production's four sites — admission (A), the weakening (W), implementation (P) and the residual projection (R):
    /// writing by default since the switch (g16), so that S's answers are taken for the conjuncts S owns and for every
    /// residual's weakening. A test may arm a flow beside the translator instead (<see cref="Arming.Writes"/> off), where
    /// the sites ask S through <see cref="CosmosRendering"/> beside it and tell the flow's observer: the shadow, which
    /// g17 retires with this arm (<c>DESIGN.md</c>, <i>The shadow in production code</i>, <i>The switch</i>).
    /// </summary>
    internal static class CosmosRenderingShadow
    {

        static readonly AsyncLocal<Arming?> Flow = new();

        /// <summary>
        /// The arm every flow without its own takes: writing, under each container's own set — the switch's one default —
        /// or under the set a whole-suite run names before anything plans.
        /// </summary>
        static volatile Arming? _everywhere = new(null, true, null);

        /// <summary>Whether a site reads the flow's arm: always, since the process writes; kept until g17 retires the arm.</summary>
        internal static volatile bool Armed = true;

        /// <summary>The flow's arm, its own or the process's; read past <see cref="Armed"/> alone.</summary>
        internal static Arming? Current => Flow.Value ?? _everywhere;

        /// <summary>
        /// The flow's arm where it asks S at all: writing, or beside the translator for an observer; none where a test arms
        /// neither, which turns S off and leaves the translator alone (line 29's pairing).
        /// </summary>
        internal static Arming? Asking => Armed && Current is { } arm && (arm.Writes || arm.Observer is not null) ? arm : null;

        /// <summary>
        /// Asks S at every site the flow plans through until disposed, telling an observer if one is given; writing as the
        /// process does unless told, and under the set given or each container's own.
        /// </summary>
        internal static IDisposable Arm(ICosmosRenderingShadowObserver? observer, bool? writes = null, RenderingSet? set = null)
        {
            var arming = new Restore(Flow.Value);
            Flow.Value = new Arming(observer, writes ?? _everywhere?.Writes ?? false, set ?? _everywhere?.Set);
            Armed = true;
            return arming;
        }

        /// <summary>Arms every flow of the process to write under a set, or each container's own: a whole-suite run's, set once, before anything plans.</summary>
        internal static void ArmEverywhere(RenderingSet? set)
        {
            _everywhere = new Arming(null, true, set);
            Armed = true;
        }

        /// <summary>
        /// A flow's arm: who is told, whether the sites take S's answers, and the set every statement is proven under,
        /// or each container's own (<see cref="CosmosRendering.SetFor"/>) where none.
        /// </summary>
        internal sealed record Arming(ICosmosRenderingShadowObserver? Observer, bool Writes, RenderingSet? Set);

        /// <summary>Puts the flow's arm back as <see cref="Arm"/> found it.</summary>
        sealed class Restore(Arming? was) : IDisposable
        {

            public void Dispose() => Flow.Value = was;

        }

    }

}
