using System;
using System.Globalization;
using System.Numerics;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The double a number rounds to, and where a double lies against a decimal: what bounds a cell's double slots
    /// (<see cref="PathCell.Numbers"/>). A decimal becomes a double by parsing its invariant text, which rounds once and
    /// correctly; .NET's cast rounds twice, and misses at boundaries such as 9007199254740994.4.
    /// </summary>
    internal static class DoubleBound
    {

        /// <summary>The double a decimal rounds to, correctly.</summary>
        public static double Of(decimal x) => double.Parse(x.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>The double a number in <see cref="JsonValues"/>' spelling rounds to, or is held as.</summary>
        public static double Of(string number) => TryNumberOf(number, out var x) ? Of(x) : ApproximateOf(number) ?? double.NaN;

        /// <summary>The sign of a double less a decimal, exactly: a double's value is its mantissa times a power of two.</summary>
        public static int Compare(double d, decimal x)
        {
            if (double.IsNaN(d))
                throw new ArgumentOutOfRangeException(nameof(d), d, "NaN lies nowhere.");
            if (double.IsInfinity(d))
                return Math.Sign(d);

            var bits = BitConverter.DoubleToInt64Bits(d);
            var exponent = (int)((bits >> 52) & 0x7FF);
            var mantissa = new BigInteger(bits & 0xF_FFFF_FFFF_FFFFL | (exponent == 0 ? 0 : 1L << 52)) * (bits < 0 ? -1 : 1);
            exponent = (exponent == 0 ? 1 : exponent) - 1075;

            // x = digits / 10^scale, so d - x has the sign of mantissa × 2^exponent × 10^scale - digits.
            var parts = decimal.GetBits(x);
            var digits = (new BigInteger((uint)parts[2]) << 64 | new BigInteger((uint)parts[1]) << 32 | (uint)parts[0]) * (x < 0 ? -1 : 1);
            var scaled = mantissa * BigInteger.Pow(10, (parts[3] >> 16) & 0xFF);
            return exponent >= 0 ? (scaled << exponent).CompareTo(digits) : scaled.CompareTo(digits << -exponent);
        }

        /// <summary>
        /// The numbers whose double is this one, its rounding interval: from the midpoint with the double below to the
        /// midpoint with the double above, both ends held whichever way a tie rounds, each rounded outward to a decimal. A
        /// side past a decimal's range is <c>null</c>, no bound; an infinity's and NaN's are both.
        /// </summary>
        public static (decimal? Low, decimal? High) Rounding(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                return (null, null);

            var (m, e) = Exactly(d);
            var (below, eb) = Exactly(Math.BitDecrement(d));
            var (above, ea) = Exactly(Math.BitIncrement(d));
            return (Outward(Midpoint(below, eb, m, e), floor: true), Outward(Midpoint(m, e, above, ea), floor: false));
        }

        /// <summary>A double as its mantissa times a power of two, exactly; an infinity as the largest finite double's.</summary>
        static (BigInteger Mantissa, int Exponent) Exactly(double d)
        {
            if (double.IsInfinity(d))
                d = Math.CopySign(double.MaxValue, d);

            var bits = BitConverter.DoubleToInt64Bits(d);
            var exponent = (int)((bits >> 52) & 0x7FF);
            var mantissa = new BigInteger(bits & 0xF_FFFF_FFFF_FFFFL | (exponent == 0 ? 0 : 1L << 52)) * (bits < 0 ? -1 : 1);
            return (mantissa, (exponent == 0 ? 1 : exponent) - 1075);
        }

        /// <summary>The midpoint of two numbers each a mantissa times a power of two, as one.</summary>
        static (BigInteger Mantissa, int Exponent) Midpoint(BigInteger a, int ea, BigInteger b, int eb)
        {
            var e = Math.Min(ea, eb);
            return ((a << (ea - e)) + (b << (eb - e)), e - 1);
        }

        /// <summary>
        /// A mantissa times a power of two as the decimal nearest it on one side, at the finest scale a decimal holds it at;
        /// <c>null</c> past a decimal's range.
        /// </summary>
        static decimal? Outward((BigInteger Mantissa, int Exponent) x, bool floor)
        {
            var limit = BigInteger.One << 96;
            for (var scale = 28; scale >= 0; scale--)
            {
                var scaled = x.Mantissa * BigInteger.Pow(10, scale);
                BigInteger digits;
                if (x.Exponent >= 0)
                    digits = scaled << x.Exponent;
                else
                {
                    var divisor = BigInteger.One << -x.Exponent;
                    digits = BigInteger.DivRem(scaled, divisor, out var remainder);
                    if (remainder.Sign != 0 && (floor ? remainder.Sign < 0 : remainder.Sign > 0))
                        digits += floor ? -1 : 1;
                }

                if (BigInteger.Abs(digits) < limit)
                {
                    var magnitude = BigInteger.Abs(digits);
                    return new decimal((int)(uint)(magnitude & uint.MaxValue), (int)(uint)((magnitude >> 32) & uint.MaxValue), (int)(uint)((magnitude >> 64) & uint.MaxValue), digits.Sign < 0, (byte)scale);
                }
            }

            return null;
        }

    }

}
