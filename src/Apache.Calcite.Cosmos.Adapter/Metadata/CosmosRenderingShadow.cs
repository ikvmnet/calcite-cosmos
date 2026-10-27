using System;
using System.Threading;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// S beside the translator in planning (spec g15): inert unless a test arms it, which production never does. Armed, the
    /// four sites — admission (A), the weakening (W), implementation (P) and the residual projection (R) — ask S through
    /// <see cref="CosmosRendering"/> beside the translator and tell the flow's observer; with <see cref="Arming.Writes"/>, they
    /// take S's answers for the conjuncts S owns, and every residual's weakening, as the switch will (g16): the rehearsal.
    /// A site reads <see cref="Armed"/>, one <c>volatile bool</c>, and only past it the flow's arm (<c>DESIGN.md</c>, <i>The
    /// shadow in production code</i>).
    /// </summary>
    internal static class CosmosRenderingShadow
    {

        static readonly AsyncLocal<Arming?> Flow = new();

        /// <summary>The arm every flow without its own takes: the whole-suite rehearsal's, set before anything plans.</summary>
        static volatile Arming? _everywhere;

        /// <summary>Whether any flow, or the process, has armed the shadow; once one has, a site also reads the flow's arm.</summary>
        internal static volatile bool Armed;

        /// <summary>The flow's arm, its own or the process's; read past <see cref="Armed"/> alone.</summary>
        internal static Arming? Current => Flow.Value ?? _everywhere;

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

        /// <summary>Arms every flow of the process to write, under a set or each container's own: the whole-suite rehearsal, set once, before anything plans.</summary>
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
