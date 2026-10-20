using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// One measured fact a pushdown relies on.
    /// </summary>
    /// <param name="Id">A stable name, <c>Service.</c> or <c>Calcite.</c> by what was measured.</param>
    /// <param name="Statement">What holds, in a sentence or two, as the measurement asserts it.</param>
    /// <param name="MeasuredOn">Every target the measurement has been run against and passed.</param>
    /// <param name="Measured">When it was first measured.</param>
    /// <param name="PinnedBy">The test that measures it, relative to the test assembly's root
    /// namespace: <c>Measurements.CosmosJsonTypeMeasurementTests.IsPrimitiveIsFalseOverAnAbsentPath</c>.</param>
    /// <param name="Outcomes">What the measurement answered, key by key, as the rendering graph reads it
    /// (<see cref="CosmosAxiomRows"/>); each names the targets that ran it, within <paramref name="MeasuredOn"/>.</param>
    internal sealed record CosmosAxiom(string Id, string Statement, CosmosAxiomTarget MeasuredOn, DateOnly Measured, string PinnedBy,
        IReadOnlyList<CosmosOutcome>? Outcomes = null);

}
