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

        /// <summary>
        /// Branch-only, the Stage 3 census's (deleted at h16): an observer of every flow in the process, told beside the one
        /// a flow armed, which a census over the whole suite sets once at start-up.
        /// </summary>
        internal static ICosmosRenderingObserver? Census;

        /// <summary>Gets the observer this flow armed, if any, and the census's beside it.</summary>
        internal static ICosmosRenderingObserver? Observer => Census is { } census
            ? Current.Value is { } armed ? new Both(armed, census) : census
            : Current.Value;

        /// <summary>Tells an observer of every conjunct the flow writes until disposed.</summary>
        internal static IDisposable Arm(ICosmosRenderingObserver observer)
        {
            var arming = new Arming(Current.Value);
            Current.Value = observer;
            Armed = true;
            return arming;
        }

        /// <summary>Branch-only: tells the flow's observer, then the census.</summary>
        sealed class Both(ICosmosRenderingObserver first, ICosmosRenderingObserver second) : ICosmosRenderingObserver
        {

            public void Admitted(CosmosFilterStatement statement, int conjunct, string text, Sql.CosmosParameterList parameters) { first.Admitted(statement, conjunct, text, parameters); second.Admitted(statement, conjunct, text, parameters); }

            public void Written(CosmosFilterStatement statement, int conjunct, string text, Sql.CosmosParameterList parameters) { first.Written(statement, conjunct, text, parameters); second.Written(statement, conjunct, text, parameters); }

            public void Weakened(CosmosFilterStatement statement, int conjunct, string text, Sql.CosmosParameterList parameters) { first.Weakened(statement, conjunct, text, parameters); second.Weakened(statement, conjunct, text, parameters); }

            public void Read(CosmosFilterStatement statement, Apache.Calcite.Cosmos.Facts.AdmissionRound round) { first.Read(statement, round); second.Read(statement, round); }

            public void Planned(org.apache.calcite.rel.RelNode root, CosmosContainerMetadata container) { first.Planned(root, container); second.Planned(root, container); }

            public void Translated(string position, org.apache.calcite.rex.RexNode node, string text) { first.Translated(position, node, text); second.Translated(position, node, text); }

            public void Rewritten(org.apache.calcite.rex.RexNode condition, org.apache.calcite.rex.RexNode rewritten) { first.Rewritten(condition, rewritten); second.Rewritten(condition, rewritten); }

        }

        /// <summary>Puts the flow's observer back as <see cref="Arm"/> found it.</summary>
        sealed class Arming(ICosmosRenderingObserver? was) : IDisposable
        {

            public void Dispose() => Current.Value = was;

        }

    }

}
