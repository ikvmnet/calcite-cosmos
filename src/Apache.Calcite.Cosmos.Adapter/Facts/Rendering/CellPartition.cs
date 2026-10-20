using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A path's import cut into cells (<see cref="PathCell.Split"/>): every value it admits lies in exactly one, and every
    /// cell holds one.
    /// </summary>
    internal sealed record CellPartition(string Path, ValueSet<PathCell> Cells);

}
