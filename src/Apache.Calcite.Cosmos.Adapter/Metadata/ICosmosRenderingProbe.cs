using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What a test's schema hands its filters' admission and is told of it (<see cref="CosmosReasoning.Probe"/>): the sets
    /// each statement is asked under beside its own, which the rendering check asserts and reports without planning again;
    /// each bundle S opens, which lines 1–4 judge; and the plants G9, G11 and G12 read. Production's reasonings hold none.
    /// </summary>
    internal interface ICosmosRenderingProbe
    {

        /// <summary>The sets each statement is asked under beside the one it is proven under, each in its own admissions, never taken.</summary>
        IReadOnlyList<RenderingSet> Sets { get; }

        /// <summary>A plant's reading precision for <c>StratumFor</c> (G12); none for the reading precision's rows.</summary>
        Func<SqlPredicate, bool>? ReadExactly { get; }

        /// <summary>A plant's node a conjunct is stated beneath in its own round (G11); none for <c>StratumFor</c>'s.</summary>
        Func<CosmosStatementScope, int, (Scope? Context, PredicateRules? Rules)?>? RenderedFrom { get; }

        /// <summary>A plant's name for a question within its terms, less a value it names (G9); none for the question's own.</summary>
        Func<string, string>? Signature { get; }

        /// <summary>A bundle S opened for a question, read before it goes: its roots' terms, its import, its run and S's time in it.</summary>
        void Opened(CosmosFilterStatement statement, CosmosFilterRendering.Question question, RenderingBundle bundle, IReadOnlyList<Pred> terms, ImportReport import, RenderingRun run,
            TimeSpan elapsed);

    }

}
