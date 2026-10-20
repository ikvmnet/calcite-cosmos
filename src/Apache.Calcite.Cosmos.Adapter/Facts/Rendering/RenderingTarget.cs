namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Who answered a measured row: the registry's targets, which <c>Facts</c> does not reference.
    /// </summary>
    internal enum RenderingTarget
    {

        /// <summary>The Cosmos DB emulator.</summary>
        Emulator,

        /// <summary>The service on a real account.</summary>
        Azure,

        /// <summary>Calcite's own runtime, with no service.</summary>
        Calcite,

    }

}
