namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// A band of UTF-16 code units a path's strings may be known to hold none of: the two bands whose order
    /// the service's comparison and Calcite's part over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three bands, and only two of them matter.</b> Every unit is in one of three: below U+D800, the
    /// surrogates, or from U+E000 up. The service orders a document's strings by code point, Calcite and the
    /// SDK's merge of two partitions by UTF-16 code unit, and the two orders are one except where a surrogate
    /// unit meets a unit from U+E000: a supplementary character is above U+FFFF as a code point and below
    /// U+E000 as its first unit. So what a path's strings are known to lack of these two bands is the whole
    /// of what decides whether the orders agree over them. See <see cref="CosmosServiceOrder"/>.
    /// </para>
    /// <para>
    /// The band below U+D800 has no member here: nothing is gained by knowing a path lacks it.
    /// </para>
    /// </remarks>
    public enum CosmosCharacterBand
    {

        /// <summary>
        /// U+D800 to U+DFFF: the units of a surrogate pair, which is how a character beyond U+FFFF is
        /// written, and of a lone surrogate.
        /// </summary>
        Surrogates,

        /// <summary>
        /// U+E000 to U+FFFF: the private use area and everything above it in the basic plane.
        /// </summary>
        HighBmp,

    }

}
