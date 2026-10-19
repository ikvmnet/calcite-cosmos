using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The JSON types as a set of members, and scalar values as strings tagged with their type, so that a domain is
    /// a <see cref="ValueSet{T}"/> and the type of each member is read off its tag.
    /// </summary>
    /// <remarks>
    /// A number is <c>integer</c> or <c>fraction</c>, as JSON Schema reads it: <c>1.0</c> is an integer. So a schema's
    /// <c>number</c> is two members, and every "is a" between types is a subset test.
    /// </remarks>
    internal static class JsonValues
    {

        public const string StringType = "string";
        public const string IntegerType = "integer";
        public const string FractionType = "fraction";
        public const string BooleanType = "boolean";
        public const string NullType = "null";
        public const string ObjectType = "object";
        public const string ArrayType = "array";

        public static readonly ValueSet<string> AllTypes = ValueSet.Of(StringType, IntegerType, FractionType, BooleanType, NullType, ObjectType, ArrayType);

        public static readonly ValueSet<string> Numbers = ValueSet.Of(IntegerType, FractionType);

        public static readonly ValueSet<string> Scalars = ValueSet.Of(StringType, IntegerType, FractionType, BooleanType, NullType);

        public static ValueSet<string> Types(params string[] members) =>
            ValueSet.From(members.SelectMany(member => member == "number" ? new[] { IntegerType, FractionType } : new[] { member }));

        public static ValueSet<string> AllTypesBut(params string[] members) => ValueSet.From(AllTypes.Except(Types(members)));

        public static string NullValue => "null|";

        public static string Of(bool value) => value ? "boolean|true" : "boolean|false";

        public static string Of(string value) => "string|" + value;

        public static string Of(decimal value)
        {
            // Two spellings of one number are one value: strip the trailing zeros 1.0 carries.
            var normal = value / 1.000000000000000000000000000000000m;
            return (normal == decimal.Truncate(normal) ? IntegerType : FractionType) + "|" + normal.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A number a <see cref="decimal"/> cannot hold — too large, too small, or not finite — as its type and an
        /// approximate spelling, which no exact number shares.
        /// </summary>
        /// <remarks>
        /// Its type is read as JSON Schema reads any number's, so it is an integer or a fraction like any other. Its value
        /// is opaque: no bound, multiple or range reads it, so each keeps it rather than guess where it lies. A number a
        /// decimal can hold is written as one, so that one value has one spelling.
        /// </remarks>
        public static string Of(double value)
        {
            if (double.IsFinite(value))
            {
                try
                {
                    var exact = (decimal)value;
                    if (exact != 0 || value == 0)
                        return Of(exact);
                }
                catch (OverflowException)
                {
                }
            }

            var integral = Math.Floor(value) == value && double.IsInfinity(value) == false;
            return (integral ? IntegerType : FractionType) + "|" + Approximate + value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>What marks a number's spelling as approximate: a character no exact number is written with.</summary>
        const char Approximate = '~';

        public static string TypeOf(string value) => value.Substring(0, value.IndexOf('|'));

        public static ValueSet<string> TypesOf(ValueSet<string> values) => ValueSet.From(values.Select(TypeOf));

        public static bool IsNumber(string value) => TypeOf(value) is IntegerType or FractionType;

        /// <summary>Whether a value is a number whose value can be read, rather than one held approximately.</summary>
        public static bool IsExactNumber(string value) => IsNumber(value) && value[value.IndexOf('|') + 1] != Approximate;

        public static decimal NumberOf(string value) => decimal.Parse(value.Substring(value.IndexOf('|') + 1), CultureInfo.InvariantCulture);

        /// <summary>Reads a number's value, or answers <c>false</c> where it is not a number or is held approximately.</summary>
        public static bool TryNumberOf(string value, out decimal number)
        {
            number = 0;
            if (IsExactNumber(value) == false)
                return false;

            number = NumberOf(value);
            return true;
        }

        /// <summary>The approximate value of a number held approximately, or <c>null</c>.</summary>
        public static double? ApproximateOf(string value) =>
            IsNumber(value) && IsExactNumber(value) == false ? double.Parse(value.Substring(value.IndexOf('|') + 2), CultureInfo.InvariantCulture) : null;

        public static bool IsString(string value) => TypeOf(value) == StringType;

        public static string StringOf(string value) => value.Substring(value.IndexOf('|') + 1);

        /// <summary>A domain less the numbers a lower bound refuses; members that are not exact numbers are kept.</summary>
        public static ValueSet<string> AtLeast(ValueSet<string> values, Bound lower) =>
            ValueSet.From(values.Where(v => TryNumberOf(v, out var x) == false || lower.AdmitsAbove(x)));

        /// <summary>A domain less the numbers an upper bound refuses; members that are not exact numbers are kept.</summary>
        public static ValueSet<string> AtMost(ValueSet<string> values, Bound upper) =>
            ValueSet.From(values.Where(v => TryNumberOf(v, out var x) == false || upper.AdmitsBelow(x)));

        /// <summary>
        /// The least and the most of a domain's numbers, or <c>null</c> where it holds none, or one held approximately,
        /// whose place against a bound is not read.
        /// </summary>
        public static (decimal Least, decimal Most)? NumbersOf(ValueSet<string> values)
        {
            (decimal Least, decimal Most)? span = null;
            foreach (var value in values)
            {
                // Read off the tag in place: a domain of strings, the commonest, is passed over without a copy of each.
                if (value.StartsWith(IntegerType + "|", StringComparison.Ordinal) == false && value.StartsWith(FractionType + "|", StringComparison.Ordinal) == false)
                    continue;

                if (TryNumberOf(value, out var x) == false)
                    return null;

                span = span is var (least, most) ? (Math.Min(least, x), Math.Max(most, x)) : (x, x);
            }

            return span;
        }

        /// <summary>A domain less the strings shorter than a length; members that are not strings are kept.</summary>
        public static ValueSet<string> LengthAtLeast(ValueSet<string> values, long least) =>
            ValueSet.From(values.Where(v => IsString(v) == false || StringOf(v).Length >= least));

        /// <summary>A domain less the strings longer than a length; members that are not strings are kept.</summary>
        public static ValueSet<string> LengthAtMost(ValueSet<string> values, long most) =>
            ValueSet.From(values.Where(v => IsString(v) == false || StringOf(v).Length <= most));

        // ── Characters ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The band of the UTF-16 units of a surrogate pair, U+D800 to U+DFFF: how a character beyond U+FFFF is written,
        /// and a lone surrogate.
        /// </summary>
        public const string SurrogateBand = "surrogate";

        /// <summary>The band of the units from U+E000 to U+FFFF: the private use area and the rest of the basic plane.</summary>
        public const string HighBand = "high";

        /// <summary>
        /// The band of the units from U+0080 to U+D7FF: outside ASCII and below the surrogates. With the other two, every
        /// unit outside ASCII, so a string holding none of the three is ASCII.
        /// </summary>
        public const string NonAsciiBand = "nonascii";

        /// <summary>
        /// The class of the characters the service's <c>TRIM</c> removes beside U+0020, which cuts across the bands: see
        /// <see cref="IsTrimmed"/>.
        /// </summary>
        public const string TrimmedClass = "trimmed";

        /// <summary>
        /// Every class: what a string nothing states the characters of may hold. A string of ASCII characters none of
        /// which the service trims beside U+0020 holds <see cref="NoBand"/>.
        /// </summary>
        public static readonly ValueSet<string> AllBands = ValueSet.Of(SurrogateBand, HighBand, NonAsciiBand, TrimmedClass);

        /// <summary>No class: every character ASCII, and none the service trims beside U+0020.</summary>
        public static readonly ValueSet<string> NoBand = ValueSet.Of<string>();

        /// <summary>
        /// Every set of classes, by a mask of the four, so that reading a value's characters allocates nothing.
        /// </summary>
        static readonly ValueSet<string>[] ClassSets = Enumerable.Range(0, 16).Select(mask => mask == 0 ? NoBand : ValueSet.From(
            new[] { SurrogateBand, HighBand, NonAsciiBand, TrimmedClass }.Where((_, i) => (mask & 1 << i) != 0))).ToArray();

        /// <summary>
        /// The characters the service's <c>TRIM</c>, <c>LTRIM</c> and <c>RTRIM</c> remove beside U+0020, as ranges of code
        /// points: measured on the probe account over every unit of the basic plane but the surrogates, every code point of
        /// plane 1 and U+E0000 to U+E01EF, each first and last in a string, projected and in a <c>WHERE</c>, the three
        /// functions alike (<c>Service.TrimRemovesWhiteSpace</c>). It is the white space of an older Unicode, U+180E
        /// among it and U+FEFF and U+200B not, and nothing past the basic plane. The emulator removes fewer: neither
        /// U+0085, U+00A0, U+180E, U+2007 nor U+202F.
        /// </summary>
        static readonly (int Lo, int Hi)[] Trimmed =
        [
            (0x0009, 0x000D), (0x0085, 0x0085), (0x00A0, 0x00A0), (0x1680, 0x1680), (0x180E, 0x180E),
            (0x2000, 0x200A), (0x2028, 0x2029), (0x202F, 0x202F), (0x205F, 0x205F), (0x3000, 0x3000),
        ];

        /// <summary>
        /// Returns the band a UTF-16 code unit is in, or <c>null</c> for an ASCII one, which is in none.
        /// </summary>
        /// <remarks>
        /// The one place a unit's band is decided: a value's characters, a pattern's ranges, a parameter's at run time
        /// and a row's as it arrives are each read through it, or through <see cref="BandsOf"/>, which reads a range by
        /// the same bounds.
        /// </remarks>
        public static string? BandOf(char unit) =>
            unit < '\u0080' ? null : unit < '\uD800' ? NonAsciiBand : unit <= '\uDFFF' ? SurrogateBand : HighBand;

        /// <summary>
        /// Determines whether the service's <c>TRIM</c> removes a character that Calcite's keeps: one of the measured
        /// ranges, U+0020 being the one both remove.
        /// </summary>
        /// <param name="point">A code point, or a UTF-16 unit.</param>
        public static bool IsTrimmed(int point) => TrimmedWithin(point, point);

        /// <summary>
        /// Returns the classes one UTF-16 unit is in: its band, and the trimmed class where the service trims it.
        /// </summary>
        public static ValueSet<string> ClassesOf(char unit)
        {
            var band = BandOf(unit);
            return Classes(band == SurrogateBand, band == HighBand, band == NonAsciiBand, IsTrimmed(unit));
        }

        /// <summary>
        /// Returns the classes of every character from one to another, each a UTF-16 code unit or a code point: a code
        /// point past U+FFFF is written in surrogates.
        /// </summary>
        /// <param name="lo">The first character.</param>
        /// <param name="hi">The last, at least <paramref name="lo"/>.</param>
        public static ValueSet<string> BandsOf(int lo, int hi) => Classes(
            lo <= 0xDFFF && hi >= 0xD800 || hi > 0xFFFF,
            lo <= 0xFFFF && hi >= 0xE000,
            lo <= 0xD7FF && hi >= 0x0080,
            TrimmedWithin(lo, hi));

        /// <summary>
        /// Returns the classes a text's units are in: <see cref="NoBand"/> where every one is ASCII and none is trimmed.
        /// </summary>
        public static ValueSet<string> BandsOfText(string text)
        {
            bool surrogates = false, high = false, nonAscii = false, trimmed = false;
            foreach (var unit in text)
            {
                var band = BandOf(unit);
                surrogates |= band == SurrogateBand;
                high |= band == HighBand;
                nonAscii |= band == NonAsciiBand;
                trimmed |= IsTrimmed(unit);
            }

            return Classes(surrogates, high, nonAscii, trimmed);
        }

        /// <summary>
        /// Returns the classes a value holds characters of: a string's own, and none for any other value, which holds no
        /// character. An attribute of the value, read off its spelling, as its type is.
        /// </summary>
        public static ValueSet<string> CharactersOf(string value) => IsString(value) ? BandsOfText(StringOf(value)) : NoBand;

        /// <summary>
        /// Returns the classes the strings of a domain hold characters of, together.
        /// </summary>
        public static ValueSet<string> CharactersOf(ValueSet<string> values)
        {
            bool surrogates = false, high = false, nonAscii = false, trimmed = false;
            foreach (var value in values)
            {
                var classes = CharactersOf(value);
                surrogates |= classes.Contains(SurrogateBand);
                high |= classes.Contains(HighBand);
                nonAscii |= classes.Contains(NonAsciiBand);
                trimmed |= classes.Contains(TrimmedClass);
            }

            return Classes(surrogates, high, nonAscii, trimmed);
        }

        /// <summary>
        /// Returns the classes a string written in some classes may hold once it is stored: the same, and the high band
        /// too where it was written with a surrogate, which may have been a lone one.
        /// </summary>
        /// <remarks>
        /// The service's measured behaviour, read as data by one rule of the theory: a lone surrogate written as a JSON
        /// escape is stored as U+FFFD (<c>Service.LoneSurrogateIsStoredAsReplacement</c>, measured on the probe account;
        /// the emulator refuses the write). A surrogate pair is stored as it is. U+FFFD is in the high band alone, and no
        /// trim removes it.
        /// </remarks>
        public static ValueSet<string> Stored(ValueSet<string> written) =>
            written.Contains(SurrogateBand) && written.Contains(HighBand) == false ? ValueSet.From(written.Append(HighBand)) : written;

        static bool TrimmedWithin(int lo, int hi)
        {
            foreach (var (from, to) in Trimmed)
                if (lo <= to && hi >= from)
                    return true;

            return false;
        }

        static ValueSet<string> Classes(bool surrogates, bool high, bool nonAscii, bool trimmed) =>
            ClassSets[(surrogates ? 1 : 0) | (high ? 2 : 0) | (nonAscii ? 4 : 0) | (trimmed ? 8 : 0)];

    }

}
