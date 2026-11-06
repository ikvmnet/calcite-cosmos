namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What a reading's order holds in process, a <c>Calcite</c> row's (F3): <see cref="Itself"/>, the reading of a string
    /// is that string, so two strings read so compare as the strings do; <see cref="Reflexive"/>, two equal values read so
    /// compare equal. Read by <see cref="CosmosAxiomRows.ReadReaderOrders"/> into S's <c>readerorder</c>, every claim a row
    /// of its own, so that a reading no row names claims nothing.
    /// </summary>
    /// <param name="Reader">The reading, by the name a reader takes.</param>
    /// <param name="Claim">What its order holds.</param>
    internal sealed record CosmosReaderOrder(string Reader, string Claim)
    {

        /// <summary>The reading of a string is the string.</summary>
        public const string Itself = "itself";

        /// <summary>Two equal values read compare equal.</summary>
        public const string Reflexive = "reflexive";

    }

}
