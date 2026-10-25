using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// What a stored scalar renders as through <c>JSON_VALUE</c>, read backwards: which values a text could have come
    /// from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A number renders as Java spells it</b>, which parses back to the number: <c>30</c>, <c>30.0</c>,
    /// <c>1.0E30</c>, and from a <c>BigDecimal</c> <c>1E+30</c>. So only text in that grammar can be a number's
    /// rendering, and only the number with the text's value can render as it — in some spellings and not others.
    /// </para>
    /// <para>
    /// <b>A domain names the number only where a decimal holds the text exactly</b>, at most 28 significant digits. Text
    /// longer than that is still a number's rendering, and stands for a number of either kind, not for a value.
    /// </para>
    /// </remarks>
    internal static class Renderings
    {

        /// <summary>
        /// The magnitude from which a double's unit in the last place is whole: 2^52.
        /// </summary>
        /// <remarks>
        /// A cast to <c>DOUBLE</c> rounds a stored integer and the literal it is compared with by up to half of that unit
        /// each, so from here two whole numbers a unit apart may compare equal — measured, <c>= 2^60</c> keeps a stored
        /// 2^60 + 1 — and no window a unit wide holds around a bound. <c>CosmosRexTranslator.TryNumericCastOperand</c>
        /// reports it as where the conversion rounds from; this is the one place it is written.
        /// </remarks>
        public const long DoubleUnitFrom = 1L << 52;

        /// <summary>
        /// Determines whether text is in the grammar a stored number renders in: digits, a point, an exponent, a sign.
        /// </summary>
        public static bool MayBeNumberText(string text)
        {
            var i = 0;
            if (i < text.Length && text[i] is '+' or '-')
                i++;

            var digits = 0;
            while (i < text.Length && text[i] is >= '0' and <= '9')
            {
                i++;
                digits++;
            }

            if (i < text.Length && text[i] == '.')
            {
                i++;
                while (i < text.Length && text[i] is >= '0' and <= '9')
                {
                    i++;
                    digits++;
                }
            }

            if (digits == 0)
                return false;

            if (i < text.Length && text[i] is 'e' or 'E')
            {
                i++;
                if (i < text.Length && text[i] is '+' or '-')
                    i++;

                var exponent = 0;
                while (i < text.Length && text[i] is >= '0' and <= '9')
                {
                    i++;
                    exponent++;
                }

                if (exponent == 0)
                    return false;
            }

            return i == text.Length;
        }

        /// <summary>
        /// Reads number text as the decimal it spells, where a decimal holds it exactly: never .NET's parse, which reads
        /// <c>1E-50</c> as 0 and a text past 28 places rounded.
        /// </summary>
        public static bool TryExactNumber(string text, out decimal value)
        {
            value = 0;
            return MayBeNumberText(text) && SignificantDigits(text) <= 28 && ExactDecimal(text) is var (digits, exponent) && TryDecimal(digits, exponent, out value);
        }

        /// <summary>The text Calcite renders a boolean as.</summary>
        public static string BooleanText(string value) => value == Of(true) ? "true" : "false";

        /// <summary>
        /// Determines whether a stored number may render as the text: whether the text is a number's rendering with the
        /// value of this one.
        /// </summary>
        public static bool MayRenderAs(string value, string text)
        {
            if (MayBeNumberText(text) == false)
                return false;

            if (TryNumberOf(value, out var exact) && TryExactNumber(text, out var number))
                return exact == number;

            var stored = TryNumberOf(value, out exact) ? (double)exact : ApproximateOf(value);
            return stored is double d && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && d == parsed;
        }

        /// <summary>
        /// Determines whether some number of the kind within the bounds may render as the text.
        /// </summary>
        public static bool MayRenderWithin(string text, bool integral, Bound? lower, Bound? upper)
        {
            if (MayBeNumberText(text) == false)
                return false;

            if (TryExactNumber(text, out var x) == false)
                return true;

            return (integral == false || x == decimal.Truncate(x))
                && (lower is not Bound l || l.AdmitsAbove(x))
                && (upper is not Bound u || u.AdmitsBelow(x));
        }

        /// <summary>
        /// Determines whether a structure may render as the text: an array as <c>[…]</c>, an object as <c>{…}</c>.
        /// </summary>
        public static bool MayRenderStructure(string type, string text) =>
            type == ArrayType ? text.StartsWith('[') : type == ObjectType && text.StartsWith('{');

        /// <summary>Determines whether two scalars may be one value: numbers by their value, anything else by its spelling.</summary>
        public static bool SameValue(string a, string b) => IsNumber(a) && IsNumber(b) ? SameNumber(a, b) : a == b;

        /// <summary>Determines whether two numbers may be one value.</summary>
        public static bool SameNumber(string a, string b)
        {
            if (TryNumberOf(a, out var x) && TryNumberOf(b, out var y))
                return x == y;

            return DoubleOf(a) == DoubleOf(b);
        }

        /// <summary>Determines whether a number may lie within bounds and be of the kind.</summary>
        public static bool NumberWithin(string value, bool integral, Bound? lower, Bound? upper)
        {
            if (TryNumberOf(value, out var x) == false)
                return true;

            return (integral == false || x == decimal.Truncate(x))
                && (lower is not Bound l || l.AdmitsAbove(x))
                && (upper is not Bound u || u.AdmitsBelow(x));
        }

        /// <summary>A number's value as a double, exact or approximate.</summary>
        public static double DoubleOf(string value) =>
            TryNumberOf(value, out var exact) ? (double)exact : ApproximateOf(value) ?? double.NaN;

        // A cast of text to a number, as Calcite converts one: the text trimmed as Java's String.trim trims, every character
        // up to U+0020 off either end, then parsed by Java, which raises on what it cannot parse
        // (CalciteJsonValueMeasurementTests); null is the raise. The grammars are compiled on first use, so that planning,
        // which reads this class, builds none.
        static string Trimmed(string text) => text.Trim(JavaTrims);

        static readonly char[] JavaTrims = [.. Enumerable.Range(0, ' ' + 1).Select(c => (char)c)];

        /// <summary>
        /// Whether Calcite's cast of text to <c>BIGINT</c> reads it as a timestamp rather than parsing it: text that starts
        /// <c>199</c>, untrimmed, and holds a colon (<c>SqlFunctions.toLong</c>), whose value hangs on the local time zone.
        /// </summary>
        public static bool ReadsAsTimestamp(string text) => text.StartsWith("199", StringComparison.Ordinal) && text.Contains(':');

        /// <summary>Java's integer parse, for <c>INTEGER</c> and <c>BIGINT</c>: a whole number within the type, or <c>null</c>.</summary>
        public static decimal? ParseInteger(string text, decimal least, decimal most) =>
            Regex.IsMatch(Trimmed(text), @"^[+-]?[0-9]+$", RegexOptions.CultureInvariant)
            && decimal.TryParse(Trimmed(text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) && whole >= least && whole <= most ? whole : null;

        /// <summary>The double parse of <c>DOUBLE</c>, correctly rounded, which takes no type suffix; or <c>null</c>.</summary>
        public static double? ParseDouble(string text) =>
            Regex.IsMatch(Trimmed(text), @"^[+-]?(NaN|Infinity|(([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?))$", RegexOptions.CultureInvariant)
            && double.TryParse(Trimmed(text), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        /// <summary>
        /// <c>BigDecimal</c>'s parse, for <c>RETURNING DECIMAL</c>: the number in <see cref="JsonValues"/>' spelling, exact
        /// where a decimal holds the text's value and held approximately past one; or <c>null</c>, where Java raises.
        /// </summary>
        /// <remarks>
        /// Never .NET's decimal parse, which reads <c>1e-50</c> as 0 and rounds a text past 28 places, each an exact spelling
        /// of another number. A comparison reads <see cref="DecimalSide"/>, exact at any length.
        /// </remarks>
        public static string? ParseDecimal(string text) => ExactDecimal(text) is not { } x ? null
            : TryDecimal(x.Digits, x.Exponent, out var exact) ? Of(exact)
            : Approximately(double.Parse(Trimmed(text), NumberStyles.Float, CultureInfo.InvariantCulture), x.Exponent >= 0);

        /// <summary>
        /// The sign of the number <c>BigDecimal</c> parses text as, less a decimal, exactly; or <c>null</c>, where Java raises.
        /// </summary>
        public static int? DecimalSide(string text, decimal c)
        {
            if (ExactDecimal(text) is not var (digits, exponent))
                return null;

            // c = u / 10^s. Of one sign, the magnitudes' orders of ten decide, unless they are within one, where the two are
            // scaled to the lesser power, a few digits more than the two hold.
            var parts = decimal.GetBits(c);
            var (u, s) = (new BigInteger((uint)parts[2]) << 64 | new BigInteger((uint)parts[1]) << 32 | (uint)parts[0], (parts[3] >> 16) & 0xFF);
            var sign = c == 0 ? 0 : c < 0 ? -1 : 1;
            if (digits.Sign != sign || digits.IsZero)
                return digits.Sign.CompareTo(sign);

            var (x, n) = (BigInteger.Abs(digits), (long)BigInteger.Abs(digits).ToString(CultureInfo.InvariantCulture).Length);
            var m = (long)u.ToString(CultureInfo.InvariantCulture).Length;
            var magnitude = n - 1 + exponent >= m - s ? 1 : m - 1 - s >= n + exponent ? -1
                : (x * BigInteger.Pow(10, (int)(exponent - Math.Min(exponent, -s)))).CompareTo(u * BigInteger.Pow(10, (int)(-s - Math.Min(exponent, -s))));
            return magnitude * sign;
        }

        /// <summary>
        /// Text in <c>BigDecimal</c>'s grammar as its digits and a power of ten, trailing zeros taken out; <c>null</c> where
        /// Java raises, an exponent or a scale past an <c>int</c> among it.
        /// </summary>
        static (BigInteger Digits, long Exponent)? ExactDecimal(string text)
        {
            var match = Regex.Match(Trimmed(text), @"^(?<sign>[+-]?)(?:(?<whole>[0-9]+)\.?(?<fraction>[0-9]*)|\.(?<fraction>[0-9]+))(?:[eE](?<exponent>[+-]?[0-9]+))?$", RegexOptions.CultureInvariant);
            if (match.Success == false)
                return null;

            var (whole, fraction, exponent) = (match.Groups["whole"].Value, match.Groups["fraction"].Value, 0L);
            if (match.Groups["exponent"].Success && long.TryParse(match.Groups["exponent"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) == false
                || exponent is < int.MinValue or > int.MaxValue || fraction.Length - exponent is < int.MinValue or > int.MaxValue)
                return null;

            var digits = BigInteger.Parse("0" + whole + fraction, CultureInfo.InvariantCulture) * (match.Groups["sign"].Value == "-" ? -1 : 1);
            exponent -= fraction.Length;
            while (digits.IsZero == false && (digits % 10).IsZero)
                (digits, exponent) = (digits / 10, exponent + 1);

            return (digits, digits.IsZero ? 0 : exponent);
        }

        /// <summary>The decimal that is exactly digits × 10^exponent, or <c>false</c> where none is: past 96 bits or 28 places.</summary>
        static bool TryDecimal(BigInteger digits, long exponent, out decimal exact)
        {
            exact = 0;
            if (exponent > 28 && digits.IsZero == false || exponent < -28)
                return false;

            var unscaled = BigInteger.Abs(digits) * BigInteger.Pow(10, (int)Math.Max(exponent, 0));
            if (unscaled.GetBitLength() > 96)
                return false;

            exact = new decimal((int)(uint)(unscaled & uint.MaxValue), (int)(uint)(unscaled >> 32 & uint.MaxValue), (int)(uint)(unscaled >> 64), digits.Sign < 0, (byte)Math.Max(0, -exponent));
            return true;
        }

        static int SignificantDigits(string text)
        {
            var mantissa = text;
            var e = mantissa.IndexOfAny(new[] { 'e', 'E' });
            if (e >= 0)
                mantissa = mantissa.Substring(0, e);

            var digits = mantissa.Where(char.IsAsciiDigit).SkipWhile(c => c == '0').Count();
            return Math.Max(digits, 1);
        }

    }

}
