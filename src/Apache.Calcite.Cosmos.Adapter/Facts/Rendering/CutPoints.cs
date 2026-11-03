using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where a pair's cells are cut: at every literal the atom and the form compare with, at the edges the atom's reader
    /// names of each numeric one, and at the reader's limits. Which edges is a host row a rule hands in; this applies it by
    /// arithmetic. A cut only parts a cell in two, so one no reading needs changes no answer.
    /// </summary>
    internal static class CutPoints
    {

        public static ValueSet<CutPoint> Of(PredicateAtom atom, ServiceForm form, ReaderEdges edges)
        {
            var literals = new[] { atom.Operand }.OfType<string>().Concat(atom.Points ?? ValueSet<string>.Empty).Concat(form.Literals).Distinct().ToList();
            return ValueSet.From(literals.Select(l => new CutPoint(l, CutKind.Literal)).Concat(edges.Boundaries.Select(b => new CutPoint(b, CutKind.Boundary)))
                .Concat(from c in literals.Where(IsExactNumber).Select(NumberOf) from edge in edges.Edges select Edge(c, edge)).OfType<CutPoint>());
        }

        /// <summary>
        /// Where a function's rows say it turns, as cuts of an operand's import (h10): an arithmetic operator's at 0, ±1,
        /// 2^31, 2^53 and the widest decimal (1e308 lies past it), where integer and double arithmetic part, and a repetition's count at the service's length
        /// bound. Data, so a non-monotone function is evaluated where it turns; an image over a range is read coarsely until
        /// a monotone evaluation reads them (TODO.md).
        /// </summary>
        public static ValueSet<CutPoint> Critical(string function)
        {
            var name = function.Split(':')[0];
            decimal[] points = name switch
            {
                "+" or "-" or "*" or "/" or "%" or "MOD" => [0m, 1m, -1m, 2147483648m, -2147483648m, 9007199254740992m, -9007199254740992m, decimal.MaxValue, decimal.MinValue],
                "REPEAT" => [0m, 10_000m],
                _ => [],
            };

            return ValueSet.From(points.Select(p => new CutPoint(JsonValues.Of(p), CutKind.Boundary)));
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
