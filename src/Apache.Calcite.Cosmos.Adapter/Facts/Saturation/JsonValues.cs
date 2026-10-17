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
        /// Both bands: what a string nothing states the characters of may hold units of. A unit below U+D800 is in
        /// neither, and a string all of whose units are holds <see cref="NoBand"/>.
        /// </summary>
        public static readonly ValueSet<string> AllBands = ValueSet.Of(SurrogateBand, HighBand);

        /// <summary>Neither band: every unit below U+D800.</summary>
        public static readonly ValueSet<string> NoBand = ValueSet.Of<string>();

        static readonly ValueSet<string> Surrogates = ValueSet.Of(SurrogateBand);

        static readonly ValueSet<string> High = ValueSet.Of(HighBand);

        /// <summary>
        /// Returns the band a UTF-16 code unit is in, or <c>null</c> below U+D800, where it is in neither.
        /// </summary>
        /// <remarks>
        /// The one place a unit's band is decided: a value's characters, a pattern's ranges, a parameter's at run time
        /// and a row's as it arrives are each read through it.
        /// </remarks>
        public static string? BandOf(char unit) =>
            unit < '\uD800' ? null : unit <= '\uDFFF' ? SurrogateBand : HighBand;

        /// <summary>
        /// Returns the bands of every character from one to another, each a UTF-16 code unit or a code point: a code
        /// point past U+FFFF is written in surrogates.
        /// </summary>
        /// <param name="lo">The first character.</param>
        /// <param name="hi">The last, at least <paramref name="lo"/>.</param>
        public static ValueSet<string> BandsOf(int lo, int hi)
        {
            var surrogates = lo <= 0xDFFF && hi >= 0xD800 || hi > 0xFFFF;
            var high = lo <= 0xFFFF && hi >= 0xE000;
            return Bands(surrogates, high);
        }

        /// <summary>
        /// Returns the bands a text's units are in: <see cref="NoBand"/> where every one is below U+D800.
        /// </summary>
        public static ValueSet<string> BandsOfText(string text)
        {
            bool surrogates = false, high = false;
            foreach (var unit in text)
            {
                var band = BandOf(unit);
                surrogates |= band == SurrogateBand;
                high |= band == HighBand;
            }

            return Bands(surrogates, high);
        }

        /// <summary>
        /// Returns the bands a value holds units of: a string's own, and none for any other value, which holds no
        /// character. An attribute of the value, read off its spelling, as its type is.
        /// </summary>
        public static ValueSet<string> CharactersOf(string value) => IsString(value) ? BandsOfText(StringOf(value)) : NoBand;

        /// <summary>
        /// Returns the bands the strings of a domain hold units of, together.
        /// </summary>
        public static ValueSet<string> CharactersOf(ValueSet<string> values)
        {
            bool surrogates = false, high = false;
            foreach (var value in values)
            {
                var bands = CharactersOf(value);
                surrogates |= bands.Contains(SurrogateBand);
                high |= bands.Contains(HighBand);
            }

            return Bands(surrogates, high);
        }

        static ValueSet<string> Bands(bool surrogates, bool high) =>
            surrogates ? high ? AllBands : Surrogates : high ? High : NoBand;

    }

}
