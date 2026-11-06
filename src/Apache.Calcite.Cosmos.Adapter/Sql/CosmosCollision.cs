using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// That under a comparator a stored string holding a character of one band may equal a sent value holding one of
    /// another that it does not hold, a <c>Service</c> order row's (G1): the emulator stores U+0000 as U+FDD0 followed by
    /// <c>0</c>, which a value holding U+FDD0, of the high band, spells. Read by <see cref="CosmosAxiomRows.ReadCollisions"/>
    /// into S's <c>collides</c>.
    /// </summary>
    /// <param name="Comparator">The comparator, an order key's answer.</param>
    /// <param name="Band">The band the stored string holds, by its member (<c>JsonValues.CharacterClasses</c>).</param>
    /// <param name="Other">The band the sent value holds.</param>
    internal sealed record CosmosCollision(CosmosAnswer Comparator, string Band, string Other);

}
