namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a bundle says of one claim: its kind, and the proof, the refutation or the reason that goes with it.
    /// </summary>
    internal sealed record RenderingVerdict(RenderingVerdictKind Kind, Proof? Proof, Refutation? Refutation, string? Reason);

}
