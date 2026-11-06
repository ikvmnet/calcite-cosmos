namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The spelling a set's statements send a string in, which S derives from the set's string order and the rows of what
    /// its comparators' spelling rewrites (<c>RenderingTheory.Sends</c>, G1): as written, or as the target stores it
    /// (<see cref="EmulatorStoredText"/>), which the writer applies where S says so.
    /// </summary>
    internal enum SentSpelling
    {

        /// <summary>As written: the set's comparators compare what is sent as it is stored.</summary>
        Written,

        /// <summary>As the target stores it: a comparator of the set compares a spelling that rewrites a character.</summary>
        Stored,

    }

}
