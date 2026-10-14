using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A path into a document: a class of terms built from <c>Root</c>, <c>Prop</c>, <c>Index</c> and <c>Each</c>.
    /// </summary>
    internal readonly record struct JPath(ClassId Id) : ISort<JPath>
    {

        public static JPath FromId(ClassId id) => new(id);

    }

    /// <summary>
    /// A bound on a number: the value, and whether the bound itself is excluded.
    /// </summary>
    internal readonly record struct Bound(decimal Value, bool Open)
    {

        public static Bound AtLeast(decimal value) => new(value, false);

        public static Bound Above(decimal value) => new(value, true);

        public static Bound AtMost(decimal value) => new(value, false);

        public static Bound Below(decimal value) => new(value, true);

        /// <summary>Whether a number satisfies this as a lower bound.</summary>
        public bool AdmitsAbove(decimal x) => x > Value || x == Value && Open == false;

        /// <summary>Whether a number satisfies this as an upper bound.</summary>
        public bool AdmitsBelow(decimal x) => x < Value || x == Value && Open == false;

        /// <summary>The stricter of two lower bounds: the larger, and the open one where they are equal.</summary>
        public static Bound StricterLower(Bound a, Bound b) =>
            a.Value > b.Value ? a : b.Value > a.Value ? b : new(a.Value, a.Open || b.Open);

        /// <summary>The stricter of two upper bounds: the smaller, and the open one where they are equal.</summary>
        public static Bound StricterUpper(Bound a, Bound b) =>
            a.Value < b.Value ? a : b.Value < a.Value ? b : new(a.Value, a.Open || b.Open);

        /// <summary>The weaker of two lower bounds: what holds whichever of two cases does.</summary>
        public static Bound WeakerLower(Bound a, Bound b) =>
            a.Value < b.Value ? a : b.Value < a.Value ? b : new(a.Value, a.Open && b.Open);

        /// <summary>The weaker of two upper bounds: what holds whichever of two cases does.</summary>
        public static Bound WeakerUpper(Bound a, Bound b) =>
            a.Value > b.Value ? a : b.Value > a.Value ? b : new(a.Value, a.Open && b.Open);

        /// <summary>Whether no number lies between a lower and an upper bound.</summary>
        public static bool NoNumberBetween(Bound lower, Bound upper) =>
            lower.Value > upper.Value || lower.Value == upper.Value && (lower.Open || upper.Open);

        /// <summary>Whether no integer lies between a lower and an upper bound.</summary>
        public static bool NoIntegerBetween(Bound lower, Bound upper)
        {
            var least = Math.Ceiling(lower.Value);
            if (least == lower.Value && lower.Open)
                least += 1;

            var most = Math.Floor(upper.Value);
            if (most == upper.Value && upper.Open)
                most -= 1;

            return least > most;
        }

        public override string ToString() => (Open ? "open " : "") + Value.ToString(CultureInfo.InvariantCulture);

    }

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

    }

}
