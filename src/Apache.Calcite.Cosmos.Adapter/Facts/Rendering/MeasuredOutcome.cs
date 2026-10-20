namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One measured row: what one target answered for one key, and the axiom that records it.
    /// </summary>
    internal sealed record MeasuredOutcome(string AxiomId, MeasuredKey Key, RenderingTarget Target, CosmosAnswer Answer);

}
