using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where one reader's answer changes, as host data: which edges of a numeric literal its reading moves a number across,
    /// and the limits of its type, in <c>JsonValues</c>' spelling, beyond which it raises or saturates. A reader with no row
    /// is cut nowhere.
    /// </summary>
    internal sealed record ReaderEdges(ValueSet<CutKind> Edges, ValueSet<string> Boundaries);

}
