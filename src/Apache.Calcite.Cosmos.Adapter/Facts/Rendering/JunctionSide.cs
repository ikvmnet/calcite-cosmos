namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Which arm of a junction in the IR a <c>junction</c> row takes as its own side, the other being its sibling. Each
    /// junction has a row for each, so a rule that reads one side reads either.
    /// </summary>
    internal enum JunctionSide
    {

        /// <summary>A conjunction, read from its first arm.</summary>
        AndLeft,

        /// <summary>A conjunction, read from its second arm.</summary>
        AndRight,

        /// <summary>A disjunction, read from its first arm.</summary>
        OrLeft,

        /// <summary>A disjunction, read from its second arm.</summary>
        OrRight,

    }

}
