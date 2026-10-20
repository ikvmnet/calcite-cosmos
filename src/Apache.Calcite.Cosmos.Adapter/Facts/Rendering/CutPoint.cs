using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A value a path's import is cut at, in <c>JsonValues</c>' spelling, and why.
    /// </summary>
    internal sealed record CutPoint(string Value, CutKind Kind) : IComparable<CutPoint>
    {

        /// <inheritdoc />
        public int CompareTo(CutPoint? other) =>
            other is null ? 1 : string.CompareOrdinal(Value, other.Value) is var order and not 0 ? order : Kind.CompareTo(other.Kind);

    }

}
