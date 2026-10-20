namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The shape of a <see cref="ServiceForm"/>.
    /// </summary>
    internal enum ServiceFormKind
    {

        /// <summary>An <c>IS_*</c> test of a path.</summary>
        Test,

        /// <summary>A path compared with a literal.</summary>
        Compare,

        /// <summary>A path's membership in literals.</summary>
        In,

        /// <summary>A path written as a whole condition.</summary>
        Bare,

        /// <summary><c>true</c> or <c>false</c>.</summary>
        Constant,

        Not,
        And,
        Or,

        /// <summary>Text the reader could not read into a shape.</summary>
        Opaque,

    }

}
