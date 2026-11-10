using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// That a comparator compares a string in a spelling that rewrites a character of a band, a <c>Service</c> spelling
    /// row's (G1): the emulator compares and matches what it stores, U+0000 as U+FDD0 followed by <c>0</c> and U+FDD0 as
    /// U+FDD0 followed by <c>1</c>, against what it is sent. Read by <see cref="CosmosAxiomRows.ReadRewrites"/> into S's
    /// <c>rewrites</c>, from which S derives the spelling a set's statements send and where a match is read.
    /// </summary>
    /// <param name="Comparator">The comparator or matcher, an order or affix key's answer.</param>
    /// <param name="Band">The class of the character its spelling rewrites, by its member (<c>JsonValues.CharacterClasses</c>).</param>
    internal sealed record CosmosRewrite(CosmosAnswer Comparator, string Band);

}
