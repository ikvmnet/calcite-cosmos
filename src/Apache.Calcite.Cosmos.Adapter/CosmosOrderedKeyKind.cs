namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// How Calcite compares the values of one key of an order the reader completes: what a value of the column it orders
    /// is, read from the JSON the service returned.
    /// </summary>
    /// <remarks>
    /// Java's semantics, as Calcite's own comparator has them, and not the CLR runtime's, which orders text of a
    /// multi-key sort by culture and puts <c>NaN</c> first: see <c>Client.CosmosKeyComparer</c>. A value of any other
    /// JSON type than the kind's is one the column reads as null.
    /// </remarks>
    public enum CosmosOrderedKeyKind
    {

        /// <summary>
        /// A string, compared by UTF-16 code unit, which is <c>String.compareTo</c>: text, an identifier, and a stored
        /// form whose spelling orders as its value does.
        /// </summary>
        Text,

        /// <summary>
        /// A number read exactly, compared by value whatever its scale, which is <c>BigDecimal.compareTo</c>: every
        /// integer type and <c>DECIMAL</c>.
        /// </summary>
        Exact,

        /// <summary>
        /// A number read as a double, which is <c>Double.compare</c>: <c>DOUBLE</c> and <c>FLOAT</c>.
        /// </summary>
        Double,

        /// <summary>
        /// A number read as a float, which is <c>Float.compare</c>: <c>REAL</c>.
        /// </summary>
        Real,

        /// <summary>
        /// A boolean, false before true.
        /// </summary>
        Boolean,

    }

}
