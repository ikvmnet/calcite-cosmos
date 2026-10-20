namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Why a path's cells are cut at a value: a literal of the pair, or an edge the atom's reader implies of one.
    /// </summary>
    internal enum CutKind
    {

        /// <summary>A literal the atom or the form compares with.</summary>
        Literal,

        /// <summary>One below a numeric literal, where a truncating reading moves a number by less than one.</summary>
        Below,

        /// <summary>One above a numeric literal.</summary>
        Above,

        /// <summary>The double a numeric literal rounds to.</summary>
        Double,

        /// <summary>The text a numeric literal renders as.</summary>
        Rendering,

        /// <summary>A limit of the reader's type, beyond which it raises or saturates.</summary>
        Boundary,

    }

}
