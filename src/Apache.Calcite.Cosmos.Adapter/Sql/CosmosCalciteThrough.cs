using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A test of a reading Calcite takes as the same test of its operand, a <c>Calcite</c> row's (G7): <c>IS NULL</c> of a
    /// cast is its operand's where the cast stands alone, as <c>Calcite.CastToBooleanParses</c> measured, so over each class
    /// the test answers what its operand's read of the class means beside what its own does. Read by
    /// <see cref="CosmosAxiomRows.ReadCalciteThrough"/> into <c>calcitethrough</c>.
    /// </summary>
    /// <param name="Reader">The reading, by the name a reader takes.</param>
    /// <param name="Test">The test taken through.</param>
    /// <param name="Operand">The reading of its operand.</param>
    internal sealed record CosmosCalciteThrough(string Reader, CosmosServiceTest Test, string Operand);

}
