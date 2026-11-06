using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// That a comparator moves a character of a band in order from where Calcite's order puts it, a <c>Service</c> order
    /// row's (F3): the emulator's order of what it stores puts U+0000, stored as U+FDD0 followed by <c>0</c>, above every
    /// unit from U+0001 to U+FDCF. Read by <see cref="CosmosAxiomRows.ReadMoves"/> into S's <c>moves</c>.
    /// </summary>
    /// <param name="Comparator">The comparator, an order key's answer.</param>
    /// <param name="Band">The band of the character it moves, by its member (<c>JsonValues.CharacterClasses</c>).</param>
    internal sealed record CosmosMove(CosmosAnswer Comparator, string Band);

}
