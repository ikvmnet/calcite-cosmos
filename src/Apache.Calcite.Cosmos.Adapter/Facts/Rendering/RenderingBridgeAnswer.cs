using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What S answered one question of the translator's bridge (G6): a call's image (<see cref="RenderingOracle.ImageOf"/>)
    /// and the spelling S chose of it where its rows give it more than one, or the case-insensitive affix a <c>LIKE</c> over a
    /// fold answers as (<see cref="RenderingOracle.FoldOf"/>), each <c>null</c> where S gives none, with why; why the question
    /// declined, where it did, a reason a filter's decline is counted by (<see cref="RenderingOracle.Unavailable"/>,
    /// <see cref="RenderingOracle.Engine"/>, <see cref="RenderingOracle.Incomplete"/>), in which case nothing is S's answer and
    /// nothing is remembered; and what the question cost, a remembered answer's its lookup alone.
    /// </summary>
    internal sealed record RenderingBridgeAnswer(PathValues? Image, string? Why, long? Spelling, string? Declined, RenderingCost Cost,
        (CosmosServiceTest Test, string Argument, long Operand)? Fold = null);

}
