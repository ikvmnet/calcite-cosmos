using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// That a test's case-insensitive form, its third operand <c>true</c>, matches a string against an argument exactly as the
    /// test matches Calcite's fold of each by a function, where neither holds a character of a band, on the targets in
    /// <see cref="On"/>, a <c>Service</c> row's (G6): the service's case-insensitive <c>STARTSWITH</c>, <c>ENDSWITH</c> and
    /// <c>CONTAINS</c> fold ASCII case as <c>UPPER</c> and <c>LOWER</c> do, and part from Java's full mapping beyond ASCII.
    /// Read by <see cref="CosmosAxiomRows.ReadFolds"/> into S's <c>foldsas</c>.
    /// </summary>
    /// <param name="Test">The test whose case-insensitive form the row holds of.</param>
    /// <param name="Function">The fold, by the name its function rows are filed under.</param>
    /// <param name="Band">The bands the row says nothing of, by flag (<c>JsonValues.CharacterClasses</c>).</param>
    /// <param name="On">The targets that match so.</param>
    internal sealed record CosmosFold(CosmosServiceTest Test, string Function, JsonCharacterBand Band, CosmosAxiomTarget On);

}
