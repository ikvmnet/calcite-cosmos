using System;

namespace Apache.Calcite.Cosmos.Facts
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
    /// U+E000 as its first unit. So what a path's strings may hold of these two bands is the whole of what
    /// decides whether the orders agree over them (<c>CosmosServiceOrder</c>).
    /// </para>
    /// <para>
    /// <b>The adapter's spelling of a fact of the theory.</b> What characters a path's strings hold is the theory's
    /// <c>chars</c>, a set of these bands per path, as a type is a set of JSON types; <see cref="JsonClaim.Characters"/>
    /// states it and <see cref="JsonFactSet.CharactersOf"/> reads it back. The band below U+D800 has no member: nothing
    /// is gained by knowing a path lacks it, and a string all of whose units lie in it holds <see cref="None"/>. Which
    /// band a unit is in is decided in one place, the theory's own (<c>JsonValues.BandOf</c>).
    /// </para>
    /// </remarks>
    [Flags]
    public enum JsonCharacterBand
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

        /// <summary>
        /// Both: what a string nothing states the characters of may hold.
        /// </summary>
        Any = Surrogates | HighBmp,

    }

}
