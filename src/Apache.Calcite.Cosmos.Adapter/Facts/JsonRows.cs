using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The measured rows the facts graph's root holds, read once from the registry by the adapter
    /// (<c>CosmosReasoning.Rows</c>): what the service stores of a string written in a class, and the <c>Calcite</c> rows of
    /// readings, among them what each type test is TRUE of in process (<c>Calcite.TypeTestsAreTheirBodies</c>, G7), which
    /// the readings vocabulary keys by the test's name for the shared vocabulary's type-test rule. A test no row names is
    /// no atom, so it proves nothing.
    /// </summary>
    /// <param name="Stored">What the service stores of a string written in a class.</param>
    /// <param name="Calcite">The <c>Calcite</c> rows of readings, which the readings vocabulary reads at the root (G7).</param>
    internal sealed record JsonRows(IReadOnlyCollection<JsonClassMap> Stored, CalciteRows Calcite);

}
