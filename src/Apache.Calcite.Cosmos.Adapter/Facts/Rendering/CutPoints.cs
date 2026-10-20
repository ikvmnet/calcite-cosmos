using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where a pair's cells are cut: at every literal the atom and the form compare with, and at the limits of the atom's
    /// reader, which a host row a rule hands in names. A cut only parts a cell in two, so one no reading needs changes no
    /// answer. The edges a typed or numeric reading moves a number across come with their cells (c13).
    /// </summary>
    internal static class CutPoints
    {

        public static ValueSet<CutPoint> Of(PredicateAtom atom, ServiceForm form, ReaderEdges edges) => ValueSet.From(
            new[] { atom.Operand }.OfType<string>().Concat(atom.Points ?? ValueSet<string>.Empty).Concat(form.Literals).Select(l => new CutPoint(l, CutKind.Literal))
                .Concat(edges.Boundaries.Select(b => new CutPoint(b, CutKind.Boundary))));

    }

}
