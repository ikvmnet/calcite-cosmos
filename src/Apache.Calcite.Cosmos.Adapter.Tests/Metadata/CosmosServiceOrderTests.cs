using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

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
            var container = TestReasoning.Bind(new CosmosContainerMetadata("items", new[] { "/pk" }));
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

            CosmosServiceOrder.Compare(facts, id, CosmosOrderOperand.ForLiteral(JsonScalar.From("a")), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, id, CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse();
            CosmosServiceOrder.Compare(facts, id, Typed("id", SqlTypeName.VARCHAR), inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse("two strings of any character");
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

            var verdict = CosmosServiceOrder.Compare(facts, id, parameter, inclusive: false, RenderingSet.Production);
            verdict.Kind.Should().Be(CosmosOrderVerdictKind.Anchored);
            verdict.Anchors.Should().Equal(CosmosOrderAnchor.Text);

            CosmosServiceOrder.Compare(facts, parameter, id, inclusive: false, RenderingSet.Production).Kind.Should().Be(CosmosOrderVerdictKind.Anchored);
            CosmosServiceOrder.Compare(facts, id, Typed("id", SqlTypeName.VARCHAR), inclusive: false, RenderingSet.Production).Kind.Should().Be(CosmosOrderVerdictKind.Refused);
            CosmosServiceOrder.Compare(facts, parameter, CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false, RenderingSet.Production).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "no path for the reader to recheck");
            CosmosServiceOrder.Compare(facts, Text("v"), parameter, inclusive: false, RenderingSet.Production).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "a rendering of what may not be a string no value certifies");
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

            var id = CosmosServiceOrder.OrderBy(facts, new[] { Key(Typed("id", SqlTypeName.VARCHAR)) }, RenderingSet.Production);
            id.Kind.Should().Be(CosmosOrderVerdictKind.Anchored);
            id.Keys.Select(k => k.Anchor).Should().Equal(CosmosOrderAnchor.Text);
            id.Reason.Should().Contain("text across U+D800");

            CosmosServiceOrder.OrderBy(facts, new[] { Key(Typed("w", SqlTypeName.BIGINT)) }, RenderingSet.Production).Keys.Select(k => k.Anchor).Should().Equal(CosmosOrderAnchor.Number);

            var both = CosmosServiceOrder.OrderBy(facts, new[] { Key(Typed("d", SqlTypeName.DOUBLE)), Key(Typed("d", SqlTypeName.DOUBLE), RelFieldCollation.Direction.DESCENDING), Key(Text("s")) }, RenderingSet.Production);
            both.Kind.Should().Be(CosmosOrderVerdictKind.Anchored);
            both.Written.Should().Equal(0, 1, 2);
            both.Keys.Select(k => k.Anchor).Should().Equal(CosmosOrderAnchor.Number, CosmosOrderAnchor.Number, CosmosOrderAnchor.Text);
            both.Keys.Select(k => k.Descending).Should().Equal(false, true, false);

            CosmosServiceOrder.OrderBy(facts, new[] { Key(Typed("d", SqlTypeName.DOUBLE)) }, RenderingSet.Production).IsProved.Should().BeTrue("the last key may be finer at the service");

            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("t"), nulls: RelFieldCollation.NullDirection.LAST) }, RenderingSet.Production).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "no value moves the nulls");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("v")) }, RenderingSet.Production).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "a rendering");
        }

        /// <remarks>
        /// An extreme that is an anchor is Calcite's; one over strings nothing states, or an integer nothing bounds, is
        /// anchored, and still refused where the service counts a JSON null.
        /// </remarks>
        [Fact]
        public void AnExtremeTheRowsCanProveIsAnchored()
        {
            var facts = Declared("""{ "type": "object", "properties": { "w": { "type": "integer" }, "n": { "type": ["string", "null"] } } }""");

            CosmosServiceOrder.Extreme(facts, Typed("id", SqlTypeName.VARCHAR), RenderingSet.Production).Anchors.Should().Equal(CosmosOrderAnchor.Text);
            CosmosServiceOrder.Extreme(facts, Typed("w", SqlTypeName.BIGINT), RenderingSet.Production).Anchors.Should().Equal(CosmosOrderAnchor.Number);
            CosmosServiceOrder.Extreme(facts, Text("n"), RenderingSet.Production).Kind.Should().Be(CosmosOrderVerdictKind.Refused);
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

            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("u"), CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse("U+E000 against a supplementary character");
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

            CosmosServiceOrder.Compare(facts, CosmosOrderOperand.ForLiteral(JsonScalar.From("a")), parameter, inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, parameter, CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR), inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse();

            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForLiteral(JsonScalar.From("")), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForLiteral(JsonScalar.From("\U0001F600")), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
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

            CosmosServiceOrder.OrderBy(facts, new[] { Key(id) }, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Extreme(facts, id, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, id, parameter, inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();

            CosmosServiceOrder.Compare(facts, Text("s"), Text("e"), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue("below U+D800 against anything");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("w")) }, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("w"), Text("s"), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("w"), parameter, inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse("a high unit against a surrogate");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("e")) }, RenderingSet.Production).IsProved.Should().BeFalse("a surrogate stored lone is U+FFFD");
        }

        // ── Under the emulator ───────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>s</c> may hold U+0000, its pattern admitting it; <c>a</c> and <c>f</c> may not, <c>f</c> holding U+FDD0;
        /// <c>e</c> holds three values either side of U+D800; <c>id</c> anything.
        /// </summary>
        static JsonFactSet EmulatorFacts() => Declared("""
            { "type": "object", "required": ["s", "a", "f", "e"], "properties": {
                "s": { "type": "string", "pattern": "^[\\u0000-\\uD7FF]*$" },
                "a": { "type": "string", "pattern": "^[a-z]*$" },
                "f": { "type": "string", "pattern": "^[a-z\\uFDD0]*$" },
                "e": { "enum": ["a", "", "😀"] } } }
            """);

        /// <summary>A sort's verdict over one key, ascending and descending, and an extreme's over the same operand.</summary>
        static CosmosOrderVerdictKind[] Ordered(JsonFactSet facts, CosmosOrderOperand operand, RenderingSet set) =>
        [
            CosmosServiceOrder.OrderBy(facts, new[] { Key(operand) }, set).Kind,
            CosmosServiceOrder.OrderBy(facts, new[] { Key(operand, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.LAST) }, set).Kind,
            CosmosServiceOrder.Extreme(facts, operand, set).Kind,
        ];

        /// <remarks>
        /// <para>
        /// R1 reads the target's measured string order (<c>Service.StringOrderIsCodePoint</c>): the emulator stores U+0000
        /// as U+FDD0 <c>0</c>, above every unit from U+0001 to U+FDCF, where Calcite puts it least. Measured on both images,
        /// <c>MIN(c.s)</c> is U+0001 where Calcite's is U+0000, and a two-partition merge by code unit puts an anchor before
        /// a row of the other partition that belongs first. So a sort either way and an extreme over text that may hold
        /// one is refused there; Azure's verdicts stand. Text that cannot is anchored as before, on
        /// <see cref="CosmosOrderAnchor.Text"/>, none of its values holding U+0000; U+FDD0, stored as U+FDD0 <c>1</c>, keeps
        /// the order and is refused nothing.
        /// </para>
        /// </remarks>
        [Fact]
        public void UnderTheEmulatorTextThatMayHoldANulOrdersAlikeWithNothing()
        {
            var facts = EmulatorFacts();
            var (proved, anchored, refused) = (CosmosOrderVerdictKind.Proved, CosmosOrderVerdictKind.Anchored, CosmosOrderVerdictKind.Refused);

            Ordered(facts, Text("s"), RenderingSet.Production).Should().Equal(proved, proved, proved);
            Ordered(facts, Text("s"), RenderingSet.Emulator).Should().Equal(refused, refused, refused);
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("s")) }, RenderingSet.Emulator).Reason.Should().Contain("U+0000");
            Ordered(facts, Typed("id", SqlTypeName.VARCHAR), RenderingSet.Production).Should().Equal(anchored, anchored, anchored);
            Ordered(facts, Typed("id", SqlTypeName.VARCHAR), RenderingSet.Emulator).Should().Equal(refused, refused, refused);
            Ordered(facts, Text("a"), RenderingSet.Emulator).Should().Equal(proved, proved, proved);
            Ordered(facts, Text("f"), RenderingSet.Emulator).Should().Equal(proved, proved, proved);

            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("e")) }, RenderingSet.Production).Keys.Select(k => k.Anchor).Should().Equal(CosmosOrderAnchor.Text);
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("e")) }, RenderingSet.Emulator).Keys.Select(k => k.Anchor).Should().Equal(CosmosOrderAnchor.Text);
            CosmosServiceOrder.Extreme(facts, Text("e"), RenderingSet.Emulator).Anchors.Should().Equal(CosmosOrderAnchor.Text);
        }

        /// <remarks>
        /// <para>
        /// A comparison under the emulator, its values sent in the stored spelling: the two orders part only where U+0000
        /// meets a unit from U+0001 to U+FDCF (measured, 6 of 26 pairs of paths wrong, exactly there). Against a parameter,
        /// a path that cannot hold U+0000 is anchored on a value holding no U+0000 either, where Azure proved it outright or
        /// anchored on <see cref="CosmosOrderAnchor.Text"/>; one that can is refused, whatever the value, nothing weaker
        /// being sent (owner, 2026-11-01: "Cut it"). Two paths, or a path and a literal, either of which may hold U+0000,
        /// are refused: nothing recovers the rows the service drops. A conversion's parameter is written in its stored
        /// form, which holds none.
        /// </para>
        /// </remarks>
        [Fact]
        public void UnderTheEmulatorAComparisonOverANulIsRefused()
        {
            var facts = EmulatorFacts();
            var parameter = CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR);
            var id = Typed("id", SqlTypeName.VARCHAR);

            CosmosOrderVerdict Compare(CosmosOrderOperand left, CosmosOrderOperand right, RenderingSet set) => CosmosServiceOrder.Compare(facts, left, right, inclusive: false, set);

            Compare(Text("a"), parameter, RenderingSet.Production).IsProved.Should().BeTrue();
            Compare(Text("a"), parameter, RenderingSet.Emulator).Anchors.Should().Equal(CosmosOrderAnchor.TextWithoutNul);
            Compare(parameter, Text("a"), RenderingSet.Emulator).Anchors.Should().Equal(CosmosOrderAnchor.TextWithoutNul);
            Compare(Text("e"), parameter, RenderingSet.Production).Anchors.Should().Equal(CosmosOrderAnchor.Text);
            Compare(Text("e"), parameter, RenderingSet.Emulator).Anchors.Should().Equal(CosmosOrderAnchor.TextWithoutNul);

            Compare(Text("s"), parameter, RenderingSet.Production).IsProved.Should().BeTrue("below U+D800 against anything");
            Compare(Text("s"), parameter, RenderingSet.Emulator).Reason.Should().Contain("U+0000");
            Compare(Text("s"), parameter, RenderingSet.Emulator).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "nothing weaker is sent (owner, 2026-11-01: \"Cut it\")");
            Compare(id, parameter, RenderingSet.Production).Anchors.Should().Equal(CosmosOrderAnchor.Text);
            Compare(parameter, id, RenderingSet.Emulator).Kind.Should().Be(CosmosOrderVerdictKind.Refused);

            Compare(Text("s"), Text("a"), RenderingSet.Production).IsProved.Should().BeTrue();
            Compare(Text("s"), Text("a"), RenderingSet.Emulator).Reason.Should().Contain("U+0000");
            Compare(Text("a"), Text("s"), RenderingSet.Emulator).Kind.Should().Be(CosmosOrderVerdictKind.Refused);
            Compare(Text("a"), Text("f"), RenderingSet.Emulator).IsProved.Should().BeTrue("U+FDD0's escape keeps the order");
            Compare(Text("s"), CosmosOrderOperand.ForLiteral(JsonScalar.From("b")), RenderingSet.Emulator).Kind.Should().Be(CosmosOrderVerdictKind.Refused);
            Compare(Text("a"), CosmosOrderOperand.ForLiteral(JsonScalar.From("b")), RenderingSet.Emulator).IsProved.Should().BeTrue();
            Compare(Text("a"), CosmosOrderOperand.ForLiteral(JsonScalar.From("b\u0000")), RenderingSet.Emulator).Kind.Should().Be(CosmosOrderVerdictKind.Refused);
            Compare(CosmosOrderOperand.ForLiteral(JsonScalar.From("a")), parameter, RenderingSet.Emulator).Kind.Should().Be(CosmosOrderVerdictKind.Refused, "a value that may hold U+0000 against a constant");

            CosmosServiceOrder.Equate(facts, Text("s"), parameter).IsProved.Should().BeTrue("equality in the stored spelling is exact");

            // A stored form's conversion, its parameter written in the form: whatever Azure answers, the emulator does.
            var at = At("at");
            var stored = CosmosOracle.For(TestReasoning.Bind(new CosmosContainerMetadata("items", new[] { "/pk" })).WithFacts(new[]
            {
                new JsonFact(at, new JsonClaim.Represents(CosmosUuidForms.CanonicalLowerSortable)), new JsonFact(at, new JsonClaim.Present()), new JsonFact(at, new JsonClaim.OfType(JsonType.String)),
            }.Select(JsonFactRule.Unconditional)).Facts).Declared;
            var conversion = CosmosOrderOperand.ForConversion(at, null, CosmosTemporalParts.None);
            var azure = CosmosServiceOrder.Compare(stored, conversion, parameter, inclusive: true, RenderingSet.Production);
            azure.IsProved.Should().BeTrue(azure.Reason);
            CosmosServiceOrder.Compare(stored, conversion, parameter, inclusive: true, RenderingSet.Emulator).IsProved.Should().BeTrue();
        }

        /// <remarks>
        /// The emulator's anchor: <see cref="CosmosOrderAnchor.TextWithoutNul"/> is a <see cref="CosmosOrderAnchor.Text"/>
        /// value holding no U+0000. Its bounds stop at U+0000
        /// too, so that it holds no character the stored spelling escapes; Azure's stop at U+D800 alone.
        /// </remarks>
        [Fact]
        public void TheEmulatorsAnchorAndBoundsStopAtU0000()
        {
            static System.Text.Json.JsonElement Json(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.Text, Json("\"a\\u0000\"")).Should().BeTrue("below U+D800");
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.TextWithoutNul, Json("\"a\\u0000\"")).Should().BeFalse();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.TextWithoutNul, Json("\"a\"")).Should().BeTrue();
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.TextWithoutNul, Json("\"\\uFDD0\"")).Should().BeFalse("from U+D800");
            CosmosServiceOrder.IsAnchor(CosmosOrderAnchor.TextWithoutNul, Json("null")).Should().BeTrue();


            CosmosServiceOrder.LowerBound("ab\u0000c", CosmosOrderAnchor.Text).Should().Be("ab\u0000c");
            CosmosServiceOrder.LowerBound("ab\u0000c", CosmosOrderAnchor.TextWithoutNul).Should().Be("ab");
            CosmosServiceOrder.LowerBound("a\uE000\u0000", CosmosOrderAnchor.TextWithoutNul).Should().Be("a");
            CosmosServiceOrder.UpperBound("ab\u0000c", CosmosOrderAnchor.TextWithoutNul).Should().Be("ac");
            CosmosServiceOrder.UpperBound("\u0000a", CosmosOrderAnchor.TextWithoutNul).Should().BeNull();
            CosmosServiceOrder.UpperBound("ab\u0000c", CosmosOrderAnchor.Text).Should().Be("ab\u0000d");
        }

        /// <remarks>
        /// Text over a path that may hold a number is a rendering, compared as text in Calcite and as another type at
        /// the service.
        /// </remarks>
        [Fact]
        public void ARenderingOfWhatMayNotBeAStringIsNotCompared()
        {
            CosmosServiceOrder.Compare(Declared(), Text("v"), CosmosOrderOperand.ForLiteral(JsonScalar.From("30")), inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse();
            CosmosServiceOrder.Compare(Declared("""{ "type": "object", "properties": { "v": { "type": "string" } } }"""), Text("v"), CosmosOrderOperand.ForLiteral(JsonScalar.From("30")), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
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

            CosmosServiceOrder.Compare(facts, Text("t"), Text("t"), inclusive: true, RenderingSet.Production).Reason.Should().Contain("a JSON null on both sides");
            CosmosServiceOrder.Compare(facts, Text("t"), Text("t"), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue("null < null is false at the service too");
            CosmosServiceOrder.Compare(facts, Text("t"), Text("s"), inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue("s is never null");
            CosmosServiceOrder.Compare(facts, Text("t"), CosmosOrderOperand.ForLiteral(JsonScalar.From("a")), inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Typed("k", SqlTypeName.INTEGER), Typed("k", SqlTypeName.INTEGER), inclusive: true, RenderingSet.Production).IsProved.Should().BeFalse();
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
            CosmosServiceOrder.Compare(Declared(), Typed("w", SqlTypeName.BIGINT), CosmosOrderOperand.ForParameter(SqlTypeName.BIGINT), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(Declared(), Typed("d", SqlTypeName.DOUBLE), CosmosOrderOperand.ForLiteral(JsonScalar.From(1e300)), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
        }

        /// <remarks>
        /// <para>
        /// An array's element has no document path, and was read as a value its type alone knew — a rendering as a
        /// string — so <c>JSON_VALUE(c."DOC", '$.tags[0]') &lt; 'm'</c> was proved and sent exact, and a stored number or
        /// boolean, compared as its text in Calcite and across types at the service, was lost. A path no fact can be
        /// about may hold anything: its rendering is no string the service compares, and a typed reading of it reads
        /// every other type as null.
        /// </para>
        /// </remarks>
        [Fact]
        public void APathNoFactIsAboutMayHoldAnything()
        {
            var facts = Declared();
            var m = CosmosOrderOperand.ForLiteral(JsonScalar.From("m"));

            CosmosServiceOrder.Compare(facts, CosmosOrderOperand.ForPath(null, SqlTypeName.VARCHAR, CosmosReading.Text), m, inclusive: false, RenderingSet.Production).Reason.Should().Contain("a rendering of what may not be a string");
            CosmosServiceOrder.Compare(facts, CosmosOrderOperand.ForPath(null, SqlTypeName.VARCHAR, CosmosReading.Typed), m, inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse("a column of text renders what it reads");
            CosmosServiceOrder.Compare(facts, CosmosOrderOperand.ForPath(null, SqlTypeName.ANY, CosmosReading.Typed), m, inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue("the stored value, against a string below U+D800, is the weakening's string branch");

            var element = CosmosOrderOperand.ForPath(null, SqlTypeName.INTEGER, CosmosReading.Typed);
            CosmosServiceOrder.Compare(facts, element, CosmosOrderOperand.ForLiteral(JsonScalar.From(5L)), inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue("a number read as one, every other type read as null and undefined against a number");
            CosmosServiceOrder.Compare(facts, element, element, inclusive: true, RenderingSet.Production).Reason.Should().Contain("a JSON null");
            CosmosServiceOrder.Equate(facts, element, element).IsProved.Should().BeFalse();
            CosmosServiceOrder.Compare(facts, element, Typed("k", SqlTypeName.INTEGER), inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse("either may hold a string, which the service compares and both read as null");
        }

        /// <remarks>
        /// <para>
        /// A value the service computes is Calcite's where Calcite has one, and where Calcite reads null may be anything
        /// else: an undefined from a function, a JSON null a <c>CASE</c> writes, a stored value one hands through. Against
        /// a literal and a parameter of its type that is nothing the service compares; against a path that may hold
        /// another type, or another such value, it is. A type that admits no null has a value in every row: the cast the
        /// validator writes around a constant compared with a <c>DOUBLE</c> is one.
        /// </para>
        /// </remarks>
        [Fact]
        public void AComputedValueIsReadAsAnythingWhereCalciteReadsNull()
        {
            var facts = Declared("""{ "type": "object", "properties": { "k": { "type": ["integer", "null"] }, "n": { "type": "integer" } } }""");
            var computed = CosmosOrderOperand.ForExpression(SqlTypeName.INTEGER, nullable: true);

            CosmosServiceOrder.Compare(facts, computed, CosmosOrderOperand.ForLiteral(JsonScalar.From(5L)), inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, computed, CosmosOrderOperand.ForParameter(SqlTypeName.INTEGER), inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, computed, Typed("k", SqlTypeName.INTEGER), inclusive: true, RenderingSet.Production).Reason.Should().Contain("a JSON null on both sides");
            CosmosServiceOrder.Compare(facts, computed, Typed("k", SqlTypeName.INTEGER), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue("k holds a number or a null, and a null is less than nothing");
            CosmosServiceOrder.Compare(facts, computed, Typed("n", SqlTypeName.INTEGER), inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue("n holds a number wherever it is there");
            CosmosServiceOrder.Compare(facts, computed, computed, inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse("two computed values may both hold a string");

            var always = CosmosOrderOperand.ForExpression(SqlTypeName.DOUBLE, nullable: false);
            CosmosServiceOrder.Compare(facts, Typed("k", SqlTypeName.DOUBLE), always, inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue("a value in every row is never null");
            CosmosServiceOrder.Compare(facts, computed, always, inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue();

            CosmosServiceOrder.Compare(facts, CosmosOrderOperand.ForExpression(SqlTypeName.DATE, nullable: false), CosmosOrderOperand.ForParameter(SqlTypeName.DATE), inclusive: false, RenderingSet.Production).IsProved.Should().BeFalse("an instant the service computes is no value it orders as Calcite does");
        }

        /// <remarks>
        /// A null literal is written as the JSON null, which the service compares as a value: <c>&lt;=</c> keeps a row
        /// holding one, where Calcite's comparison with a null is unknown.
        /// </remarks>
        [Fact]
        public void ANullLiteralIsAJsonNullTheServiceCompares()
        {
            var facts = Declared("""{ "type": "object", "properties": { "k": { "type": ["integer", "null"] }, "n": { "type": "integer" } } }""");

            CosmosServiceOrder.Compare(facts, Typed("k", SqlTypeName.INTEGER), CosmosOrderOperand.ForLiteral(JsonScalar.Null), inclusive: true, RenderingSet.Production).IsProved.Should().BeFalse();
            CosmosServiceOrder.Compare(facts, Typed("k", SqlTypeName.INTEGER), CosmosOrderOperand.ForLiteral(JsonScalar.Null), inclusive: false, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Typed("n", SqlTypeName.INTEGER), CosmosOrderOperand.ForLiteral(JsonScalar.Null), inclusive: true, RenderingSet.Production).IsProved.Should().BeTrue();
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

            CosmosServiceOrder.Extreme(facts, Typed("_etag", SqlTypeName.VARCHAR), RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Extreme(facts, Typed("_ts", SqlTypeName.BIGINT), RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.Extreme(facts, Typed("id", SqlTypeName.VARCHAR), RenderingSet.Production).IsProved.Should().BeFalse();
        }

        /// <remarks>
        /// The service's extreme skips an absent path, counts a JSON null and is undefined over an object; Calcite
        /// skips every null. And a <c>BIGINT</c> read back past 2^63 is below 2^63 - 1 on the emulator.
        /// </remarks>
        [Fact]
        public void AnExtremeSkipsOnlyWhatBothSkip()
        {
            CosmosServiceOrder.Extreme(Declared("""{ "type": "object", "properties": { "n": { "type": "integer" } } }"""), Typed("n", SqlTypeName.INTEGER), RenderingSet.Production).IsProved.Should().BeTrue("absent or an integer");
            CosmosServiceOrder.Extreme(Declared("""{ "type": "object", "properties": { "n": { "type": ["integer", "null"] } } }"""), Typed("n", SqlTypeName.INTEGER), RenderingSet.Production).IsProved.Should().BeFalse("a JSON null is the service's MIN");
            CosmosServiceOrder.Extreme(Declared(), Typed("n", SqlTypeName.INTEGER), RenderingSet.Production).IsProved.Should().BeFalse("an object makes the service's answer undefined");
            CosmosServiceOrder.Extreme(Declared("""{ "type": "object", "properties": { "w": { "type": "integer" } } }"""), Typed("w", SqlTypeName.BIGINT), RenderingSet.Production).IsProved.Should().BeFalse("unbounded");
            CosmosServiceOrder.Extreme(Declared("""{ "type": "object", "properties": { "w": { "type": "integer", "minimum": 0, "maximum": 4294967296 } } }"""), Typed("w", SqlTypeName.BIGINT), RenderingSet.Production).IsProved.Should().BeTrue();
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

            CosmosServiceOrder.OrderBy(facts, new[] { Key(score, RelFieldCollation.Direction.DESCENDING) }, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.OrderBy(facts, new[] { Key(score, RelFieldCollation.Direction.ASCENDING) }, RenderingSet.Production).Reason.Should().Contain("descending");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(CosmosOrderOperand.ForScore(bestFirstDescending: false), RelFieldCollation.Direction.DESCENDING) }, RenderingSet.Production).IsProved.Should().BeFalse();
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

            CosmosServiceOrder.OrderBy(Declared(), new[] { Key(distance, nulls: RelFieldCollation.NullDirection.FIRST) }, RenderingSet.Production).IsProved.Should().BeTrue();
            CosmosServiceOrder.OrderBy(Declared(), new[] { Key(distance, nulls: RelFieldCollation.NullDirection.LAST) }, RenderingSet.Production).IsProved.Should().BeFalse();
            CosmosServiceOrder.OrderBy(Declared(), new[] { Key(CosmosOrderOperand.ForDistance(null), nulls: RelFieldCollation.NullDirection.FIRST) }, RenderingSet.Production).IsProved.Should().BeTrue();
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
            }, RenderingSet.Production);

            verdict.IsProved.Should().BeTrue(verdict.Reason);
            verdict.Written.Should().Equal(1, 3);
        }

        // ── The transpose ─────────────────────────────────────────────────────────────────────

        /// <remarks>
        /// With no facts a cast moves beneath a sort only where it is a strict widening: a nullability assertion, a
        /// wider integer, an integer a double holds exactly. Text into an instant or a UUID orders the text, and a
        /// narrowing ties what the operand parts — which on the last key is a tie Calcite breaks any way.
        /// </remarks>
        [Fact]
        public void OnlyAWideningCastPreservesTheOrderOfEveryValue()
        {
            var types = new JavaTypeFactoryImpl();
            org.apache.calcite.rel.type.RelDataType Of(SqlTypeName name, int precision = -1) => precision < 0 ? types.createSqlType(name) : types.createSqlType(name, precision);

            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), Of(SqlTypeName.BIGINT), last: false).Should().BeTrue();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), Of(SqlTypeName.DOUBLE), last: false).Should().BeTrue();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR, 10), Of(SqlTypeName.VARCHAR, 20), last: false).Should().BeTrue();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), types.createTypeWithNullability(Of(SqlTypeName.INTEGER), true), last: false).Should().BeTrue();

            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.BIGINT), Of(SqlTypeName.DOUBLE), last: false).Should().BeFalse("2^53 + 1 is 2^53 as a double");
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR, 20), Of(SqlTypeName.VARCHAR, 2), last: false).Should().BeFalse("a truncation ties");
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR), Of(SqlTypeName.TIMESTAMP), last: false).Should().BeFalse("the text's order, not the instant's");
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR), Of(SqlTypeName.UUID), last: false).Should().BeFalse();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), Of(SqlTypeName.VARCHAR), last: false).Should().BeFalse("10 before 9 as text");

            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR, 20), Of(SqlTypeName.VARCHAR, 2), last: true).Should().BeTrue("on the last key a tie is Calcite's to break");
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR), Of(SqlTypeName.VARCHAR, 2), last: true).Should().BeTrue();
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.VARCHAR, 20), Of(SqlTypeName.CHAR, 2), last: true).Should().BeFalse("a pad of spaces reorders");
            CosmosServiceOrder.CastPreservesOrder(Of(SqlTypeName.INTEGER), Of(SqlTypeName.VARCHAR, 2), last: true).Should().BeFalse("10 before 9 as text");
        }

    }

}
