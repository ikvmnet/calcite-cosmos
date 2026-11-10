using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// How a comparator reads a whole number within int64, a <c>Service</c> order row's (G2): by its <c>value</c>, the int64
    /// it is, or by its <c>double</c>, as a target that holds one written with a point as a double compares it; the cell
    /// keeps no spelling, so a comparator of both places it both ways. Read by <see cref="CosmosAxiomRows.ReadWholeReadings"/>
    /// into S's <c>wholereads</c>.
    /// </summary>
    /// <param name="Comparator">The comparator, an order key's answer.</param>
    /// <param name="Reading"><c>value</c> or <c>double</c>.</param>
    internal sealed record CosmosWholeReading(CosmosAnswer Comparator, string Reading);

}
