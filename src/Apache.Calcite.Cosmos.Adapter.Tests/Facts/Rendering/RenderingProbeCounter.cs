using System;
using System.Collections.Generic;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// A schema's probe that asks S under every set the legs plan under and the one beside, and counts the bundles opened:
    /// what a test that only needs the probe exercised hands its schema, where nothing it is told is read.
    /// </summary>
    internal sealed class RenderingProbeCounter : ICosmosRenderingProbe
    {

        int _opened;

        public IReadOnlyList<RenderingSet> Sets { get; } = [.. RenderingGenerateRun.Sets];

        public Func<SqlPredicate, bool>? ReadExactly => null;

        public Func<CosmosStatementScope, int, (Scope? Context, PredicateRules? Rules)?>? RenderedFrom => null;

        public Func<string, string>? Signature => null;

        /// <summary>The bundles opened.</summary>
        public int Opened => Volatile.Read(ref _opened);

        void ICosmosRenderingProbe.Opened(CosmosFilterStatement statement, CosmosFilterRendering.Question question, RenderingBundle bundle, IReadOnlyList<Pred> terms, ImportReport import,
            RenderingRun run, TimeSpan elapsed) => Interlocked.Increment(ref _opened);

    }

}
