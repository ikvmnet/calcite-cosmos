namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a written form is claimed to be of the predicate it was written from, at a position.
    /// </summary>
    internal enum Verdict
    {

        /// <summary>It keeps exactly the rows the predicate keeps.</summary>
        Equal,

        /// <summary>It keeps every row the predicate keeps, and may keep more: a weakening Calcite rechecks.</summary>
        Implies,

    }

}
