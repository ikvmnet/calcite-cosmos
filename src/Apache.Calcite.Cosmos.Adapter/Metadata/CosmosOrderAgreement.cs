namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// How the order the service gives a key's values stands to the order Calcite gives the same rows, over the
    /// values the facts admit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The question is about pairs.</b> An order the service computes is allowable for every database the facts
    /// admit exactly where, for every two admitted values Calcite orders strictly, the service orders them the same
    /// way strictly; a tie is Calcite's to break any way it likes. Where the service is finer than Calcite that is
    /// still so, and where it ties what Calcite parts, or parts the other way, it is not.
    /// </para>
    /// <para>
    /// <b>Why two grades and not one.</b> Before the last key, a tie decides which rows the next key orders, so the
    /// service has to tie exactly what Calcite ties; on the last key a finer order is one Calcite allows. A
    /// comparison is stricter still and needs <see cref="Exact"/>: it keeps a row or drops it, and there is no tie to
    /// be free in. See <see cref="CosmosServiceOrder"/>.
    /// </para>
    /// </remarks>
    public enum CosmosOrderAgreement
    {

        /// <summary>
        /// Some two admitted values the service and Calcite order apart, or no order is claimed at all.
        /// </summary>
        None,

        /// <summary>
        /// The service orders strictly every two values Calcite orders strictly, the same way, and may order two
        /// Calcite ties: enough for the last key.
        /// </summary>
        Refines,

        /// <summary>
        /// The two are one order, ties included: enough for any key and for a comparison.
        /// </summary>
        Exact,

    }

}
