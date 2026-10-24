namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The service a container's statements run on, which decides what the adapter may prove of the filters it sends: each
    /// is held to what that service was measured to do, and the emulator stores and orders some strings unlike Azure.
    /// </summary>
    public enum CosmosServiceTarget
    {

        /// <summary>Azure Cosmos DB, the default.</summary>
        Azure,

        /// <summary>The Cosmos DB emulator.</summary>
        Emulator,

    }

}
