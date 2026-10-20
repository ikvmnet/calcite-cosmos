using System;
using System.Collections.Generic;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Metadata;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Compares the rows of an order the reader completes as Calcite compares them, from the values the service
    /// returned; and says which rows certify the order around them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Calcite's semantics, which are Java's, written here.</b> A string compares by UTF-16 code unit, which is
    /// <c>String.compareTo</c> and <see cref="string.CompareOrdinal(string, string)"/>; an exact number by value
    /// whatever its scale, which is <c>BigDecimal.compareTo</c>; a double as <c>Double.compare</c> does, <c>-0.0</c>
    /// below <c>0.0</c> and <c>NaN</c> last; a boolean false first. Every value a column reads as null is one tie class
    /// placed absolutely: first wherever <c>NULLS FIRST</c> says so, otherwise last, whatever the direction. Not the CLR
    /// runtime's comparator, which orders the text of a multi-key sort by the en-US culture and puts <c>NaN</c> first
    /// under one non-null key: those are its defects, not Calcite's order, and an order completed by them would be one
    /// Calcite does not allow.
    /// </para>
    /// <para>
    /// <b>What a column reads as null, read from the stored value.</b> A key's reading was proved to read one JSON type
    /// as values (R3), so a value of any other — absent, a JSON null, an object — is what the column reads as null.
    /// </para>
    /// </remarks>
    internal sealed class CosmosKeyComparer : IComparer<JsonElement>
    {

        /// <summary>
        /// What a key's value is, to the error a value that is not UTF-8 is refused with. A key is a member the
        /// statement selects under a name no column has, so there is no column to name.
        /// </summary>
        internal const string Ordered = "A value the statement orders or compares by";

        readonly IReadOnlyList<CosmosOrderedKey> _keys;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keys">The keys, in order.</param>
        public CosmosKeyComparer(IReadOnlyList<CosmosOrderedKey> keys)
        {
            _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        }

        /// <summary>
        /// Compares two rows by every key, as Calcite does.
        /// </summary>
        /// <param name="x">A row the service returned.</param>
        /// <param name="y">Another.</param>
        /// <returns>The first non-zero comparison of a key, or zero.</returns>
        public int Compare(JsonElement x, JsonElement y)
        {
            foreach (var key in _keys)
            {
                var c = CompareKey(Member(x, key.Member), Member(y, key.Member), key);
                if (c != 0)
                    return c;
            }

            return 0;
        }

        /// <summary>
        /// Determines whether a row certifies the order around it: whether every key's value is an anchor.
        /// </summary>
        /// <param name="row">A row the service returned.</param>
        /// <returns><c>true</c> where it does.</returns>
        public bool IsAnchor(JsonElement row)
        {
            foreach (var key in _keys)
                if (CosmosServiceOrder.IsAnchor(key.Anchor, Member(row, key.Member)) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Reads a member of a row, or an undefined element where the service left it out.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <param name="member">The member.</param>
        /// <returns>The value.</returns>
        public static JsonElement Member(JsonElement row, string member) =>
            row.ValueKind == JsonValueKind.Object && row.TryGetProperty(member, out var value) ? value : default;

        /// <summary>
        /// Compares one key's values, its nulls placed and its direction applied.
        /// </summary>
        /// <param name="a">One value.</param>
        /// <param name="b">Another.</param>
        /// <param name="key">The key.</param>
        /// <returns>Negative, zero or positive.</returns>
        public static int CompareKey(JsonElement a, JsonElement b, CosmosOrderedKey key)
        {
            var aNull = IsValue(a, key.Kind) == false;
            var bNull = IsValue(b, key.Kind) == false;

            if (aNull || bNull)
                return aNull == bNull ? 0 : aNull == key.NullsFirst ? -1 : 1;

            var c = CompareValues(a, b, key.Kind);
            return key.Descending ? -c : c;
        }

        /// <summary>
        /// Determines whether a stored value is one the column reads as a value of the kind, rather than as null.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="kind">How the column compares.</param>
        /// <returns><c>true</c> for a value.</returns>
        public static bool IsValue(JsonElement value, CosmosOrderedKeyKind kind) => kind switch
        {
            CosmosOrderedKeyKind.Text => value.ValueKind == JsonValueKind.String,
            CosmosOrderedKeyKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => value.ValueKind == JsonValueKind.Number,
        };

        /// <summary>
        /// Compares two values of the kind, ascending, as Java does.
        /// </summary>
        /// <param name="a">One value, of the kind.</param>
        /// <param name="b">Another.</param>
        /// <param name="kind">How the column compares.</param>
        /// <returns>Negative, zero or positive.</returns>
        public static int CompareValues(JsonElement a, JsonElement b, CosmosOrderedKeyKind kind)
        {
            switch (kind)
            {
                case CosmosOrderedKeyKind.Text:
                    // Decoded strictly: text that is not UTF-8 has no place in Calcite's order, and the
                    // service ranked it by its bytes, so the order cannot be completed over it.
                    return Math.Sign(string.CompareOrdinal(CosmosJson.Decode(a, Ordered), CosmosJson.Decode(b, Ordered)));

                case CosmosOrderedKeyKind.Boolean:
                    return a.GetBoolean().CompareTo(b.GetBoolean());

                case CosmosOrderedKeyKind.Double:
                    return JavaCompare(a.GetDouble(), b.GetDouble());

                case CosmosOrderedKeyKind.Real:
                    return JavaCompare(a.GetSingle(), b.GetSingle());

                default:
                    // A JSON number's grammar is a subset of BigDecimal's, and compareTo ignores scale: 1 and 1.0 tie.
                    return new java.math.BigDecimal(a.GetRawText()).compareTo(new java.math.BigDecimal(b.GetRawText()));
            }
        }

        /// <summary>
        /// Compares two doubles as <c>Double.compare</c> does: by value, then <c>-0.0</c> below <c>0.0</c>, and <c>NaN</c>
        /// above everything and equal to itself.
        /// </summary>
        static int JavaCompare(double a, double b)
        {
            if (a < b)
                return -1;
            if (a > b)
                return 1;

            var x = double.IsNaN(a) ? long.MaxValue : BitConverter.DoubleToInt64Bits(a);
            var y = double.IsNaN(b) ? long.MaxValue : BitConverter.DoubleToInt64Bits(b);
            return x.CompareTo(y);
        }

    }

}
