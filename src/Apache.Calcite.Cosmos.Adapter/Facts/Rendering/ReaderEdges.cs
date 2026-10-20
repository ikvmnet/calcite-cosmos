using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where one reader's answer changes, as host data: the limits of its type, in <c>JsonValues</c>' spelling, beyond which
    /// it raises or saturates. A reader with no row is cut nowhere.
    /// </summary>
    internal sealed record ReaderEdges(ValueSet<string> Boundaries);

}
