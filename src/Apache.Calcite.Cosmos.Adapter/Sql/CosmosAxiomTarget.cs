using System;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Where a measured axiom was measured.
    /// </summary>
    [Flags]
    internal enum CosmosAxiomTarget
    {

        /// <summary>
        /// The Cosmos DB emulator, <c>vnext-preview</c>.
        /// </summary>
        Emulator = 1,

        /// <summary>
        /// A real Azure Cosmos DB account.
        /// </summary>
        Azure = 2,

        /// <summary>
        /// Calcite's own runtime at the version the adapter pins, with no service involved.
        /// </summary>
        Calcite = 4,

    }

}
