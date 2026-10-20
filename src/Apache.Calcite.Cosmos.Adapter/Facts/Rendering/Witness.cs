using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A cell of one path, named as the place a document sets that path to: the sibling's half of a refutation of a
    /// junction.
    /// </summary>
    internal sealed record Witness(string Path, PathCell Cell) : IComparable<Witness>
    {

        /// <summary>The least cell whose class is among the given; the classes must meet the cells'.</summary>
        public static Witness Of(CellOutcomes outcomes, ValueSet<CellClass> classes) =>
            new(outcomes.Path, outcomes.Cells.First(c => classes.Contains(c.Class)).Cell);

        /// <inheritdoc />
        public int CompareTo(Witness? other) =>
            other is null ? 1 : string.CompareOrdinal(Path, other.Path) is var order and not 0 ? order : Cell.CompareTo(other.Cell);

    }

}
