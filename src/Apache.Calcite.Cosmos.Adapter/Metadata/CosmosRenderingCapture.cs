using System;
using System.Threading;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The rendering check's hook into the filter's two writing sites: inert unless a test arms it, which production never
    /// does (<c>DESIGN.md</c>, <i>The rendering graph in check mode</i>).
    /// </summary>
    internal static class CosmosRenderingCapture
    {

        static readonly AsyncLocal<ICosmosRenderingObserver?> Current = new();

        /// <summary>Whether any flow has armed the capture; once one has, a site also reads the flow's observer.</summary>
        internal static volatile bool Armed;

        /// <summary>Gets the observer this flow armed, if any.</summary>
        internal static ICosmosRenderingObserver? Observer => Current.Value;

        /// <summary>Tells an observer of every conjunct the flow writes until disposed.</summary>
        internal static IDisposable Arm(ICosmosRenderingObserver observer)
        {
            var arming = new Arming(Current.Value);
            Current.Value = observer;
            Armed = true;
            return arming;
        }

        /// <summary>Puts the flow's observer back as <see cref="Arm"/> found it.</summary>
        sealed class Arming(ICosmosRenderingObserver? was) : IDisposable
        {

            public void Dispose() => Current.Value = was;

        }

    }

}
