using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where each reader's answer changes, as host data: the <c>edges</c> rows S's <c>cuts</c> rule applies. A reader with
    /// no row is cut nowhere, so an atom read by it is never evaluated and stays unproven.
    /// </summary>
    /// <remarks>
    /// Text and raw answer at the literals compared with alone. A text cast of the raw value has no row: over a
    /// <c>VARIANT</c> Calcite casts every value but a string to null, which its cells, modelled on the rendering, do not
    /// say (<c>TODO.md</c>). The typed and numeric readings enter with their edges when their cells do.
    /// </remarks>
    internal static class ReaderEdgesTable
    {

        static readonly ReaderEdges AtTheLiterals = new(ValueSet<string>.Empty);

        public static IReadOnlyDictionary<string, ReaderEdges> Rows { get; } = new Dictionary<string, ReaderEdges>
        {
            [PredicateAtom.TextReader] = AtTheLiterals,
            [PredicateAtom.RawReader] = AtTheLiterals,
        };

    }

}
