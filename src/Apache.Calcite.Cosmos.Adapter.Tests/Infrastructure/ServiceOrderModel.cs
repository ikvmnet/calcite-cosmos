using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// The order a pushed sort comes back in, modelled from what was measured of the service and the
    /// SDK: two comparators, not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Within a physical partition the service orders stored values.</b> By JSON type first,
    /// <c>undefined &lt; null &lt; false &lt; true &lt; number &lt; string &lt; array &lt; object</c>;
    /// numbers by their exact value, int64 exactly; strings by code point, which is UTF-8 byte order,
    /// with no collation and no normalisation; arrays and objects by no structure anyone has found, so
    /// two of them are held apart in an order this model does not claim to know. <c>DESC</c> is the
    /// exact reverse. Measured on the emulator and on Azure alike.
    /// </para>
    /// <para>
    /// <b>Across physical partitions the SDK merges the partitions' streams</b>, comparing each
    /// stream's next row by <c>ItemComparer</c>: the same type order, numbers by the value the response
    /// printed, and strings by <c>String.CompareOrdinal</c>, which is UTF-16 code unit order and
    /// Calcite's. A tie goes to the lower feed range, in both directions. So a stream is one order where
    /// a container has one partition, and where it has several it can be neither: each run is code point
    /// order and the merge between them is UTF-16, measured on Azure over two partitions holding
    /// characters from both sides of U+D800.
    /// </para>
    /// <para>
    /// <b>What the service prints is not always what it stored.</b> A double is printed with 17
    /// significant digits, so 2^63 stored as a double comes back as <c>9223372036854775800</c> on the
    /// emulator and <c>9.223372036854776E+18</c> on Azure, and a partition orders it by what it stored
    /// while the merge and Calcite see what was printed. <c>-0.0</c> is stored as <c>0</c>.
    /// </para>
    /// </remarks>
    public static class ServiceOrderModel
    {

        /// <summary>
        /// A stored value's JSON type, in the service's type order.
        /// </summary>
        public enum Kind
        {
            Absent,
            Null,
            Boolean,
            Number,
            String,
            Array,
            Object,
        }

        /// <summary>
        /// Which service printed a response, which decides how a large double reads back.
        /// </summary>
        public enum Printer
        {
            Emulator,
            Azure,
        }

        /// <summary>
        /// A value as the service stores it.
        /// </summary>
        /// <param name="Kind">Its JSON type.</param>
        /// <param name="Boolean">A boolean's value.</param>
        /// <param name="Number">A number's exact value, as stored.</param>
        /// <param name="IsDouble">Whether a number is stored as a double rather than an int64.</param>
        /// <param name="Text">A string's value, or an array's or an object's JSON.</param>
        public sealed record Value(Kind Kind, bool Boolean = false, decimal Number = 0, bool IsDouble = false, string Text = "")
        {

            public static readonly Value Absent = new(Kind.Absent);

            public static readonly Value Null = new(Kind.Null);

            public static Value Of(bool value) => new(Kind.Boolean, Boolean: value);

            public static Value Of(long value) => new(Kind.Number, Number: value);

            /// <summary>A double, stored as one: <c>-0.0</c> is stored as <c>0</c>.</summary>
            public static Value OfDouble(double value) => new(Kind.Number, Number: Exactly(value), IsDouble: true);

            public static Value Of(string value) => new(Kind.String, Text: value);

            public static Value Array(string json) => new(Kind.Array, Text: json);

            public static Value Object(string json) => new(Kind.Object, Text: json);

            /// <summary>
            /// The number a response prints for this one: an int64 exactly, a double to 17
            /// significant digits, as the given service writes them.
            /// </summary>
            public decimal ReadBack(Printer printer)
            {
                if (Kind != Kind.Number || IsDouble == false)
                    return Number;

                var d = (double)Number;
                if (Math.Abs(d) < 9007199254740992d)
                    return Number;

                // 17 significant digits: 2^63 is 9223372036854775800 from the emulator. Azure writes the
                // shortest round trip, 9.223372036854776E+18, which parses back to the double itself.
                return printer == Printer.Emulator
                    ? decimal.Parse(d.ToString("E16", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture)
                    : Exactly(double.Parse(d.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
            }

            /// <summary>
            /// A double's exact value. A cast would round it to 15 significant digits, which is
            /// exactly the difference between 2^63 and the integers beside it.
            /// </summary>
            static decimal Exactly(double value) => value == Math.Floor(value)
                ? (decimal)new System.Numerics.BigInteger(value)
                : decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

            public override string ToString() => Kind switch
            {
                Kind.Absent => "absent",
                Kind.Null => "null",
                Kind.Boolean => Boolean ? "true" : "false",
                Kind.Number => Number.ToString(CultureInfo.InvariantCulture) + (IsDouble ? "d" : ""),
                Kind.String => OrderAllowability.Escape(Text),
                _ => Text,
            };

        }

        /// <summary>
        /// Compares two stored values as one physical partition orders them, ascending.
        /// </summary>
        /// <returns>The sign, or <c>null</c> for two values held apart in an order nothing measured
        /// states: two distinct arrays, or two distinct objects.</returns>
        public static int? CompareInPartition(Value left, Value right) => Compare(left, right, l => l.Number, CompareCodePoints);

        /// <summary>
        /// Compares two values as the SDK's merge does, ascending: by what was printed, strings by
        /// UTF-16 code unit.
        /// </summary>
        /// <returns>The sign, or <c>null</c> as for <see cref="CompareInPartition"/>.</returns>
        public static int? CompareInMerge(Value left, Value right, Printer printer) => Compare(left, right, l => l.ReadBack(printer), string.CompareOrdinal);

        static int? Compare(Value left, Value right, Func<Value, decimal> number, Func<string, string, int> text)
        {
            if (left.Kind != right.Kind)
                return left.Kind.CompareTo(right.Kind) < 0 ? -1 : 1;

            switch (left.Kind)
            {
                case Kind.Absent:
                case Kind.Null:
                    return 0;
                case Kind.Boolean:
                    return left.Boolean.CompareTo(right.Boolean);
                case Kind.Number:
                    return number(left).CompareTo(number(right));
                case Kind.String:
                    return Math.Sign(text(left.Text, right.Text));
                default:
                    return string.Equals(left.Text, right.Text, StringComparison.Ordinal) ? 0 : null;
            }
        }

        /// <summary>
        /// Compares two strings by Unicode code point, which is the order of their UTF-8 bytes.
        /// </summary>
        public static int CompareCodePoints(string left, string right)
        {
            var l = left.EnumerateRunes();
            var r = right.EnumerateRunes();

            while (true)
            {
                var hasLeft = l.MoveNext();
                var hasRight = r.MoveNext();

                if (hasLeft == false || hasRight == false)
                    return hasLeft == hasRight ? 0 : hasLeft ? 1 : -1;

                if (l.Current.Value != r.Current.Value)
                    return l.Current.Value < r.Current.Value ? -1 : 1;
            }
        }

        /// <summary>
        /// Merges the streams of several partitions as the SDK does: the next row is the least head
        /// under <paramref name="compare"/>, and a tie goes to the lower partition.
        /// </summary>
        /// <param name="partitions">Each partition's stream, in feed range order.</param>
        /// <param name="compare">The merge comparator, with the direction already applied.</param>
        public static List<T> Merge<T>(IReadOnlyList<IReadOnlyList<T>> partitions, Comparison<T> compare)
        {
            var heads = new int[partitions.Count];
            var merged = new List<T>();

            while (true)
            {
                var next = -1;
                for (var p = 0; p < partitions.Count; p++)
                    if (heads[p] < partitions[p].Count && (next < 0 || compare(partitions[p][heads[p]], partitions[next][heads[next]]) < 0))
                        next = p;

                if (next < 0)
                    return merged;

                merged.Add(partitions[next][heads[next]++]);
            }
        }

    }

}
