using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The reading a reading is evaluated as over one class of value (<c>calcitereadas</c>, G7): another a row says it is
    /// read as, over a class both read alike.
    /// </summary>
    /// <param name="Kind">The class of the stored value.</param>
    /// <param name="Reader">The reading it is evaluated as there.</param>
    internal readonly record struct ClassAlias(CosmosValueClass Kind, string Reader) : IComparable<ClassAlias>
    {

        /// <inheritdoc />
        public int CompareTo(ClassAlias other) => Kind != other.Kind ? Kind.CompareTo(other.Kind) : string.CompareOrdinal(Reader, other.Reader);

        /// <summary>The alias spelled <c>kind as reader</c>.</summary>
        public override string ToString() => Kind + " as " + Reader;

    }

}
