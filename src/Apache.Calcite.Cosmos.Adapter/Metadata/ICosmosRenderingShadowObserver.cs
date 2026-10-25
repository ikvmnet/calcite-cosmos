using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What the generate shadow is told of S at production's four sites, where a test armed it
    /// (<see cref="CosmosRenderingShadow"/>), and what it hands them: the sets to ask beside the statement's own, its own
    /// oracle, and a test's plants.
    /// </summary>
    internal interface ICosmosRenderingShadowObserver
    {

        /// <summary>The sets each statement is asked under beside the one it is proven under, each in its own admissions.</summary>
        IReadOnlyList<RenderingSet> Sets { get; }

        /// <summary>The oracle the flow's questions go to, over the shadow's graph; none for each statement's schema's.</summary>
        RenderingOracle? Oracle { get; }

        /// <summary>A plant's reading precision for <c>StratumFor</c> (G12); none for the reading precision's rows.</summary>
        Func<SqlPredicate, bool>? ReadExactly { get; }

        /// <summary>A plant's node a conjunct is stated beneath in its own round (G11); none for <c>StratumFor</c>'s.</summary>
        Func<CosmosStatementScope, int, (Scope? Context, PredicateRules? Rules)?>? RenderedFrom { get; }

        /// <summary>A bundle S opened for a question, read before it goes: its roots' terms, its import, its run and S's time in it.</summary>
        void Opened(CosmosFilterStatement statement, CosmosFilterRendering.Question question, RenderingBundle bundle, IReadOnlyList<Pred> terms, ImportReport import, RenderingRun run,
            TimeSpan elapsed);

        /// <summary>
        /// Site W: S's weakening of a residual conjunct under a set, as the split rule would push it: the text its Rex
        /// translates to, none where the builder or the translator wrote none, and whether what is beneath applies it already.
        /// </summary>
        void Weakened(CosmosFilterStatement statement, int conjunct, RenderingSet set, ServiceForm form, string? text, bool applied);

        /// <summary>Site P: the form S admitted for a conjunct under a set, printed, beside the translator's text written; none where it did not print.</summary>
        void Printed(CosmosFilterStatement statement, int conjunct, RenderingSet set, ServiceForm form, string? text, string written);

        /// <summary>Site R: whether the residual projection rule fires over a filter by the translator's split, and by S's.</summary>
        void Placed(CosmosFilterStatement statement, bool translator, bool mine);

    }

}
