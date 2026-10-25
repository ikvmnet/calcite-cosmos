using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a question to S cost: whether the memo answered it, a bundle was opened, or it declined; the run's iterations;
    /// and S's time from the lookup to the roots' answers. The unit the per-query lines are read in (spec §6.3), which g15's
    /// accumulator sums a query.
    /// </summary>
    internal sealed record RenderingCost(int Hits, int Bundles, int Declines, int Iterations, TimeSpan Elapsed)
    {

        /// <summary>Nothing asked.</summary>
        public static RenderingCost None { get; } = new(0, 0, 0, 0, TimeSpan.Zero);

    }

}
