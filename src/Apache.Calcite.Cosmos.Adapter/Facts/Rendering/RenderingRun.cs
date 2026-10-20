using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// How a bundle's run ended: why it stopped, after how many iterations, whether every class of its terms still holds
    /// one node (no rule merged two), and whether it derived a contradiction.
    /// </summary>
    internal sealed record RenderingRun(RunStopReason StopReason, int Iterations, bool OneNodePerClass, bool Contradicted);

}
