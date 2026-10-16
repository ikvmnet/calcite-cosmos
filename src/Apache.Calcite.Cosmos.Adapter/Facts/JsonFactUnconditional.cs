using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What a declaration with no conditional rule states, as the overlay reads it: the paths it states a fact about, and
    /// the tables of its own context.
    /// </summary>
    /// <param name="Stated">Every path the declaration states a fact about.</param>
    /// <param name="Outright">The tables of the declaration's context.</param>
    internal sealed record JsonFactUnconditional(IReadOnlyList<JsonDocumentPath> Stated, JsonFactTables Outright);

}
