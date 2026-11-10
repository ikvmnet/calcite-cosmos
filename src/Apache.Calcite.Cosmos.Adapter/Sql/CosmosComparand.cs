namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A reading a <c>Calcite</c> row says compares a number Calcite wrote cast with the number the cast denotes (G8): by
    /// the reading its reader applies last (<c>PredicateAtom.Outermost</c>), which is what the value compares as; and
    /// whether it takes one a cast that drops scale moved, which only a reading that rescales its literal reads as such
    /// (<c>RETURNING DECIMAL</c>). Read by <see cref="CosmosAxiomRows.ReadComparands"/> into S's <c>takesconstant</c>, citing
    /// its row; a reading no row names compares with no number a cast denotes, and its comparison is no atom.
    /// </summary>
    /// <param name="Reading">The reading, by the name its reader's outermost reading takes (<c>cast:DOUBLE</c>, <c>typed:DECIMAL</c>).</param>
    /// <param name="Moved">Whether the number taken is one a cast that drops scale truncated, rather than the number itself.</param>
    internal sealed record CosmosComparand(string Reading, bool Moved);

}
