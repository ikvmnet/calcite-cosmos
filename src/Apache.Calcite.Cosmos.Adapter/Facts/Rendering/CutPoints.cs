using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where a pair's cells are cut: at every literal the atom and the form compare with, at the edges the atom's reader
    /// names of each numeric one, at the reader's limits, and where each pattern matches, as Calcite's rows read it. Which
    /// edges and which units is host rows a rule hands in; this applies them by arithmetic. A cut only parts a cell in two,
    /// so one no reading needs changes no answer.
    /// </summary>
    internal static class CutPoints
    {

        public static ValueSet<CutPoint> Of(PredicateAtom atom, ServiceForm form, ReaderEdges edges, ReadingTable readings)
        {
            // A pattern is no literal: a LIKE's cuts where it matches (h07), one of literal units alone at the string it is, and
            // so does an affix's, each spelled as Calcite's rows read it (G7); one they leave unread cuts nowhere.
            var like = atom.Test == PredicateAtom.LikeTest && atom.Operand is { } operand ? LikePattern.Read(StringOf(operand), readings.Units) : null;
            var patterns = form.Subforms.Where(f => f.Kind == ServiceFormKind.Affix).Select(f => f.PatternOf(readings.Units)).Append(like).OfType<LikePattern>()
                .Select(p => p.Spelled).Distinct().ToList();
            var literals = new[] { atom.Test == PredicateAtom.LikeTest ? null : atom.Operand }.OfType<string>().Concat(atom.Points ?? ValueSet<string>.Empty)
                .Concat(form.Subforms.Where(f => f.Kind != ServiceFormKind.Affix && f.Members.Count == 0).SelectMany(f => f.Literals))
                .Concat(patterns.Select(AffixPattern.Unescaped).OfType<string>().Select(p => JsonValues.Of(p))).Distinct().ToList();
            return ValueSet.From(literals.Select(l => new CutPoint(l, CutKind.Literal)).Concat(edges.Boundaries.Select(b => new CutPoint(b, CutKind.Boundary)))
                .Concat(patterns.Where(p => AffixPattern.Unescaped(p) is null).Select(p => new CutPoint(JsonValues.Of(p), CutKind.Affix)))
                .Concat(from c in literals.Where(IsExactNumber).Select(NumberOf) from edge in edges.Edges select Edge(c, edge)).OfType<CutPoint>());
        }

        static CutPoint? Edge(decimal c, CutKind edge) => edge switch
        {
            CutKind.Below when c > decimal.MinValue + 1 => new(JsonValues.Of(c - 1), edge),
            CutKind.Above when c < decimal.MaxValue - 1 => new(JsonValues.Of(c + 1), edge),
            CutKind.Double => new(JsonValues.Of(c), edge),
            CutKind.Rendering => new(JsonValues.Of(StringOf(JsonValues.Of(c))), edge),
            _ => null,
        };

    }

}
