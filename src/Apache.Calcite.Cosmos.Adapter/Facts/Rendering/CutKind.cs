namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Why a path's cells are cut at a value.
    /// </summary>
    internal enum CutKind
    {

        /// <summary>A literal the atom or the form compares with.</summary>
        Literal,

        /// <summary>A limit of the reader's type, beyond which it raises or saturates.</summary>
        Boundary,

    }

}
