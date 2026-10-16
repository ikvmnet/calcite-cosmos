using System;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The truth values a SQL predicate may take on a row: true, false and unknown, as a set.
    /// </summary>
    [Flags]
    internal enum Truths
    {

        /// <summary>No row: what a context that admits no document says of anything.</summary>
        None = 0,

        /// <summary>TRUE: the row is kept.</summary>
        True = 1,

        /// <summary>FALSE: the row is not kept, and <c>NOT</c> keeps it.</summary>
        False = 2,

        /// <summary>UNKNOWN: the row is not kept, and <c>NOT</c> does not keep it either.</summary>
        Unknown = 4,

        /// <summary>Any of the three.</summary>
        Any = True | False | Unknown,

        /// <summary>
        /// Calcite raises on the row: no truth value at all. The query that reaches it fails, so it is no row a predicate
        /// keeps or its negation keeps, and it supports no verdict.
        /// </summary>
        Raises = 8,

    }

}
