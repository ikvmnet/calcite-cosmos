using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// That a comparator ranks a string holding a character of one band against one holding a character of another
    /// otherwise than Calcite's UTF-16 order does, a <c>Service</c> order row's (F3): U+E000 and above against a surrogate,
    /// which code point order puts above it and UTF-16 order below. Read by <see cref="CosmosAxiomRows.ReadParts"/> into S's
    /// <c>parts</c>, which its parted rules read through the comparators a set's order row names.
    /// </summary>
    /// <param name="Comparator">The comparator, an order key's answer.</param>
    /// <param name="Band">The band of the one string's character, by its member (<c>JsonValues.CharacterClasses</c>).</param>
    /// <param name="Other">The band of the other's.</param>
    internal sealed record CosmosParting(CosmosAnswer Comparator, string Band, string Other);

}
