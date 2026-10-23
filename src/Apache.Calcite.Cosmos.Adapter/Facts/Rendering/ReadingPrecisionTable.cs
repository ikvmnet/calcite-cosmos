using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// How the facts graph reads each reader beside Calcite, as host data: exact where a stratum that assumes an atom read
    /// by it leaves out no document Calcite gives that atom's truth on, coarse otherwise. Which stratum a later round's
    /// atoms are stated beneath reads it (<see cref="RenderingFamily.ReadExactly(SqlPredicate)"/>), so a stratum never
    /// rests on a reading that may exclude cells Calcite keeps.
    /// </summary>
    /// <remarks>
    /// <c>PredicateAtomCellTests.EveryExactReadingAnswersAsItsCell</c> pins every row it calls exact. The rest are coarse:
    /// an integer type's column reads a fraction whose double is whole, a double compares doubles, <c>RETURNING DECIMAL</c>
    /// reads a fraction's rendering, and a cast of a raw value is the column's; and the raw value beside a number, which a
    /// <c>VARIANT</c> compares by its boxed type (<c>TODO.md</c>).
    /// </remarks>
    internal static class ReadingPrecisionTable
    {

        /// <summary>How a reader is read.</summary>
        public enum ReadingPrecision
        {

            /// <summary>Read more widely than Calcite reads it: a stratum resting on it may exclude a document Calcite keeps.</summary>
            Coarse,

            /// <summary>Read as Calcite reads it, whatever the literal.</summary>
            Exact,

            /// <summary>Read as Calcite reads it beside no number literal.</summary>
            ExactBesideNoNumber,

        }

        /// <summary>The readers read otherwise than coarsely; every other is coarse.</summary>
        public static IReadOnlyDictionary<string, ReadingPrecision> Rows { get; } = new Dictionary<string, ReadingPrecision>
        {
            [PredicateAtom.TextReader] = ReadingPrecision.Exact,
            [PredicateAtom.TypedReader("BOOLEAN")] = ReadingPrecision.Exact,
            [PredicateAtom.CastReader("INTEGER", PredicateAtom.TextReader)] = ReadingPrecision.Exact,
            [PredicateAtom.CastReader("BIGINT", PredicateAtom.TextReader)] = ReadingPrecision.Exact,
            [PredicateAtom.RawReader] = ReadingPrecision.ExactBesideNoNumber,
        };

        /// <summary>How a reader is read: its row, or coarse.</summary>
        public static ReadingPrecision Of(string reader) => Rows.TryGetValue(reader, out var precision) ? precision : ReadingPrecision.Coarse;

    }

}
