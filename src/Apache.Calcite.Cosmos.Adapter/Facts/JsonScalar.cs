using System;
using System.Globalization;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A JSON scalar — null, a boolean, a string or a number — with JSON's own equality.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a claim's value is, and why it is not an <see cref="object"/>.</b> A value reaches a claim
    /// from a schema and from a query, and each side boxes a number its own way: a schema's <c>1.5</c>
    /// was a <see cref="double"/>, a query's an exact <see cref="decimal"/>, and two boxes of one number
    /// were unequal. The theory then read a value as excluding a domain that held it, and a predicate
    /// that should have matched was rewritten to <c>FALSE</c> (#198). JSON has one number type, so this
    /// has one: a number is compared by its value, whatever spelled it.
    /// </para>
    /// <para>
    /// <b>A number is held exactly where it can be.</b> As a <see cref="decimal"/>, which carries every
    /// <see cref="long"/> and every number a schema or a query writes in decimal notation without
    /// rounding — a <see cref="double"/> would make two distinct large integers one key, and a fact about
    /// one would be read as a fact about the other. A number a decimal cannot hold, one too large or too
    /// small, is held as a <see cref="double"/>, and equals only another such number.
    /// </para>
    /// <para>
    /// <b>Each side converts once, at its boundary:</b> the schema reader from the JSON it walks, and the
    /// fact extractor from a query's literal. Nothing inside the theory sees a CLR box.
    /// </para>
    /// <para>
    /// Its <c>default</c> is <see cref="Null"/>.
    /// </para>
    /// </remarks>
    public readonly struct JsonScalar : IEquatable<JsonScalar>
    {

        enum Kind : byte
        {
            Null,
            Boolean,
            String,
            Decimal,
            Double,
        }

        /// <summary>
        /// The JSON null. The same as <c>default</c>.
        /// </summary>
        public static readonly JsonScalar Null = default;

        readonly Kind _kind;
        readonly bool _boolean;
        readonly decimal _decimal;
        readonly double _double;
        readonly string? _string;

        JsonScalar(Kind kind, bool boolean = false, decimal number = 0, double approximate = 0, string? text = null)
        {
            _kind = kind;
            _boolean = boolean;
            _decimal = number;
            _double = approximate;
            _string = text;
        }

        /// <summary>
        /// Returns a boolean.
        /// </summary>
        public static JsonScalar From(bool value) => new(Kind.Boolean, boolean: value);

        /// <summary>
        /// Returns a string, or <see cref="Null"/> for <c>null</c>.
        /// </summary>
        public static JsonScalar From(string? value) => value is null ? Null : new(Kind.String, text: value);

        /// <summary>
        /// Returns a number.
        /// </summary>
        public static JsonScalar From(decimal value) => new(Kind.Decimal, number: value);

        /// <summary>
        /// Returns a number.
        /// </summary>
        public static JsonScalar From(long value) => new(Kind.Decimal, number: value);

        /// <summary>
        /// Returns a number, held exactly where a decimal can hold it.
        /// </summary>
        /// <remarks>
        /// A <see cref="double"/> is already a rounding of whatever was written, so converting it is no
        /// further loss: <c>1.5</c> becomes <c>1.5m</c>, and equals the exact <c>1.5</c> a query writes.
        /// One a decimal cannot represent — too large, too small, or not finite — stays a double.
        /// </remarks>
        public static JsonScalar From(double value)
        {
            if (double.IsFinite(value) && TryDecimal(value, out var exact))
                return new(Kind.Decimal, number: exact);

            return new(Kind.Double, approximate: value);
        }

        static bool TryDecimal(double value, out decimal exact)
        {
            try
            {
                exact = (decimal)value;
                return exact != 0 || value == 0;
            }
            catch (OverflowException)
            {
                exact = 0;
                return false;
            }
        }

        /// <summary>
        /// Converts a CLR value as a host or an engine hands one over, or answers <c>false</c> where it
        /// is not a JSON scalar.
        /// </summary>
        /// <param name="value">A string, a boolean, a number of any CLR numeric type, or <c>null</c>.</param>
        /// <param name="scalar">The scalar.</param>
        /// <returns><c>true</c> where the value is a JSON scalar.</returns>
        public static bool TryFrom(object? value, out JsonScalar scalar)
        {
            switch (value)
            {
                case null:
                    scalar = Null;
                    return true;
                case string s:
                    scalar = From(s);
                    return true;
                case bool b:
                    scalar = From(b);
                    return true;
                case sbyte or byte or short or ushort or int or uint or long:
                    scalar = From(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    return true;
                case ulong u:
                    scalar = From((decimal)u);
                    return true;
                case decimal m:
                    scalar = From(m);
                    return true;
                case float f:
                    scalar = From((double)f);
                    return true;
                case double d:
                    scalar = From(d);
                    return true;
                default:
                    scalar = Null;
                    return false;
            }
        }

        /// <summary>
        /// Gets the scalar's JSON type: <see cref="JsonType.Integer"/> for a whole number, as the
        /// service's own type predicates answer.
        /// </summary>
        public JsonType Type => _kind switch
        {
            Kind.Null => JsonType.Null,
            Kind.Boolean => JsonType.Boolean,
            Kind.String => JsonType.String,
            Kind.Decimal => decimal.Truncate(_decimal) == _decimal ? JsonType.Integer : JsonType.Number,
            _ => Math.Floor(_double) == _double && double.IsInfinity(_double) == false ? JsonType.Integer : JsonType.Number,
        };

        /// <summary>
        /// Gets whether this is the JSON null.
        /// </summary>
        public bool IsNull => _kind == Kind.Null;

        /// <summary>
        /// Returns the value as a CLR value: <c>null</c>, a <see cref="bool"/>, a <see cref="string"/>, a
        /// <see cref="decimal"/>, or a <see cref="double"/> for a number a decimal cannot hold.
        /// </summary>
        /// <returns>The value.</returns>
        public object? ToClrValue() => _kind switch
        {
            Kind.Null => null,
            Kind.Boolean => _boolean,
            Kind.String => _string,
            Kind.Decimal => _decimal,
            _ => _double,
        };

        /// <inheritdoc />
        public bool Equals(JsonScalar other)
        {
            if (_kind != other._kind)
                return false;

            return _kind switch
            {
                Kind.Null => true,
                Kind.Boolean => _boolean == other._boolean,
                Kind.String => string.Equals(_string, other._string, StringComparison.Ordinal),

                // Decimal equality is by value: 1m and 1.0m are equal, and hash alike.
                Kind.Decimal => _decimal == other._decimal,
                _ => _double.Equals(other._double),
            };
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is JsonScalar other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => _kind switch
        {
            Kind.Null => 0,
            Kind.Boolean => _boolean ? 1 : 2,
            Kind.String => StringComparer.Ordinal.GetHashCode(_string!),
            Kind.Decimal => _decimal.GetHashCode(),
            _ => _double.GetHashCode(),
        };

        /// <summary>A string, or the JSON null for <c>null</c>.</summary>
        public static implicit operator JsonScalar(string? value) => From(value);

        /// <summary>A boolean.</summary>
        public static implicit operator JsonScalar(bool value) => From(value);

        /// <summary>A number.</summary>
        public static implicit operator JsonScalar(long value) => From(value);

        /// <summary>A number.</summary>
        public static implicit operator JsonScalar(decimal value) => From(value);

        /// <summary>A number, held exactly where a decimal can hold it.</summary>
        public static implicit operator JsonScalar(double value) => From(value);

        /// <summary>
        /// Determines whether two scalars are the same JSON value.
        /// </summary>
        public static bool operator ==(JsonScalar left, JsonScalar right) => left.Equals(right);

        /// <summary>
        /// Determines whether two scalars are different JSON values.
        /// </summary>
        public static bool operator !=(JsonScalar left, JsonScalar right) => left.Equals(right) == false;

        /// <summary>
        /// Renders the value as JSON would write it, for diagnostics.
        /// </summary>
        public override string ToString() => _kind switch
        {
            Kind.Null => "null",
            Kind.Boolean => _boolean ? "true" : "false",
            Kind.String => "\"" + _string + "\"",
            Kind.Decimal => _decimal.ToString(CultureInfo.InvariantCulture),
            _ => _double.ToString("R", CultureInfo.InvariantCulture),
        };

    }

}
