using System;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A cell and its class: what a witness or a refutation names.
    /// </summary>
    internal sealed record CellAt(PathCell Cell, CellClass Class) : IComparable<CellAt>
    {

        /// <inheritdoc />
        public int CompareTo(CellAt? other) => other is null ? 1 : Cell.CompareTo(other.Cell) is var order and not 0 ? order : Class.CompareTo(other.Class);

        /// <inheritdoc />
        public override string ToString() => Cell + " " + Class;

    }

}
