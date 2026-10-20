namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where a predicate stands under the <c>WHERE</c>: kept where it is TRUE, or, beneath an odd number of <c>NOT</c>s,
    /// where it is FALSE.
    /// </summary>
    internal enum RenderPosition
    {

        /// <summary>A row is kept where the predicate is TRUE.</summary>
        Pos,

        /// <summary>A row is kept where the predicate is FALSE.</summary>
        Neg,

    }

}
