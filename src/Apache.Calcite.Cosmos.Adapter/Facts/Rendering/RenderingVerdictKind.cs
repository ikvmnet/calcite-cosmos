namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a bundle's run says of one claim.
    /// </summary>
    internal enum RenderingVerdictKind
    {

        /// <summary>The claim holds on every document the import admits.</summary>
        Proven,

        /// <summary>A cell refutes it: a document on which the two sides definitely part.</summary>
        Refuted,

        /// <summary>The run completed and decided neither.</summary>
        Unproven,

        /// <summary>The run stopped short, so nothing it derived may be read.</summary>
        Incomplete,

        /// <summary>The bundle derived an agreement and a refutation of one claim: the rows or the rules are wrong.</summary>
        Conflict,

    }

}
