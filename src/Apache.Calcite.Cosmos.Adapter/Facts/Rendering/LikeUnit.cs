using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What Calcite's <c>LIKE</c> reads one unit of a pattern as (<c>likeunits</c>, G7), as a <c>Calcite</c> row measured it:
    /// a run of any characters, one code point in no class <see cref="Except"/> names, or the character itself. A unit
    /// spelled empty is every unit no other names.
    /// </summary>
    /// <param name="Unit">The pattern's character, or empty for every other.</param>
    /// <param name="Reads"><see cref="Run"/>, <see cref="One"/> or <see cref="Itself"/>.</param>
    /// <param name="Except">The character class one code point is not in; empty for none.</param>
    internal readonly record struct LikeUnit(string Unit, string Reads, string Except) : IComparable<LikeUnit>
    {

        /// <summary>Any run of characters, the empty one among them.</summary>
        public const string Run = "run";

        /// <summary>One code point.</summary>
        public const string One = "one";

        /// <summary>The character itself.</summary>
        public const string Itself = "itself";

        /// <inheritdoc />
        public int CompareTo(LikeUnit other) => string.CompareOrdinal(Unit, other.Unit) is var u and not 0 ? u
            : string.CompareOrdinal(Reads, other.Reads) is var r and not 0 ? r : string.CompareOrdinal(Except, other.Except);

        /// <summary>The unit spelled <c>unit:reads-except</c>.</summary>
        public override string ToString() => Unit + ":" + Reads + (Except.Length == 0 ? "" : "-" + Except);

    }

}
