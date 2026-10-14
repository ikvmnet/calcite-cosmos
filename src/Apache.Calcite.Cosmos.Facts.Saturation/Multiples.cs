using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>Arithmetic on <c>multipleOf</c> and on integer ranges.</summary>
    public static class Multiples
    {

        /// <summary>
        /// The least common multiple of two positive decimals: what a number that is a multiple of both is a multiple
        /// of, and so how two <c>multipleOf</c> claims about one path merge.
        /// </summary>
        public static decimal Lcm(decimal a, decimal b)
        {
            var scale = Math.Max(Scale(a), Scale(b));
            var factor = BigInteger.Pow(10, scale);
            var x = ToBig(a, scale);
            var y = ToBig(b, scale);
            var lcm = x / BigInteger.GreatestCommonDivisor(x, y) * y;
            return (decimal)lcm / (decimal)factor;
        }

        /// <summary>The greatest common divisor of two positive decimals: what a multiple of one or the other is a multiple of.</summary>
        public static decimal Gcd(decimal a, decimal b)
        {
            var scale = Math.Max(Scale(a), Scale(b));
            var factor = BigInteger.Pow(10, scale);
            return (decimal)BigInteger.GreatestCommonDivisor(ToBig(a, scale), ToBig(b, scale)) / (decimal)factor;
        }

        public static bool IsMultiple(decimal x, decimal m) => m != 0 && decimal.Remainder(x, m) == 0;

        /// <summary>A domain less the numbers that are not multiples; members that are not numbers are kept.</summary>
        public static ValueSet<string> Only(ValueSet<string> values, decimal m) =>
            ValueSet.From(values.Where(v => IsNumber(v) == false || IsMultiple(NumberOf(v), m)));

        /// <summary>Whether no multiple of <paramref name="m"/> lies between two bounds.</summary>
        public static bool NoneBetween(decimal m, Bound lower, Bound upper)
        {
            if (m <= 0)
                return false;

            var k = Math.Ceiling(lower.Value / m);
            var first = k * m;
            if (first == lower.Value && lower.Open)
                first += m;

            return upper.AdmitsBelow(first) == false;
        }

        /// <summary>The integers between two bounds, or <c>null</c> where there are more than 4,096.</summary>
        public static List<decimal>? IntegersBetween(Bound lower, Bound upper)
        {
            var least = Math.Ceiling(lower.Value);
            if (least == lower.Value && lower.Open)
                least += 1;

            var most = Math.Floor(upper.Value);
            if (most == upper.Value && upper.Open)
                most -= 1;

            if (most - least > 4_096)
                return null;

            var list = new List<decimal>();
            for (var x = least; x <= most; x++)
                list.Add(x);

            return list;
        }

        static int Scale(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

        static BigInteger ToBig(decimal value, int scale)
        {
            var scaled = value;
            for (var i = 0; i < scale; i++)
                scaled *= 10;

            return new BigInteger(decimal.Truncate(scaled));
        }

    }

}
