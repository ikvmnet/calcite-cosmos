using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A path's import cut into cells (<see cref="PathCell.Split"/>): every value it admits lies in exactly one, and every
    /// cell holds one; and those of them the facts the conjunct was rendered from admit nothing of
    /// (<see cref="PathCell.SetAside"/>), which only a document an earlier round's conjunct drops holds.
    /// </summary>
    internal sealed record CellPartition(string Path, ValueSet<PathCell> Cells, ValueSet<PathCell>? Outside = null);

}
