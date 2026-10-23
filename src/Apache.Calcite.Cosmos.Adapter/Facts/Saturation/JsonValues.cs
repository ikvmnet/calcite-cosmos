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

        /// <summary>
        /// The fewest and the most UTF-16 units of a domain's strings, or <c>null</c> where it holds none.
        /// </summary>
        public static (long Least, long Most)? LengthsOf(ValueSet<string> values)
        {
            (long Least, long Most)? span = null;
            foreach (var value in values)
                if (IsString(value))
                {
                    long length = value.Length - value.IndexOf('|') - 1;
                    span = span is var (least, most) ? (Math.Min(least, length), Math.Max(most, length)) : (length, length);
                }

            return span;
        }

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
        /// The class of U+0000, which the service's <c>REPLACE</c> and <c>CONTAINS</c> read a string only up to: see
        /// <see cref="JsonCharacterBand.Nul"/>.
        /// </summary>
        public const string NulClass = "nul";

        /// <summary>
        /// The classes, each once: the adapter's flag for it and the theory's member. Every other spelling of a class — a
        /// set of members, a mask of flags, the classes a pattern leaves out — is read off this table, so that a class is
        /// added here and nowhere else.
        /// </summary>
        public static readonly IReadOnlyList<(JsonCharacterBand Flag, string Member)> CharacterClasses =
        [
            (JsonCharacterBand.Surrogates, SurrogateBand),
            (JsonCharacterBand.HighBmp, HighBand),
            (JsonCharacterBand.NonAscii, NonAsciiBand),
            (JsonCharacterBand.Trimmed, TrimmedClass),
            (JsonCharacterBand.Nul, NulClass),
        ];

        /// <summary>
        /// Every set of classes, by its mask of flags, so that reading a value's characters allocates nothing.
        /// </summary>
        static readonly ValueSet<string>[] ClassSets = Enumerable.Range(0, (int)JsonCharacterBand.Any + 1)
            .Select(mask => ValueSet.From(CharacterClasses.Where(c => (mask & (int)c.Flag) != 0).Select(c => c.Member)))
            .ToArray();

        /// <summary>
        /// Every class: what a string nothing states the characters of may hold. A string of ASCII characters other than
        /// U+0000, none of which the service trims beside U+0020, holds <see cref="NoBand"/>.
        /// </summary>
        public static readonly ValueSet<string> AllBands = ClassSet(JsonCharacterBand.Any);

        /// <summary>No class: every character ASCII other than U+0000, and none the service trims beside U+0020.</summary>
        public static readonly ValueSet<string> NoBand = ClassSet(JsonCharacterBand.None);

        /// <summary>
        /// Returns the theory's members for a mask of the adapter's flags.
        /// </summary>
        public static ValueSet<string> ClassSet(JsonCharacterBand classes) => ClassSets[(int)(classes & JsonCharacterBand.Any)];

        /// <summary>
        /// Returns the adapter's flags for a set of the theory's members.
        /// </summary>
        public static JsonCharacterBand ClassMask(ValueSet<string> members)
        {
            var mask = JsonCharacterBand.None;
            foreach (var (flag, member) in CharacterClasses)
                if (members.Contains(member))
                    mask |= flag;

            return mask;
        }

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
        /// Returns the classes one UTF-16 code unit is in, as a mask: its band — none for an ASCII unit — the trimmed
        /// class where the service trims it, and the class of U+0000.
        /// </summary>
        /// <remarks>
        /// The one place a unit's classes are decided: a value's characters, a pattern's ranges, a parameter's at run time
        /// and a row's as it arrives are each read through it, or through <see cref="MaskOf(int, int)"/>, which reads a
        /// range by the same bounds.
        /// </remarks>
        public static JsonCharacterBand MaskOf(char unit) =>
            (unit < '\u0080' ? JsonCharacterBand.None : unit < '\uD800' ? JsonCharacterBand.NonAscii : unit <= '\uDFFF' ? JsonCharacterBand.Surrogates : JsonCharacterBand.HighBmp)
            | (IsTrimmed(unit) ? JsonCharacterBand.Trimmed : JsonCharacterBand.None)
            | (unit == '\0' ? JsonCharacterBand.Nul : JsonCharacterBand.None);

        /// <summary>
        /// Returns the classes of every character from one to another, each a UTF-16 code unit or a code point, as a mask:
        /// a code point past U+FFFF is written in surrogates.
        /// </summary>
        /// <param name="lo">The first character.</param>
        /// <param name="hi">The last, at least <paramref name="lo"/>.</param>
        public static JsonCharacterBand MaskOf(int lo, int hi) =>
            (lo <= 0xDFFF && hi >= 0xD800 || hi > 0xFFFF ? JsonCharacterBand.Surrogates : JsonCharacterBand.None)
            | (lo <= 0xFFFF && hi >= 0xE000 ? JsonCharacterBand.HighBmp : JsonCharacterBand.None)
            | (lo <= 0xD7FF && hi >= 0x0080 ? JsonCharacterBand.NonAscii : JsonCharacterBand.None)
            | (TrimmedWithin(lo, hi) ? JsonCharacterBand.Trimmed : JsonCharacterBand.None)
            | (lo <= 0 && hi >= 0 ? JsonCharacterBand.Nul : JsonCharacterBand.None);

        /// <summary>
        /// Returns the classes a text's units are in, as a mask: <see cref="JsonCharacterBand.None"/> where every one is
        /// ASCII and none is trimmed.
        /// </summary>
        public static JsonCharacterBand MaskOf(string text)
        {
            var mask = JsonCharacterBand.None;
            foreach (var unit in text)
                mask |= MaskOf(unit);

            return mask;
        }

        /// <summary>
        /// Determines whether the service's <c>TRIM</c> removes a character that Calcite's keeps: one of the measured
        /// ranges, U+0020 being the one both remove.
        /// </summary>
        /// <param name="point">A code point, or a UTF-16 unit.</param>
        public static bool IsTrimmed(int point) => TrimmedWithin(point, point);

        /// <summary>
        /// Returns the classes a text's units are in: <see cref="NoBand"/> where every one is ASCII and none is trimmed.
        /// </summary>
        public static ValueSet<string> BandsOfText(string text) => ClassSet(MaskOf(text));

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
            var mask = JsonCharacterBand.None;
            foreach (var value in values)
                if (IsString(value))
                    mask |= MaskOf(StringOf(value));

            return ClassSet(mask);
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

        /// <summary>
        /// Determines whether a text holds no unpaired surrogate, which the SDK's encoder refuses to send
        /// (<c>Service.LoneSurrogateParameterIsNeverSent</c>): the twin of <c>Client.CosmosEncodable.IsEncodable</c>, which
        /// nothing under <c>Facts/</c> may reference, read unit by unit as it is.
        /// </summary>
        public static bool IsEncodable(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
                else if (char.IsSurrogate(text[i]))
                    return false;
            }

            return true;
        }

        static bool TrimmedWithin(int lo, int hi)
        {
            foreach (var (from, to) in Trimmed)
                if (lo <= to && hi >= from)
                    return true;

            return false;
        }

    }

}
