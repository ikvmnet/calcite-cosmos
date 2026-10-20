using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where one reader's answer changes, as host data: which edges of a numeric literal its reading moves a number
    /// across, and the limits of its type.
    /// </summary>
    /// <param name="Edges">Which of <see cref="CutKind.Below"/>, <see cref="CutKind.Above"/>, <see cref="CutKind.Double"/> and <see cref="CutKind.Rendering"/> a numeric literal implies.</param>
    /// <param name="Boundaries">The limits, in <c>JsonValues</c>' spelling, beyond which the reader raises or saturates.</param>
    internal sealed record ReaderEdges(ValueSet<CutKind> Edges, ValueSet<string> Boundaries);

}
