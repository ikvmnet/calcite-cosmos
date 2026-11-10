using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A reading the facts graph reads as Calcite does beside a literal of a class, a <c>Calcite</c> row's: on every cell
    /// a stratum resting on an atom read by it keeps, the coarse answer holds every truth Calcite gives there, so the
    /// stratum leaves out no document Calcite keeps. Read by <see cref="CosmosAxiomRows.ReadExactReadings"/> into both
    /// graphs' <c>readsexactly</c>, which the facts graph's exactness rule reads and the statement's strata read through it
    /// (<see cref="Facts.Saturation.PredicateAtom.ReadExactly"/>, <c>inexact</c>).
    /// </summary>
    /// <param name="Reader">The reading, its raw column apart, or <see cref="RenderingFamily.TypeTest"/> for a type test of any, or
    /// <see cref="RenderingFamily.PresenceTest"/> for a presence test of any.</param>
    /// <param name="Beside">The class of the literal it is compared beside: <see cref="CosmosValueClass.Number"/>, or
    /// <see cref="CosmosValueClass.None"/> for any other literal and for none.</param>
    internal sealed record CosmosExactReading(string Reader, CosmosValueClass Beside);

}
