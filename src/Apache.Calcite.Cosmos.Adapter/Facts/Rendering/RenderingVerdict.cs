namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a bundle says of one claim: its kind, and the proof, the refutation or the reason that goes with it; and of a
    /// proof, whether it holds of nothing, Calcite raising on every row it was checked on.
    /// </summary>
    internal sealed record RenderingVerdict(RenderingVerdictKind Kind, Proof? Proof, Refutation? Refutation, string? Reason, bool Vacuous = false);

}
