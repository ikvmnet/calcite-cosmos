using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The measured rows the facts graph's root holds, read once from the registry by the adapter
    /// (<c>CosmosReasoning.Rows</c>): what the service stores of a string written in a class, and what each type test is
    /// TRUE of in process, by its name and the JSON kinds (<c>Calcite.TypeTestsAreTheirBodies</c>, G7), which the shared
    /// vocabulary's type-test rule reads. A test no row names is no atom, so it proves nothing.
    /// </summary>
    /// <param name="Stored">What the service stores of a string written in a class.</param>
    /// <param name="TestKinds">Each type test's name beside each JSON kind it is TRUE of.</param>
    internal sealed record JsonRows(IReadOnlyCollection<JsonClassMap> Stored, IReadOnlyList<(string Test, string Kind)> TestKinds);

}
