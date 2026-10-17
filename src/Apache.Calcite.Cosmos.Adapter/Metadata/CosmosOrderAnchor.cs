namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What a value has to be for every admitted value to compare with it alike under the service's order and Calcite's:
    /// the check an anchored order makes of each value as the rows arrive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An anchor is a value the two orders agree about.</b> Where the facts cannot prove that the service orders a key
    /// as Calcite does over every value they admit, they can still say which <em>values</em> every other compares with
    /// alike: a string with no unit at or above U+D800, which orders the same against anything by code point and by
    /// UTF-16 code unit; a number strictly within ±2^53, which a double reads exactly and nothing past it reads back on
    /// its side. A value the column reads as null is one too, where the null class has passed R3, which places it alike
    /// in both orders. Around such a value the stream the service returns is in Calcite's order, whatever lies between
    /// — see <see cref="CosmosServiceOrder.IsAnchor"/>, and <c>Client.CosmosOrderedCursor</c> for what is done with it.
    /// </para>
    /// </remarks>
    public enum CosmosOrderAnchor
    {

        /// <summary>
        /// Every value: the order of this key is proved when the plan is made.
        /// </summary>
        None,

        /// <summary>
        /// A string whose every UTF-16 code unit lies below U+D800, or a value read as null.
        /// </summary>
        Text,

        /// <summary>
        /// A number strictly within ±2^53, or a value read as null.
        /// </summary>
        Number,

    }

}
