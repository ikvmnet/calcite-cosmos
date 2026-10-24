using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What S's answers cost: the questions the memo answered, the bundles opened, the questions declined, every run's
    /// iterations and its templates' candidates, S's time from the lookup to the roots' answers, the bytes allocated on
    /// the asking thread, and the values the graph interned and the share of its ids or epochs it had used after. One
    /// question's, or summed, a query's: the unit the per-query lines are read in (spec §6.3).
    /// </summary>
    internal sealed record RenderingCost(int Hits, int Bundles, int Declines, int Iterations, int Candidates, TimeSpan Elapsed, long Allocated, long Interned, double Used)
    {

        /// <summary>Nothing asked.</summary>
        public static RenderingCost None { get; } = new(0, 0, 0, 0, 0, TimeSpan.Zero, 0, 0, 0);

        /// <summary>Two costs together: each count and time summed, the share used the greater.</summary>
        public static RenderingCost operator +(RenderingCost a, RenderingCost b) => new(a.Hits + b.Hits, a.Bundles + b.Bundles, a.Declines + b.Declines,
            a.Iterations + b.Iterations, a.Candidates + b.Candidates, a.Elapsed + b.Elapsed, a.Allocated + b.Allocated, a.Interned + b.Interned, Math.Max(a.Used, b.Used));

    }

}
