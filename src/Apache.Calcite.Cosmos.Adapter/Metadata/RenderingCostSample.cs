using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// One question a plan asked S, as the cost instrument read it (spec §6.4): the site and set, the roots, what the oracle
    /// says it cost (a hit, a bundle or a decline, the run's iterations, S's time), the candidates its templates proposed,
    /// the bytes the thread allocated meanwhile, the generation's graph before and after, none before the first, and the
    /// thread's processor time, where the measure was handed its clock and the oracle was asked.
    /// </summary>
    internal sealed record RenderingCostSample(string Site, RenderingSet Set, IReadOnlyList<SqlPredicate> Roots, RenderingCost Cost, int Candidates, long Bytes,
        GraphUsage? Before, GraphUsage? After, TimeSpan? Thread = null);

}
