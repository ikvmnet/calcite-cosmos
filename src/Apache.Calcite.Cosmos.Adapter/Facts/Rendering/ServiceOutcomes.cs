using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What the service may make of a condition on a row, as a set: what a <c>WHERE</c> reads, or the statement failing.
    /// </summary>
    [Flags]
    internal enum ServiceOutcomes
    {

        /// <summary>Nothing: never the answer on a cell.</summary>
        None = 0,

        /// <summary>A <c>WHERE</c> keeps the row.</summary>
        True = 1,

        /// <summary>A <c>WHERE NOT</c> keeps the row.</summary>
        False = 2,

        /// <summary>Neither keeps it: undefined, or a value that is no boolean.</summary>
        Undefined = 4,

        /// <summary>The statement fails, whatever else it was asked.</summary>
        Fails = 8,

        /// <summary>Anything: what a key no row settles answers.</summary>
        Widest = True | False | Undefined | Fails,

    }

}
