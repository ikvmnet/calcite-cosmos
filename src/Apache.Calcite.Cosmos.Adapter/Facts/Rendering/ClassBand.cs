using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A character class a reading's witnesses of one class of value held (<c>calcitebands</c>, G7): what its parse of that
    /// class was measured over, and so decided over.
    /// </summary>
    /// <param name="Kind">The class of the stored value.</param>
    /// <param name="Band">The character class, by its name in <c>JsonValues</c>.</param>
    internal readonly record struct ClassBand(CosmosValueClass Kind, string Band) : IComparable<ClassBand>
    {

        /// <inheritdoc />
        public int CompareTo(ClassBand other) => Kind != other.Kind ? Kind.CompareTo(other.Kind) : string.CompareOrdinal(Band, other.Band);

        /// <summary>The band spelled <c>kind~band</c>.</summary>
        public override string ToString() => Kind + "~" + Band;

    }

}
