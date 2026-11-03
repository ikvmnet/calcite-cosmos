namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// How a node's weakening is assembled from its parts' where no connective of the service writes it alone. Assembles
    /// from complete inputs; whether it holds is the rule's (<c>DESIGN.md</c>, <i>The rendering graph beyond the WHERE</i>).
    /// </summary>
    internal static class RenderingWeakening
    {

        /// <summary>
        /// A case's: at <c>Pos</c>, <c>(wc AND wa) OR wb</c>, true wherever the condition and the then-arm both are, or the
        /// else-arm is; at <c>Neg</c>, <c>wa AND wb</c>, false wherever either arm is. <paramref name="wb"/> is the wide
        /// else-arm's, which holds beneath any condition.
        /// </summary>
        /// <param name="pos">Where the case stands.</param>
        /// <param name="wc">The condition's weakening at <c>Pos</c>.</param>
        /// <param name="wa">The then-arm's at the case's position.</param>
        /// <param name="wb">The wide else-arm's at the case's position.</param>
        public static ServiceForm OfCase(RenderPosition pos, ServiceForm wc, ServiceForm wa, ServiceForm wb) => pos == RenderPosition.Pos
            ? ServiceForm.Joined(CosmosServiceTest.Or, ServiceForm.Joined(CosmosServiceTest.And, wc, wa), wb)
            : ServiceForm.Joined(CosmosServiceTest.And, wa, wb);

    }

}
