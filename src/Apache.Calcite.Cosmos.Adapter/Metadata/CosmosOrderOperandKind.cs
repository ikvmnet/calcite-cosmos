namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What an operand of an order the service computes is.
    /// </summary>
    /// <remarks>
    /// Each is read by <see cref="CosmosServiceOrder"/> from the facts its consumer holds; what each needs of them is
    /// said there. See <see cref="CosmosOrderOperand"/> for the members each kind carries.
    /// </remarks>
    public enum CosmosOrderOperandKind
    {

        /// <summary>
        /// A document path, read back as the column's type and reading say: the stored value, or its rendering as
        /// text.
        /// </summary>
        Path,

        /// <summary>
        /// A conversion of a document path's text into a type the service has no equivalent of — an instant or a
        /// UUID — which the service orders by the stored spelling.
        /// </summary>
        Conversion,

        /// <summary>
        /// A geodesic distance the service computes over stored geographies, and orders by.
        /// </summary>
        Distance,

        /// <summary>
        /// A relevance score the service ranks by and never returns.
        /// </summary>
        Score,

        /// <summary>
        /// A literal the statement carries.
        /// </summary>
        Literal,

        /// <summary>
        /// A parameter, whose value is bound when the statement runs.
        /// </summary>
        Parameter,

        /// <summary>
        /// Any other value the service computes, known by its type alone.
        /// </summary>
        Expression,

    }

}
