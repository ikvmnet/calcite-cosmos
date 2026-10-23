namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What generate mode says of one root: Proven with its cheapest exact form and that form's proof, or why not; and
    /// beside either the weakening it wrote, <c>null</c> for none or <c>TRUE</c>. The one answer of a bundle's reads.
    /// </summary>
    internal sealed record RenderingAnswer(RenderingVerdictKind Kind, ServiceForm? Best, ServiceForm? Weakening, Proof? Proof, string? Reason);

}
