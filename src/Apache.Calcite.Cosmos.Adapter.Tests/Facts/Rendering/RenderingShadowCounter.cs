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
    /// An observer that asks S at production's sites under every set the legs plan under and the one beside, through an
    /// oracle of its own that waits for its build, and counts what each site was told: what a test that only needs the sites
    /// exercised arms, where the shadow's classes are not read.
    /// </summary>
    internal sealed class RenderingShadowCounter(RenderingOracle? oracle = null, IReadOnlyList<RenderingSet>? sets = null, Func<string, string>? signature = null) : ICosmosRenderingShadowObserver
    {

        int _opened, _weakened, _placed;

        public IReadOnlyList<RenderingSet> Sets { get; } = sets ?? [.. RenderingGenerateRun.Sets];

        public RenderingOracle? Oracle { get; } = oracle ?? new RenderingOracle(RenderingCheckRun.Measured, wait: Timeout.InfiniteTimeSpan);

        public Func<SqlPredicate, bool>? ReadExactly => null;

        public Func<CosmosStatementScope, int, (Scope? Context, PredicateRules? Rules)?>? RenderedFrom => null;

        public Func<string, string>? Signature { get; } = signature;

        /// <summary>Bundles opened, and what sites W and R were told.</summary>
        public (int Opened, int Weakened, int Placed) Counts => (Volatile.Read(ref _opened), Volatile.Read(ref _weakened), Volatile.Read(ref _placed));

        public void Opened(CosmosFilterStatement statement, CosmosFilterRendering.Question question, RenderingBundle bundle, IReadOnlyList<Pred> terms, ImportReport import, RenderingRun run, TimeSpan elapsed) =>
            Interlocked.Increment(ref _opened);

        public void Weakened(CosmosFilterStatement statement, int conjunct, RenderingSet set, ServiceForm form, string? text, bool applied) => Interlocked.Increment(ref _weakened);

        public void Placed(CosmosFilterStatement statement, bool translator, bool mine) => Interlocked.Increment(ref _placed);

    }

}
