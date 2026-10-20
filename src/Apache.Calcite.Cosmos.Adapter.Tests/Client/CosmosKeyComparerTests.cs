using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// The comparator the reader completes an order with: Calcite's, which is Java's, over the values the service
    /// returned — pinned to what Calcite's own engine answered, row for row, and not to the CLR runtime the differential
    /// suite's oracle runs, which orders the text of a multi-key sort by culture.
    /// </summary>
    /// <remarks>
    /// The rows of each case are what the Calcite inventory measured (its §6): S1 and U1 for text, M1 for a decimal's
    /// scale, B1 for a boolean, D2 for the zeros, Q1 for a UUID's canonical spelling, and the absolute placement of
    /// nulls in either direction.
    /// </remarks>
    public class CosmosKeyComparerTests
    {

        static JsonElement Row(string member, string json) => JsonDocument.Parse("{\"" + member + "\":" + json + "}").RootElement.Clone();

        static JsonElement Missing() => JsonDocument.Parse("{}").RootElement.Clone();

        static List<string> Sorted(CosmosOrderedKeyKind kind, bool descending, bool nullsFirst, params string[] values)
        {
            var comparer = new CosmosKeyComparer([new CosmosOrderedKey("v", kind, descending, nullsFirst, CosmosOrderAnchor.None)]);
            var rows = values.Select(v => v == "absent" ? Missing() : Row("v", v)).ToList();
            rows.Sort(comparer);
            return rows.Select(r => r.TryGetProperty("v", out var v) ? v.GetRawText() : "absent").ToList();
        }

        static string Json(string text) => JsonSerializer.Serialize(text);

        /// <summary>
        /// Text a client wrote that is not UTF-8, which Azure stores and returns as written and ranks by its bytes, has no
        /// place in Calcite's order: the comparison and the anchor's test each refuse it by name rather than with the
        /// decoder's error, which named nothing.
        /// </summary>
        [Fact]
        public void TextThatIsNotUtf8IsRefusedByName()
        {
            var comparer = new CosmosKeyComparer([new CosmosOrderedKey("v", CosmosOrderedKeyKind.Text, false, false, CosmosOrderAnchor.Text)]);
            var bad = JsonDocument.Parse(new byte[] { (byte)'{', (byte)'"', (byte)'v', (byte)'"', (byte)':', (byte)'"', (byte)'x', 0xFF, (byte)'y', (byte)'"', (byte)'}' }).RootElement.Clone();
            const string Refused = "A value the statement orders or compares by holds text that is not UTF-8 at $: the bytes FF in \"x\\xFFy\".*";

            ((System.Action)(() => comparer.Compare(Row("v", Json("a")), bad))).Should().Throw<CosmosMaterializationException>().WithMessage(Refused);
            ((System.Action)(() => comparer.IsAnchor(bad))).Should().Throw<CosmosMaterializationException>().WithMessage(Refused);
        }

        /// <summary>
        /// S1: <c>String.compareTo</c>, by UTF-16 code unit, with no collator.
        /// </summary>
        [Fact]
        public void TextComparesByCodeUnit()
        {
            Sorted(CosmosOrderedKeyKind.Text, false, false, Json("b"), Json("B"), Json("a"), Json("A"), Json("_"), Json("1"), Json("é"))
                .Select(v => JsonSerializer.Deserialize<string>(v))
                .Should().Equal("1", "A", "B", "_", "a", "b", "é");
        }

        /// <summary>
        /// U1: a supplementary character is a surrogate pair from U+D800, below U+E000 — the order Calcite gives, and the
        /// one code point order does not.
        /// </summary>
        [Fact]
        public void TextPutsASurrogatePairBelowTheHighBand()
        {
            Sorted(CosmosOrderedKeyKind.Text, false, false, Json(""), Json("\U0001F600"), Json("�"), Json("Ａ"), Json("a"))
                .Select(v => JsonSerializer.Deserialize<string>(v))
                .Should().Equal("a", "\U0001F600", "", "Ａ", "�");
        }

        /// <summary>
        /// M1: <c>BigDecimal.compareTo</c> ignores scale, and an integer past 2^53 is exact.
        /// </summary>
        [Fact]
        public void AnExactNumberComparesByValueWhateverItsScale()
        {
            var key = new CosmosOrderedKey("v", CosmosOrderedKeyKind.Exact, false, false, CosmosOrderAnchor.Number);

            CosmosKeyComparer.CompareKey(Row("v", "1.1").GetProperty("v"), Row("v", "1.10").GetProperty("v"), key).Should().Be(0);
            CosmosKeyComparer.CompareKey(Row("v", "9007199254740992").GetProperty("v"), Row("v", "9007199254740993").GetProperty("v"), key).Should().Be(-1);
            CosmosKeyComparer.CompareKey(Row("v", "9223372036854775800").GetProperty("v"), Row("v", "9223372036854775807").GetProperty("v"), key).Should().Be(-1, "2^63 as the emulator prints it is below 2^63 - 1 as Calcite reads both");
            CosmosKeyComparer.CompareKey(Row("v", "1e20").GetProperty("v"), Row("v", "100000000000000000001").GetProperty("v"), key).Should().Be(-1);
        }

        /// <summary>
        /// A double reads two integers past 2^53 alike, as Calcite reads them, where an exact key does not; and D2:
        /// <c>Double.compare</c> puts <c>-0.0</c> below <c>0.0</c>.
        /// </summary>
        [Fact]
        public void ADoubleComparesAsCalciteReadsIt()
        {
            var key = new CosmosOrderedKey("v", CosmosOrderedKeyKind.Double, false, false, CosmosOrderAnchor.Number);

            CosmosKeyComparer.CompareKey(Row("v", "9007199254740992").GetProperty("v"), Row("v", "9007199254740993").GetProperty("v"), key).Should().Be(0);
            CosmosKeyComparer.CompareKey(Row("v", "-0.0").GetProperty("v"), Row("v", "0.0").GetProperty("v"), key).Should().Be(-1);
            CosmosKeyComparer.CompareKey(Row("v", "1.5").GetProperty("v"), Row("v", "-2.5").GetProperty("v"), key).Should().Be(1);
        }

        /// <summary>
        /// B1: false before true, and a null last under <c>NULLS LAST</c>.
        /// </summary>
        [Fact]
        public void ABooleanIsFalseFirst()
        {
            Sorted(CosmosOrderedKeyKind.Boolean, false, false, "true", "null", "false").Should().Equal("false", "true", "null");
        }

        /// <summary>
        /// A null's place is absolute: first or last as written, whatever the direction; and every value the column
        /// reads as null — absent, a JSON null, another type — is one tie class.
        /// </summary>
        [Fact]
        public void NullsArePlacedAbsolutely()
        {
            Sorted(CosmosOrderedKeyKind.Text, true, true, Json("a"), "null", Json("b"), "absent")
                .Take(2).Should().BeEquivalentTo(["null", "absent"]);
            Sorted(CosmosOrderedKeyKind.Text, true, true, Json("a"), "null", Json("b"), "absent")
                .Skip(2).Should().Equal(Json("b"), Json("a"));
            Sorted(CosmosOrderedKeyKind.Text, true, false, Json("a"), "null", Json("b"), "7")
                .Take(2).Should().Equal(Json("b"), Json("a"));

            var key = new CosmosOrderedKey("v", CosmosOrderedKeyKind.Exact, false, true, CosmosOrderAnchor.Number);
            CosmosKeyComparer.CompareKey(Row("v", "null").GetProperty("v"), default, key).Should().Be(0, "a JSON null and an absent path are both null");
            CosmosKeyComparer.CompareKey(Row("v", "\"30\"").GetProperty("v"), Row("v", "null").GetProperty("v"), key).Should().Be(0, "a string is null to a number's reading");
        }

        /// <summary>
        /// Q1: a canonical UUID's spelling, which is how a stored form keeps the order, compares as the unsigned value
        /// Calcite's <c>UuidValue</c> does.
        /// </summary>
        [Fact]
        public void ACanonicalUuidSpellingComparesAsTheUnsignedValue()
        {
            Sorted(CosmosOrderedKeyKind.Text, false, false, Json("7fffffff-ffff-ffff-ffff-ffffffffffff"), Json("ffffffff-0000-0000-0000-000000000000"), Json("00000000-0000-0000-0000-000000000001"))
                .Select(v => JsonSerializer.Deserialize<string>(v))
                .Should().Equal("00000000-0000-0000-0000-000000000001", "7fffffff-ffff-ffff-ffff-ffffffffffff", "ffffffff-0000-0000-0000-000000000000");
        }

        /// <summary>
        /// Several keys compare in turn, each in its own direction, and a row certifies the order around it where
        /// every key's value is an anchor.
        /// </summary>
        [Fact]
        public void SeveralKeysCompareInTurnAndCertifyTogether()
        {
            var comparer = new CosmosKeyComparer(
            [
                new CosmosOrderedKey("k", CosmosOrderedKeyKind.Exact, false, false, CosmosOrderAnchor.None),
                new CosmosOrderedKey("s", CosmosOrderedKeyKind.Text, true, false, CosmosOrderAnchor.Text),
            ]);

            JsonElement Of(string k, string s) => JsonDocument.Parse($"{{\"k\":{k},\"s\":{Json(s)}}}").RootElement.Clone();

            comparer.Compare(Of("1", "a"), Of("1", "b")).Should().Be(1, "descending on the second key");
            comparer.Compare(Of("1", "z"), Of("2", "a")).Should().Be(-1, "the first key decides");

            comparer.IsAnchor(Of("1", "a퟿")).Should().BeTrue();
            comparer.IsAnchor(Of("1", "a")).Should().BeFalse();
            comparer.IsAnchor(Of("1", "\U0001F600")).Should().BeFalse();
            comparer.IsAnchor(JsonDocument.Parse("{\"k\":1}").RootElement.Clone()).Should().BeTrue("a key read as null is placed alike by both orders");
        }

    }

}
