using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rel;
using org.apache.calcite.sql.type;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// The one question, asked directly: the cases the model's sweep reaches by enumeration, named, beside the ones it does
    /// not model — a score, a distance, a cast moved beneath a sort.
    /// </summary>
    public class CosmosServiceOrderTests
    {

        static JsonFactSet Declared(string? schema = null)
        {
            var container = new CosmosContainerMetadata("items", new[] { "/pk" });
            if (schema is not null)
                container = container.WithFacts(CosmosSchemaRecognition.ReadFrom(JsonNode.Parse(schema)!));

            return CosmosOracle.For(container.Facts).Declared;
        }

        static JsonDocumentPath At(string name) => JsonDocumentPath.Root.Property(name);

        static CosmosOrderOperand Text(string name) => CosmosOrderOperand.ForPath(At(name), SqlTypeName.VARCHAR, CosmosReading.Text);

        static CosmosOrderOperand Typed(string name, SqlTypeName type) => CosmosOrderOperand.ForPath(At(name), type, CosmosReading.Typed);

        static CosmosOrderKey Key(CosmosOrderOperand operand, RelFieldCollation.Direction? direction = null, RelFieldCollation.NullDirection? nulls = null) =>
            new(operand, new RelFieldCollation(0, direction ?? RelFieldCollation.Direction.ASCENDING, nulls ?? RelFieldCollation.NullDirection.FIRST));

        // ── Comparisons ───────────────────────────────────────────────────────────────────────

        /// <remarks>
        /// <c>id</c> may hold any character, and so may a parameter: measured on the probe account, <c>c.s &gt; @p</c>
        /// bound to U+1F600 keeps the strings above it by code point, which Calcite puts below. A literal under the
        /// default charset holds nothing at or above U+0100, and a string below U+D800 orders alike against anything.
        /// </remarks>
        [Fact]
        public void AnIdComparesWithALiteralAndNotWithAParameter()
        {
            var facts = Declared();
            var id = Typed("id", SqlTypeName.VARCHAR);

            CosmosServiceOrder.Compare(facts, id, CosmosOrderOperand.ForLiteral(JsonScalar.From("a")), inclusive: false).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, id, CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false).IsProved.Should().BeFalse();
            CosmosServiceOrder.Compare(facts, id, Typed("id", SqlTypeName.VARCHAR), inclusive: false).IsProved.Should().BeFalse("two strings of any character");
        }

        /// <remarks>
        /// <para>
        /// What a parameter will hold is known when the statement runs: one below U+D800 compares alike with any string
        /// under both orders, so the comparison is exact then and is otherwise read weakened and rechecked — anchored,
        /// whichever side the parameter is on. Two paths' strings are never seen whole, the rows the service drops not
        /// arriving, so a comparison of two paths is refused as before.
        /// </para>
        /// </remarks>
        [Fact]
        public void AComparisonWithAParameterIsAnchoredAndOneWithAPathIsNot()
        {
            var facts = Declared();
            var id = Typed("id", SqlTypeName.VARCHAR);
            var parameter = CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR);

            var verdict = CosmosServiceOrder.Compare(facts, id, parameter, inclusive: false);
            verdict.Kind.Should().Be(CosmosOrderVerdictKind.Anchored);
            verdict.Anchors.Should().Equal(CosmosOrderAnchor.Text);

            CosmosServiceOrder.Compare(facts, parameter, id, inclusive: false).Kind.Should().Be(CosmosOrderVerdictKind.Anchored);
            CosmosServiceOrder.Compare(facts, id, Typed("id", SqlTypeName.VARCHAR), inclusive: false).Kind.Should().Be(CosmosOrderVerdictKind.Refused);
            CosmosServiceOrder.Compare(facts, parameter, CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "no path for the reader to recheck");
            CosmosServiceOrder.Compare(facts, Text("v"), parameter, inclusive: false).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "a rendering of what may not be a string no value certifies");
        }

        // ── What the rows prove ───────────────────────────────────────────────────────────────

        /// <remarks>
        /// <para>
        /// Where the one thing the facts cannot say is what the strings' characters are, or how large a number read back
        /// is, the verdict is anchored: a string below U+D800, or a number within 2^53, orders alike against every other
        /// value, and the reader checks each as it arrives. A <c>DOUBLE</c> refines the service's order past 2^53, which is
        /// enough for the last key and anchored before it.
        /// </para>
        /// <para>
        /// What no value certifies is refused as before: a null placement the service does not share, two JSON types,
        /// a rendering, the two nulls of a leading key.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnOrderTheRowsCanProveIsAnchored()
        {
            var facts = Declared("""{ "type": "object", "required": ["w", "d", "s"], "properties": { "w": { "type": "integer" }, "d": { "type": "number" }, "s": { "type": "string" }, "t": { "type": ["string", "null"] } } }""");

            var id = CosmosServiceOrder.OrderBy(facts, new[] { Key(Typed("id", SqlTypeName.VARCHAR)) });
            id.Kind.Should().Be(CosmosOrderVerdictKind.Anchored);
            id.Anchors.Should().Equal(CosmosOrderAnchor.Text);
            id.Reason.Should().Contain("text across U+D800");

            CosmosServiceOrder.OrderBy(facts, new[] { Key(Typed("w", SqlTypeName.BIGINT)) }).Anchors.Should().Equal(CosmosOrderAnchor.Number);

            var both = CosmosServiceOrder.OrderBy(facts, new[] { Key(Typed("d", SqlTypeName.DOUBLE)), Key(Typed("d", SqlTypeName.DOUBLE), RelFieldCollation.Direction.DESCENDING), Key(Text("s")) });
            both.Kind.Should().Be(CosmosOrderVerdictKind.Anchored);
            both.Written.Should().Equal(0, 1, 2);
            both.Anchors.Should().Equal(CosmosOrderAnchor.Number, CosmosOrderAnchor.Number, CosmosOrderAnchor.Text);

            CosmosServiceOrder.OrderBy(facts, new[] { Key(Typed("d", SqlTypeName.DOUBLE)) }).IsProved.Should().BeTrue("the last key may be finer at the service");

            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("t"), nulls: RelFieldCollation.NullDirection.LAST) }).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "no value moves the nulls");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("v")) }).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "a rendering");
        }

        /// <remarks>
        /// An extreme that is an anchor is Calcite's; one over strings nothing states, or an integer nothing bounds, is
        /// anchored, and still refused where the service counts a JSON null.
        /// </remarks>
        [Fact]
        public void AnExtremeTheRowsCanProveIsAnchored()
        {
            var facts = Declared("""{ "type": "object", "properties": { "w": { "type": "integer" }, "n": { "type": ["string", "null"] } } }""");

            CosmosServiceOrder.Extreme(facts, Typed("id", SqlTypeName.VARCHAR)).Anchors.Should().Equal(CosmosOrderAnchor.Text);
            CosmosServiceOrder.Extreme(facts, Typed("w", SqlTypeName.BIGINT)).Anchors.Should().Equal(CosmosOrderAnchor.Number);
            CosmosServiceOrder.Extreme(facts, Text("n")).Kind.Should().Be(CosmosOrderVerdictKind.Refused);
        }

        /// <remarks>
        /// The lemma and the bound: a string with no unit at or above U+D800, a number strictly within 2^53, and a value the
        /// column reads as null.
        /// </remarks>
        [Fact]
        public void AnAnchorIsAValueBothOrdersAgreeAbout()
        {
            static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Text, Json("\"a\\uD7FF\"")).Should().BeTrue();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Text, Json("\"a\\uE000\"")).Should().BeFalse();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Text, Json("\"\\uD83D\\uDE00\"")).Should().BeFalse();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Text, Json("null")).Should().BeTrue();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Text, default).Should().BeTrue("an absent path reads null");

            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Number, Json("9007199254740991")).Should().BeTrue();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Number, Json("-9007199254740991")).Should().BeTrue();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Number, Json("9007199254740992")).Should().BeFalse("strictly: a service comparing doubles ties 2^53 with 2^53 + 1");
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Number, Json("9.223372036854776E+18")).Should().BeFalse();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Number, Json("\"30\"")).Should().BeTrue("a string reads null under a number's reading");

            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.None, Json("\"\\uE000\"")).Should().BeTrue();
        }

        /// <remarks>
        /// <para>
        /// The bounds a comparison with a parameter that is no anchor is weakened to: below the value up to its first unit
        /// at or above U+D800 for <c>&gt;</c> and <c>&gt;=</c>; above that prefix with its last character raised for
        /// <c>&lt;</c> and <c>&lt;=</c>, U+D7FF going to U+E000; nothing where the prefix is empty.
        /// </para>
        /// <para>
        /// And they hold: every string Calcite keeps, the service keeps under the bound — every pair over strings of up to
        /// three characters from either side of the line, as the plan's <c>anchors.py</c> and <c>keyset_mirror.py</c>
        /// check them.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheBoundsKeepEveryStringCalciteKeeps()
        {
            CosmosServiceOrder.LowerBound("ab\uE000c").Should().Be("ab");
            CosmosServiceOrder.LowerBound("\U0001F600").Should().Be("");
            CosmosServiceOrder.UpperBound("ab\U0001F600").Should().Be("ac");
            CosmosServiceOrder.UpperBound("a\uD7FF\uE000").Should().Be("a\uE000");
            CosmosServiceOrder.UpperBound("\uE000").Should().BeNull();

            string[] alphabet = ["a", "b", "\uD7FE", "\uD7FF", "\uE000", "\uFF21", "\U00010000", "\U0001F600"];
            var strings = new List<string> { "" };
            for (var length = 1; length <= 3; length++)
                strings.AddRange(strings.Where(s => s.EnumerateRunes().Count() == length - 1).SelectMany(s => alphabet.Select(c => s + c)).ToList());

            var lost = new List<string>();
            foreach (var p in strings)
            {
                var lower = CosmosServiceOrder.LowerBound(p);
                var upper = CosmosServiceOrder.UpperBound(p);

                foreach (var x in strings)
                {
                    var calcite = string.CompareOrdinal(x, p);

                    if (calcite >= 0 && Infrastructure.ServiceOrderModel.CompareCodePoints(x, lower) < 0)
                        lost.Add($"{x} >= {p}");

                    if (calcite <= 0 && upper is not null && Infrastructure.ServiceOrderModel.CompareCodePoints(x, upper) >= 0)
                        lost.Add($"{x} <= {p}");
                }
            }

            strings.Should().HaveCount(585);
            lost.Should().BeEmpty("the service keeps every string Calcite does under the bound");
        }

        /// <remarks>
        /// The values a domain pins are its own characters; below U+D800, against a parameter of any.
        /// </remarks>
        [Fact]
        public void APathADomainKeepsBelowUD800ComparesWithAParameter()
        {
            var facts = Declared("""{ "type": "object", "properties": { "s": { "enum": ["a", "B"] }, "u": { "enum": [""] } } }""");

            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("u"), CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false).IsProved.Should().BeFalse("U+E000 against a supplementary character");
        }

        /// <remarks>
        /// <para>
        /// Two constants Azure compares by UTF-16 and the emulator by code point; a constant below U+D800 against any
        /// other is the same comparison under both, so the third comparator needs nothing of its own.
        /// </para>
        /// <para>
        /// A range whose bounds the two orders rank apart — U+E000 and U+1F600 — keeps nothing on Azure as a filter.
        /// Over a path below U+D800 nothing lies between them in either order, and every one of its comparisons is
        /// proved alike.
        /// </para>
        /// </remarks>
        [Fact]
        public void ConstantsAndRangesNeedNothingOfTheirOwn()
        {
            var facts = Declared("""{ "type": "object", "properties": { "s": { "enum": ["a", "B"] } } }""");
            var parameter = CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR);

            CosmosServiceOrder.Compare(facts, CosmosOrderOperand.ForLiteral(JsonScalar.From("a")), parameter, inclusive: false).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, parameter, CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false).IsProved.Should().BeFalse();

            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForLiteral(JsonScalar.From("")), inclusive: false).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForLiteral(JsonScalar.From("\U0001F600")), inclusive: false).IsProved.Should().BeTrue();
        }

        /// <remarks>
        /// A <c>pattern</c> is a proof of a path's characters, the first a user can write (R1): an id declared an
        /// identifier sorts, takes an extreme and compares with a parameter at plan time; a path declared below U+D800
        /// compares with any other; one declared the basic plane without the surrogates sorts, and does not compare with
        /// a value that may hold a surrogate. A pattern admitting a surrogate proves nothing, one stored lone being U+FFFD.
        /// </remarks>
        [Fact]
        public void APatternProvesItsPathsCharacters()
        {
            var facts = Declared("""
                { "type": "object", "required": ["s", "w", "e"], "properties": {
                    "id": { "type": "string", "pattern": "^[A-Za-z0-9_-]+$" },
                    "s": { "type": "string", "pattern": "^[\\u0000-\\uD7FF]*$" },
                    "w": { "type": "string", "pattern": "^[\\u0000-\\uD7FF\\uE000-\\uFFFF]*$" },
                    "e": { "type": "string", "pattern": "^[a-z\\uD800-\\uDFFF]*$" } } }
                """);
            var id = Typed("id", SqlTypeName.VARCHAR);
            var parameter = CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR);

            CosmosServiceOrder.OrderBy(facts, new[] { Key(id) }).IsProved.Should().BeTrue();
            CosmosServiceOrder.Extreme(facts, id).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, id, parameter, inclusive: false).IsProved.Should().BeTrue();

            CosmosServiceOrder.Compare(facts, Text("s"), Text("e"), inclusive: false).IsProved.Should().BeTrue("below U+D800 against anything");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("w")) }).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("w"), Text("s"), inclusive: false).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("w"), parameter, inclusive: false).IsProved.Should().BeFalse("a high unit against a surrogate");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("e")) }).IsProved.Should().BeFalse("a surrogate stored lone is U+FFFD");
        }

        /// <remarks>
        /// Text over a path that may hold a number is a rendering, compared as text in Calcite and as another type at
        /// the service.
        /// </remarks>
        [Fact]
        public void ARenderingOfWhatMayNotBeAStringIsNotCompared()
        {
            CosmosServiceOrder.Compare(Declared(), Text("v"), CosmosOrderOperand.ForLiteral(JsonScalar.From("30")), inclusive: false).IsProved.Should().BeFalse();
            CosmosServiceOrder.Compare(Declared("""{ "type": "object", "properties": { "v": { "type": "string" } } }"""), Text("v"), CosmosOrderOperand.ForLiteral(JsonScalar.From("30")), inclusive: false).IsProved.Should().BeTrue();
        }

        /// <remarks>
        /// The service compares a JSON null with a JSON null as two values of one type, and Calcite a null with nothing:
        /// measured on the emulator, <c>c.t &lt;= c.t</c>, <c>c.t = c.t</c> and <c>c.k &gt;= c.k</c> over paths declared
        /// a string or null and an integer or null kept the documents holding a null. A comparison that equal values do
        /// not satisfy keeps no such row, and a side that is never null leaves the null of the other undefined.
        /// </remarks>
        [Fact]
        public void TwoSidesThatMayBothBeAJsonNullAreNotCompared()
        {
            var facts = Declared("""{ "type": "object", "properties": { "t": { "type": ["string", "null"], "pattern": "^[a-z]*$" }, "s": { "type": "string", "pattern": "^[a-z]*$" }, "k": { "type": ["integer", "null"] } } }""");

            CosmosServiceOrder.Compare(facts, Text("t"), Text("t"), inclusive: true).Reason.Should().Contain("a JSON null on both sides");
            CosmosServiceOrder.Compare(facts, Text("t"), Text("t"), inclusive: false).IsProved.Should().BeTrue("null < null is false at the service too");
            CosmosServiceOrder.Compare(facts, Text("t"), Text("s"), inclusive: true).IsProved.Should().BeTrue("s is never null");
            CosmosServiceOrder.Compare(facts, Text("t"), CosmosOrderOperand.ForLiteral(JsonScalar.From("a")), inclusive: true).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Typed("k", SqlTypeName.INTEGER), Typed("k", SqlTypeName.INTEGER), inclusive: true).IsProved.Should().BeFalse();
            CosmosServiceOrder.Equate(facts, Text("t"), Text("t")).IsProved.Should().BeFalse();
            CosmosServiceOrder.Equate(facts, Typed("k", SqlTypeName.INTEGER), Typed("k", SqlTypeName.INTEGER)).IsProved.Should().BeFalse();
            CosmosServiceOrder.Equate(facts, Text("t"), Text("s")).IsProved.Should().BeTrue();
        }

        /// <remarks>
        /// A comparison reads the document, which holds a number as written: a stored 2^63 raises a
        /// <c>RETURNING BIGINT</c> there rather than reading back below 2^63 - 1, so no bound is needed.
        /// </remarks>
        [Fact]
        public void ANumberIsComparedWithoutBounds()
        {
            CosmosServiceOrder.Compare(Declared(), Typed("w", SqlTypeName.BIGINT), CosmosOrderOperand.ForParameter(SqlTypeName.BIGINT), inclusive: false).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(Declared(), Typed("d", SqlTypeName.DOUBLE), CosmosOrderOperand.ForExpression(SqlTypeName.DOUBLE), inclusive: false).IsProved.Should().BeTrue();
        }

        // ── Extremes ──────────────────────────────────────────────────────────────────────────

        /// <remarks>
        /// <c>_etag</c>'s characters the service states, and an aggregate scans: its extreme pushes where its sort,
        /// over a path the default policy excludes, does not. <c>id</c>'s characters it does not.
        /// </remarks>
        [Fact]
        public void TheServicesPropertiesAreExtremedByWhatTheServiceStates()
        {
            var facts = Declared();

            CosmosServiceOrder.Extreme(facts, Typed("_etag", SqlTypeName.VARCHAR)).IsProved.Should().BeTrue();
            CosmosServiceOrder.Extreme(facts, Typed("_ts", SqlTypeName.BIGINT)).IsProved.Should().BeTrue();
            CosmosServiceOrder.Extreme(facts, Typed("id", SqlTypeName.VARCHAR)).IsProved.Should().BeFalse();
        }

        /// <remarks>
        /// The service's extreme skips an absent path, counts a JSON null and is undefined over an object; Calcite
        /// skips every null. And a <c>BIGINT</c> read back past 2^63 is below 2^63 - 1 on the emulator.
        /// </remarks>
        [Fact]
        public void AnExtremeSkipsOnlyWhatBothSkip()
        {
            CosmosServiceOrder.Extreme(Declared("""{ "type": "object", "properties": { "n": { "type": "integer" } } }"""), Typed("n", SqlTypeName.INTEGER)).IsProved.Should().BeTrue("absent or an integer");
            CosmosServiceOrder.Extreme(Declared("""{ "type": "object", "properties": { "n": { "type": ["integer", "null"] } } }"""), Typed("n", SqlTypeName.INTEGER)).IsProved.Should().BeFalse("a JSON null is the service's MIN");
            CosmosServiceOrder.Extreme(Declared(), Typed("n", SqlTypeName.INTEGER)).IsProved.Should().BeFalse("an object makes the service's answer undefined");
            CosmosServiceOrder.Extreme(Declared("""{ "type": "object", "properties": { "w": { "type": "integer" } } }"""), Typed("w", SqlTypeName.BIGINT)).IsProved.Should().BeFalse("unbounded");
            CosmosServiceOrder.Extreme(Declared("""{ "type": "object", "properties": { "w": { "type": "integer", "minimum": 0, "maximum": 4294967296 } } }"""), Typed("w", SqlTypeName.BIGINT)).IsProved.Should().BeTrue();
        }

        // ── Scores and distances ──────────────────────────────────────────────────────────────

        /// <remarks>
        /// <c>ORDER BY RANK</c> returns the best match first, and the service refuses a direction for it (SC2222,
        /// measured on Azure): a full text score or a fusion orders descending, and a vector distance by a distance
        /// function the metadata does not hold.
        /// </remarks>
        [Fact]
        public void AScoreIsRankedOnlyDescending()
        {
            var facts = JsonFactSet.Empty;
            var score = CosmosOrderOperand.ForScore(bestFirstDescending: true);

            CosmosServiceOrder.OrderBy(facts, new[] { Key(score, RelFieldCollation.Direction.DESCENDING) }).IsProved.Should().BeTrue();
            CosmosServiceOrder.OrderBy(facts, new[] { Key(score, RelFieldCollation.Direction.ASCENDING) }).Reason.Should().Contain("descending");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(CosmosOrderOperand.ForScore(bestFirstDescending: false), RelFieldCollation.Direction.DESCENDING) }).IsProved.Should().BeFalse();
        }

        /// <remarks>
        /// A geodesic distance is the service's WGS84 value (D4), so its order is the order; it is null or undefined
        /// where an operand is not a geography the service measures, which sorts first ascending, and a declared
        /// geography rules both out.
        /// </remarks>
        [Fact]
        public void ADistanceOrdersAsTheService()
        {
            var distance = CosmosOrderOperand.ForDistance(new[] { At("location") });

            CosmosServiceOrder.OrderBy(Declared(), new[] { Key(distance, nulls: RelFieldCollation.NullDirection.FIRST) }).IsProved.Should().BeTrue();
            CosmosServiceOrder.OrderBy(Declared(), new[] { Key(distance, nulls: RelFieldCollation.NullDirection.LAST) }).IsProved.Should().BeFalse();
            CosmosServiceOrder.OrderBy(Declared(), new[] { Key(CosmosOrderOperand.ForDistance(null), nulls: RelFieldCollation.NullDirection.FIRST) }).IsProved.Should().BeTrue();
        }

        // ── Keys ──────────────────────────────────────────────────────────────────────────────

        /// <remarks>
        /// A key the facts pin, and a key an earlier exact key on the same path already ties, order nothing and are not
        /// written.
        /// </remarks>
        [Fact]
        public void ConstantAndRepeatedKeysAreNotWritten()
        {
            var facts = Declared("""{ "type": "object", "required": ["kind", "n"], "properties": { "kind": { "const": "a" }, "n": { "type": "integer" } } }""");

            var verdict = CosmosServiceOrder.OrderBy(facts, new[]
            {
                Key(Text("kind")),
                Key(Typed("n", SqlTypeName.INTEGER)),
                Key(Typed("n", SqlTypeName.BIGINT), RelFieldCollation.Direction.DESCENDING),
                Key(Typed("_ts", SqlTypeName.BIGINT)),
            });

            verdict.IsProved.Should().BeTrue(verdict.Reason);
            verdict.Written.Should().Equal(1, 3);
        }

        // ── The transpose ─────────────────────────────────────────────────────────────────────

        /// <remarks>
        /// With no facts a cast moves beneath a sort only where it is a strict widening: a nullability assertion, a
        /// wider integer, an integer a double holds exactly. Text into an instant or a UUID orders the text, and a
        /// narrowing ties what the operand parts.
        /// </remarks>
        [Fact]
        public void OnlyAWideningCastPreservesTheOrderOfEveryValue()
        {
            var types = new JavaTypeFactoryImpl();
            org.apache.calcite.rel.type.RelDataType Of(SqlTypeName name, int precision = -1) => precision < 0 ? types.createSqlType(name) : types.createSqlType(name, precision);

            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), Of(SqlTypeName.BIGINT)).Should().BeTrue();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), Of(SqlTypeName.DOUBLE)).Should().BeTrue();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR, 10), Of(SqlTypeName.VARCHAR, 20)).Should().BeTrue();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), types.createTypeWithNullability(Of(SqlTypeName.INTEGER), true)).Should().BeTrue();

            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.BIGINT), Of(SqlTypeName.DOUBLE)).Should().BeFalse("2^53 + 1 is 2^53 as a double");
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR, 20), Of(SqlTypeName.VARCHAR, 2)).Should().BeFalse("a truncation ties");
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR), Of(SqlTypeName.TIMESTAMP)).Should().BeFalse("the text's order, not the instant's");
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR), Of(SqlTypeName.UUID)).Should().BeFalse();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), Of(SqlTypeName.VARCHAR)).Should().BeFalse("10 before 9 as text");
        }

    }

}
