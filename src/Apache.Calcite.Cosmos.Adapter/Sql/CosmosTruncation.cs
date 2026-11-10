using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// That a test reads a string only up to its first character of a band, where its argument has a shape, on the targets
    /// in <see cref="On"/>, a <c>Service</c> row's (F3): Azure's <c>CONTAINS</c>, and a <c>LIKE</c> of one run of text
    /// between two <c>%</c>, which it answers as one, read a string to its first U+0000. Read by
    /// <see cref="CosmosAxiomRows.ReadTruncations"/> into S's <c>truncates</c>.
    /// </summary>
    /// <param name="Test">The test.</param>
    /// <param name="Shape">The shape of the test's argument the row holds of (<see cref="OneRun"/>).</param>
    /// <param name="Band">The band whose first character ends what the test reads, by its member (<c>JsonValues.CharacterClasses</c>).</param>
    /// <param name="On">The targets that read so.</param>
    internal sealed record CosmosTruncation(CosmosServiceTest Test, string Shape, string Band, CosmosAxiomTarget On)
    {

        /// <summary>An argument of one run of text between two <c>%</c>, as a <c>CONTAINS</c> is written (<c>AffixPattern.Runs</c>).</summary>
        public const string OneRun = AffixPattern.OneRun;

    }

}
