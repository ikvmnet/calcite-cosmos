using System;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The classes of characters on which the service's string functions and Calcite's part, or its order and Calcite's
    /// do: a set of them, which a string, a range of characters or a path's strings may hold characters of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three bands that partition what is outside ASCII, and two classes beside them.</b> Every UTF-16 unit outside
    /// ASCII is in one of three bands — U+0080 to U+D7FF, the surrogates, or from U+E000 up — and a string holding none
    /// of them is ASCII. Separately, a character is or is not one the service's <c>TRIM</c> removes beside U+0020, which
    /// cuts across the bands: a tab is ASCII, U+00A0 and U+3000 are in the first band. And U+0000, which is ASCII, is a
    /// class of its own.
    /// </para>
    /// <para>
    /// <b>What each decides.</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>The surrogates and the high band, the order.</b> The service orders a document's strings by
    /// code point, Calcite and the SDK's merge of two partitions by UTF-16 code unit, and the two orders are one except
    /// where a surrogate unit meets a unit from U+E000: a supplementary character is above U+FFFF as a code point and
    /// below U+E000 as its first unit. So what a path's strings may hold of these two bands is the whole of what decides
    /// whether the orders agree over them (<c>CosmosServiceOrder</c>, <see cref="FromD800"/>).</description></item>
    /// <item><description><b>The surrogates, a count.</b> <c>LENGTH</c> counts code points at the service and Calcite
    /// counts UTF-16 units; Azure's <c>SUBSTRING</c>, <c>LEFT</c> and <c>RIGHT</c> count units and send a split pair back
    /// as U+FFFD, the emulator's count code points. Without a surrogate the counts are one.</description></item>
    /// <item><description><b>All three bands, a case mapping.</b> Calcite maps case by Java's full mapping, the services
    /// by simpler ones that leave more unmapped; over ASCII the three are one (<see cref="BeyondAscii"/>).</description></item>
    /// <item><description><b>The trimmed class, a trim.</b> Calcite's <c>TRIM</c> removes U+0020 alone, and the service's
    /// removes the characters of <see cref="Trimmed"/> too.</description></item>
    /// <item><description><b>U+0000, a search.</b> Azure's <c>REPLACE</c>, <c>CONTAINS</c> and the <c>LIKE</c> it
    /// answers as a <c>CONTAINS</c> read a string only up to its first U+0000, and a search holding one matches anywhere
    /// (<see cref="Nul"/>).</description></item>
    /// </list>
    /// <para>
    /// <b>The adapter's spelling of a fact of the theory.</b> What characters a path's strings hold is the theory's
    /// <c>chars</c>, a set of these classes per path, as a type is a set of JSON types; <see cref="JsonClaim.Characters"/>
    /// states it and <see cref="JsonFactSet.CharactersOf(JsonDocumentPath)"/> reads it back. ASCII outside the trimmed class
    /// and other than U+0000 has no member: a string all of whose characters lie there holds <see cref="None"/>. Which
    /// classes a unit is in is decided in one place, the theory's own (<c>JsonValues.MaskOf</c>), and each class is named
    /// once, its flag beside its member (<c>JsonValues.CharacterClasses</c>).
    /// </para>
    /// </remarks>
    [Flags]
    public enum JsonCharacterBand
    {

        /// <summary>
        /// None of them: every character ASCII other than U+0000, and none the service's <c>TRIM</c> removes beside
        /// U+0020.
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
        /// U+0080 to U+D7FF: outside ASCII and below the surrogates. With <see cref="Surrogates"/> and
        /// <see cref="HighBmp"/>, every unit outside ASCII.
        /// </summary>
        NonAscii = 4,

        /// <summary>
        /// A character the service's <c>TRIM</c>, <c>LTRIM</c> and <c>RTRIM</c> remove beside U+0020, which Calcite's do not:
        /// measured on the probe account, U+0009 to U+000D, U+0085, U+00A0, U+1680, U+180E, U+2000 to U+200A, U+2028,
        /// U+2029, U+202F, U+205F and U+3000 (<c>Service.TrimRemovesWhiteSpace</c>). The emulator removes fewer.
        /// </summary>
        Trimmed = 8,

        /// <summary>
        /// U+0000. Measured on the probe account, Azure's <c>REPLACE</c> and <c>CONTAINS</c>, both modes, read a string
        /// only up to its first U+0000 — <c>REPLACE(s, 'b', '!')</c> leaves <c>a</c>U+0000<c>b</c> as it is and
        /// <c>CONTAINS</c> finds no <c>b</c> in it — and so does a <c>LIKE</c> whose pattern is one run of text between two
        /// <c>%</c>, which the service answers as a <c>CONTAINS</c>; a search holding U+0000 matches every string
        /// (<c>Service.NulEndsWhatReplaceAndContainsRead</c>). <c>INDEX_OF</c>, <c>STARTSWITH</c>, <c>ENDSWITH</c>, every
        /// other <c>LIKE</c>, the comparisons and the order read past it. The emulator stores it as U+FDD0 followed by
        /// <c>0</c>.
        /// </summary>
        Nul = 16,

        /// <summary>
        /// The surrogates and the high band: every unit from U+D800, the two whose meeting parts the service's order from
        /// Calcite's. A set without either holds only units below U+D800.
        /// </summary>
        FromD800 = Surrogates | HighBmp,

        /// <summary>
        /// The three bands: every unit outside ASCII. A set without any of them holds ASCII alone.
        /// </summary>
        BeyondAscii = Surrogates | HighBmp | NonAscii,

        /// <summary>
        /// Every class: what a string nothing states the characters of may hold.
        /// </summary>
        Any = Surrogates | HighBmp | NonAscii | Trimmed | Nul,

    }

}
