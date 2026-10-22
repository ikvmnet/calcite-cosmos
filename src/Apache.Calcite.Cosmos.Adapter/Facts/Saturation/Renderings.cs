using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        /// Reads number text as the decimal it spells, where a decimal holds it exactly.
        /// </summary>
        public static bool TryExactNumber(string text, out decimal value)
        {
            value = 0;
            if (MayBeNumberText(text) == false || SignificantDigits(text) > 28)
                return false;

            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
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
        /// <c>BigDecimal</c>'s parse, for <c>RETURNING DECIMAL</c>: the number in <see cref="JsonValues"/>' spelling, held
        /// approximately past a decimal; or <c>null</c>.
        /// </summary>
        public static string? ParseDecimal(string text) => Regex.IsMatch(Trimmed(text), @"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant) == false ? null
            : decimal.TryParse(Trimmed(text), NumberStyles.Float, CultureInfo.InvariantCulture, out var exact) ? Of(exact)
            : Of(double.Parse(Trimmed(text), NumberStyles.Float, CultureInfo.InvariantCulture));

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
