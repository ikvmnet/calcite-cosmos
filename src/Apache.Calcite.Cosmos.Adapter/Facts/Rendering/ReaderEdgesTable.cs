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
    /// Text, raw and a text cast of the raw value answer a string, a boolean, a null and a structure exactly at the
    /// literals compared with, and a number by its kind or its rendering's first unit, so none moves a value across an
    /// edge of a numeric literal and none has a limit. The typed and numeric readings, which truncate, saturate or round,
    /// enter with their edges when their cells do.
    /// </remarks>
    internal static class ReaderEdgesTable
    {

        static readonly ReaderEdges AtTheLiterals = new(ValueSet<CutKind>.Empty, ValueSet<string>.Empty);

        public static IReadOnlyDictionary<string, ReaderEdges> Rows { get; } = new Dictionary<string, ReaderEdges>
        {
            [PredicateAtom.TextReader] = AtTheLiterals,
            [PredicateAtom.RawReader] = AtTheLiterals,
            [PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)] = AtTheLiterals,
        };

    }

}
