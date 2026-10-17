using System;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The bands of UTF-16 code units whose order the service's comparison and Calcite's part over: a set of them, which
    /// a string, a range of characters or a path's strings may hold units of.
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
    /// The band below U+D800 has no member here: nothing is gained by knowing a path lacks it, and a string all of
    /// whose units lie in it holds <see cref="None"/>. Which band a unit is in is decided in one place,
    /// <see cref="CosmosCharacters.BandOf"/>.
    /// </para>
    /// </remarks>
    [Flags]
    public enum CosmosCharacterBand
    {

        /// <summary>
        /// Neither band: every unit below U+D800.
        /// </summary>
        None = 0,

        /// <summary>
        /// U+D800 to U+DFFF: the units of a surrogate pair, which is how a character beyond U+FFFF is
        /// written, and of a lone surrogate.
        /// </summary>
        Surrogates = 1,

        /// <summary>
        /// U+E000 to U+FFFF: the private use area and everything above it in the basic plane.
        /// </summary>
        HighBmp = 2,

    }

}
