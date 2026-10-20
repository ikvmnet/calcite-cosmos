using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Where a pair's cells are cut: at every literal the atom and the form compare with, at the edges the atom's reader
    /// names of each numeric one, and at the reader's limits.
    /// </summary>
    /// <remarks>
    /// Which edges a reader has is a host row a rule hands in; this applies it by arithmetic. A cut only parts a cell in
    /// two, so one no reading needs costs a cell and changes no answer.
    /// </remarks>
    internal static class CutPoints
    {

        public static ValueSet<CutPoint> Of(PredicateAtom atom, ServiceForm form, ReaderEdges edges)
        {
            var literals = new[] { atom.Operand }.OfType<string>().Concat(atom.Points ?? ValueSet<string>.Empty).Concat(form.Literals).Distinct().ToList();
            var cuts = literals.Select(l => new CutPoint(l, CutKind.Literal)).Concat(edges.Boundaries.Select(b => new CutPoint(b, CutKind.Boundary))).ToList();
            foreach (var c in literals.Where(JsonValues.IsExactNumber).Select(JsonValues.NumberOf))
                foreach (var edge in edges.Edges)
                    if (Edge(c, edge) is string value)
                        cuts.Add(new CutPoint(value, edge));

            return ValueSet.From(cuts);
        }

        static string? Edge(decimal c, CutKind edge) => edge switch
        {
            CutKind.Below when c > decimal.MinValue + 1 => JsonValues.Of(c - 1),
            CutKind.Above when c < decimal.MaxValue - 1 => JsonValues.Of(c + 1),
            CutKind.Double => Exactly(double.Parse(JsonValues.StringOf(JsonValues.Of(c)), CultureInfo.InvariantCulture)),
            CutKind.Rendering => JsonValues.Of(JsonValues.StringOf(JsonValues.Of(c))),
            _ => null,
        };

        /// <summary>
        /// A double's value exactly, where a decimal holds it, and <c>null</c> where none does: a decimal's own conversion
        /// keeps fifteen digits, and so moves 2^53 by two.
        /// </summary>
        static string? Exactly(double d)
        {
            if (double.IsFinite(d) == false || d == 0)
                return d == 0 ? JsonValues.Of(0m) : null;

            // d = mantissa × 2^exponent; a fraction's least scale is its count of binary places.
            var bits = BitConverter.DoubleToInt64Bits(d);
            var exponent = (int)((bits >> 52) & 0x7FF);
            var mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
            if (exponent == 0)
                exponent = 1;
            else
                mantissa |= 1L << 52;

            for (exponent -= 1075; mantissa != 0 && (mantissa & 1) == 0 && exponent < 0; mantissa >>= 1)
                exponent++;

            var digits = exponent >= 0 ? (BigInteger)mantissa << exponent : mantissa * BigInteger.Pow(5, -exponent);
            if (-exponent > 28 || digits > (BigInteger)decimal.MaxValue)
                return null;

            var parts = decimal.GetBits((decimal)digits);
            return JsonValues.Of(new decimal(parts[0], parts[1], parts[2], d < 0, (byte)Math.Max(0, -exponent)));
        }

    }

}
