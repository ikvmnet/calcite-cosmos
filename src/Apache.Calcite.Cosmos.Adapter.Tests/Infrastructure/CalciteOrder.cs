using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// Calcite's order, as its Java engine defines it: the comparator an answer is judged by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written here, and borrowed from nothing it judges.</b> The CLR runtime that serves as the
    /// differential's oracle departs from Calcite in two places, measured: two or more keys order text
    /// by the host's culture, since its generated comparator reaches a <c>System.String</c> through
    /// <c>IComparable</c>, and a single non-null <c>DOUBLE</c> key puts NaN first. And the adapter's own
    /// comparator, when it has one, is what is being judged. So the semantics are stated again, from
    /// Calcite's source: <c>Functions.compareListItems</c> for one key and <c>Utilities.compare</c> for
    /// several.
    /// </para>
    /// <list type="bullet">
    /// <item><description>Text by UTF-16 code unit, <c>String.compareTo</c>: no collator is installed
    /// anywhere, so none applies.</description></item>
    /// <item><description><c>false</c> before <c>true</c>.</description></item>
    /// <item><description>Exact numbers by value, a <c>DECIMAL</c>'s scale ignored: <c>1.1</c> and
    /// <c>1.10</c> tie.</description></item>
    /// <item><description>A double by <c>Double.compare</c>, NaN greatest. <c>-0.0</c> and
    /// <c>0.0</c> tie under one key, which compares <c>d0 == d1 ? 0 : Double.compare</c>, and order
    /// <c>-0.0</c> first under several, which compare <c>Double.compare</c> alone.</description></item>
    /// <item><description>A UUID as an unsigned 128-bit number, which is the order of its canonical
    /// spelling in one case (CALCITE-7716).</description></item>
    /// <item><description>A <c>VARIANT</c> not at all: <c>VariantValue</c> is not comparable. Two
    /// equal ones tie, and any other pair is refused, so that a statement keyed by one is not judged
    /// rather than judged by an order Calcite does not have.</description></item>
    /// </list>
    /// <para>
    /// Every value Calcite reads as SQL null — an absent path, a JSON null, an object under
    /// <c>JSON_VALUE</c> — is one tie class, placed at the end its key names, whatever the direction.
    /// </para>
    /// </remarks>
    public static class CalciteOrder
    {

        /// <summary>
        /// Compares two rows under a collation: the first key that does not tie decides.
        /// </summary>
        /// <param name="left">A row.</param>
        /// <param name="right">A row.</param>
        /// <param name="keys">The collation, outermost first.</param>
        /// <returns>Negative where <paramref name="left"/> comes first, positive where
        /// <paramref name="right"/> does, and zero where they tie.</returns>
        /// <exception cref="NotSupportedException">A key holds values Calcite defines no order
        /// for.</exception>
        public static int Compare(IReadOnlyList<object?> left, IReadOnlyList<object?> right, IReadOnlyList<CalciteSortKey> keys)
        {
            foreach (var key in keys)
            {
                var c = CompareKey(left[key.Field], right[key.Field], key, keys.Count > 1);
                if (c != 0)
                    return c;
            }

            return 0;
        }

        /// <summary>
        /// Compares two values of one key, with its direction and its absolute null placement.
        /// </summary>
        /// <param name="left">A value, <c>null</c> for SQL null.</param>
        /// <param name="right">A value, <c>null</c> for SQL null.</param>
        /// <param name="key">The key.</param>
        /// <param name="several">Whether the collation has more than one key, which is what decides
        /// whether <c>-0.0</c> and <c>0.0</c> tie.</param>
        /// <returns>The comparison's sign.</returns>
        public static int CompareKey(object? left, object? right, CalciteSortKey key, bool several)
        {
            if (left is null && right is null)
                return 0;

            if (left is null)
                return key.NullsFirst ? -1 : 1;

            if (right is null)
                return key.NullsFirst ? 1 : -1;

            var c = CompareValues(left, right, several);
            return key.Descending ? -c : c;
        }

        /// <summary>
        /// Compares two non-null values of one SQL type, ascending.
        /// </summary>
        /// <param name="left">A value.</param>
        /// <param name="right">A value.</param>
        /// <param name="several">Whether the comparison is one key of several.</param>
        /// <returns>The comparison's sign.</returns>
        /// <exception cref="NotSupportedException">Calcite defines no order for the pair.</exception>
        public static int CompareValues(object left, object right, bool several)
        {
            if (left is string a && right is string b)
                return Math.Sign(string.CompareOrdinal(a, b));

            if (TryBoolean(left, out var p) && TryBoolean(right, out var q))
                return p.CompareTo(q);

            if (TryDouble(left, out var x) && TryDouble(right, out var y))
            {
                // Double.compare: NaN above everything, -0.0 below 0.0. One key asks d0 == d1 first.
                if (several == false && x == y)
                    return 0;

                if (double.IsNaN(x))
                    return double.IsNaN(y) ? 0 : 1;

                if (double.IsNaN(y))
                    return -1;

                if (x == y)
                    return x == 0 ? NegativeZeroFirst(x, y) : 0;

                return x < y ? -1 : 1;
            }

            if (TryExact(left, out var m) && TryExact(right, out var n))
                return m.CompareTo(n);

            if (TryUuid(left, out var u) && TryUuid(right, out var v))
                return Math.Sign(string.CompareOrdinal(u, v));

            if (TryMilliseconds(left, out var s) && TryMilliseconds(right, out var t))
                return s.CompareTo(t);

            if (IsVariant(left) && IsVariant(right) && string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal))
                return 0;

            throw new NotSupportedException($"Calcite defines no order between {Describe(left)} and {Describe(right)}.");
        }

        /// <summary>
        /// <c>Double.compare</c> between two zeros: <c>-0.0</c> first.
        /// </summary>
        static int NegativeZeroFirst(double x, double y)
        {
            var xn = double.IsNegative(x);
            var yn = double.IsNegative(y);
            return xn == yn ? 0 : xn ? -1 : 1;
        }

        /// <summary>
        /// A value's text, equal for two values exactly where they are the same value of their SQL
        /// type: what a row is matched by, as opposed to where it sorts.
        /// </summary>
        /// <remarks>
        /// Exact where the differential's own canonical form is not, on purpose: that one reads every
        /// number through a double, and 2^53 + 1 and 2^63 - 1 are the values a sort here is judged over.
        /// An exact number is written without its scale, since the two plans may box one value at two
        /// scales, and a double by its round-trip spelling.
        /// </remarks>
        public static string Canonical(object? value) => value switch
        {
            null => "null",
            string s => "'" + s + "'",
            _ when TryBoolean(value, out var b) => b ? "true" : "false",
            _ when TryDouble(value, out var d) => "double:" + d.ToString("R", CultureInfo.InvariantCulture),
            _ when TryExact(value, out var e) => "exact:" + e,
            _ when TryUuid(value, out var u) => "uuid:" + u,
            _ => Describe(value),
        };

        static string Describe(object value) => value.GetType().Name + ":" + value;

        static bool TryBoolean(object value, out bool result)
        {
            switch (value)
            {
                case bool b: result = b; return true;
                case java.lang.Boolean jb: result = jb.booleanValue(); return true;
                default: result = false; return false;
            }
        }

        static bool TryDouble(object value, out double result)
        {
            switch (value)
            {
                case double d: result = d; return true;
                case float f: result = f; return true;
                case java.lang.Double jd: result = jd.doubleValue(); return true;
                case java.lang.Float jf: result = jf.floatValue(); return true;
                default: result = 0; return false;
            }
        }

        static bool TryExact(object value, out Exact result)
        {
            switch (value)
            {
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    result = new Exact(new BigInteger(Convert.ToDecimal(value, CultureInfo.InvariantCulture)), 0);
                    return true;
                case decimal m:
                    result = Exact.Parse(m.ToString(CultureInfo.InvariantCulture));
                    return true;
                case BigInteger i:
                    result = new Exact(i, 0);
                    return true;
                case java.math.BigDecimal bd:
                    result = Exact.Parse(bd.toPlainString());
                    return true;
                case java.math.BigInteger bi:
                    result = Exact.Parse(bi.toString());
                    return true;
                case java.lang.Number jn when jn is java.lang.Long or java.lang.Integer or java.lang.Short or java.lang.Byte:
                    result = new Exact(new BigInteger(jn.longValue()), 0);
                    return true;
                default:
                    result = default;
                    return false;
            }
        }

        /// <summary>
        /// A UUID in its canonical lower-case spelling, whose ordinal order is the unsigned 128-bit
        /// order Calcite compares by.
        /// </summary>
        static bool TryUuid(object value, out string result)
        {
            var name = value.GetType().Name;
            if (value is Guid || value is java.util.UUID || name.Contains("Uuid", StringComparison.Ordinal))
            {
                var text = value.ToString() ?? "";
                if (Guid.TryParse(text, out var guid))
                {
                    result = guid.ToString("D").ToLowerInvariant();
                    return true;
                }
            }

            result = "";
            return false;
        }

        /// <summary>
        /// A timestamp as the milliseconds Calcite holds it in: digits below the millisecond are gone
        /// before any comparison sees them.
        /// </summary>
        static bool TryMilliseconds(object value, out long result)
        {
            switch (value)
            {
                case java.sql.Timestamp ts: result = ts.getTime(); return true;
                case java.util.Date date: result = date.getTime(); return true;
                case DateTime dt: result = dt.Ticks / TimeSpan.TicksPerMillisecond; return true;
                case DateTimeOffset dto: result = dto.UtcTicks / TimeSpan.TicksPerMillisecond; return true;
                default: result = 0; return false;
            }
        }

        static bool IsVariant(object value) => value.GetType().Name.Contains("Variant", StringComparison.Ordinal);

        /// <summary>
        /// An exact number: an unscaled integer and a scale, compared by value.
        /// </summary>
        readonly record struct Exact(BigInteger Unscaled, int Scale) : IComparable<Exact>
        {

            public static Exact Parse(string plain)
            {
                var point = plain.IndexOf('.');
                if (point < 0)
                    return new Exact(BigInteger.Parse(plain, CultureInfo.InvariantCulture), 0);

                var digits = plain.Remove(point, 1);
                return new Exact(BigInteger.Parse(digits, CultureInfo.InvariantCulture), plain.Length - point - 1);
            }

            public int CompareTo(Exact other)
            {
                var scale = Math.Max(Scale, other.Scale);
                return (Unscaled * BigInteger.Pow(10, scale - Scale)).CompareTo(other.Unscaled * BigInteger.Pow(10, scale - other.Scale));
            }

            public override string ToString()
            {
                // Scale-free, so that 1.1 and 1.10 are one value as they are one tie.
                var unscaled = Unscaled;
                var scale = Scale;
                while (scale > 0 && unscaled % 10 == 0)
                {
                    unscaled /= 10;
                    scale--;
                }

                var text = BigInteger.Abs(unscaled).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
                var sign = unscaled.Sign < 0 ? "-" : "";
                return scale == 0 ? sign + text : sign + text[..^scale] + "." + text[^scale..];
            }

        }

    }

}
