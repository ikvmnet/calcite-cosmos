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

    }

}
