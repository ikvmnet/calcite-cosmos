using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// How a comparison of two paths reads, a <c>Service</c> row's (F3): <c>c.v op c.w</c> answers as <c>c.v</c> compared
    /// by <see cref="Reads"/> with the value at <c>w</c> written as a literal, or, <see cref="Swapped"/>, as <c>c.w</c>
    /// compared so with the value at <c>v</c>: the mirror law. Read by <see cref="CosmosAxiomRows.ReadPairReadings"/> into
    /// S's <c>pairreads</c>.
    /// </summary>
    /// <param name="Test">The comparison of two paths.</param>
    /// <param name="Swapped">Whether the sides are read the other way round.</param>
    /// <param name="Reads">The comparison the pair reads as, against the literal.</param>
    internal sealed record CosmosPairReading(CosmosServiceTest Test, bool Swapped, CosmosServiceTest Reads);

}
