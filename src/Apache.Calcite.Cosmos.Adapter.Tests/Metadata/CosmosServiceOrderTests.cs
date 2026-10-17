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

            CosmosServiceOrder.Compare(facts, id, CosmosOrderOperand.ForLiteral(JsonScalar.From("a"))).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, id, CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR)).IsProved.Should().BeFalse();
            CosmosServiceOrder.Compare(facts, id, Typed("id", SqlTypeName.VARCHAR)).IsProved.Should().BeFalse("two strings of any character");
        }

        /// <remarks>
        /// The values a domain pins are its own characters; below U+D800, against a parameter of any.
        /// </remarks>
        [Fact]
        public void APathADomainKeepsBelowUD800ComparesWithAParameter()
        {
            var facts = Declared("""{ "type": "object", "properties": { "s": { "enum": ["a", "B"] }, "u": { "enum": [""] } } }""");

            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR)).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("u"), CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR)).IsProved.Should().BeFalse("U+E000 against a supplementary character");
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

            CosmosServiceOrder.Compare(facts, CosmosOrderOperand.ForLiteral(JsonScalar.From("a")), parameter).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, parameter, CosmosOrderOperand.ForParameter(SqlTypeName.VARCHAR)).IsProved.Should().BeFalse();

            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForLiteral(JsonScalar.From(""))).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("s"), CosmosOrderOperand.ForLiteral(JsonScalar.From("\U0001F600"))).IsProved.Should().BeTrue();
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
            CosmosServiceOrder.Compare(facts, id, parameter).IsProved.Should().BeTrue();

            CosmosServiceOrder.Compare(facts, Text("s"), Text("e")).IsProved.Should().BeTrue("below U+D800 against anything");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("w")) }).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("w"), Text("s")).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(facts, Text("w"), parameter).IsProved.Should().BeFalse("a high unit against a surrogate");
            CosmosServiceOrder.OrderBy(facts, new[] { Key(Text("e")) }).IsProved.Should().BeFalse("a surrogate stored lone is U+FFFD");
        }

        /// <remarks>
        /// Text over a path that may hold a number is a rendering, compared as text in Calcite and as another type at
        /// the service.
        /// </remarks>
        [Fact]
        public void ARenderingOfWhatMayNotBeAStringIsNotCompared()
        {
            CosmosServiceOrder.Compare(Declared(), Text("v"), CosmosOrderOperand.ForLiteral(JsonScalar.From("30"))).IsProved.Should().BeFalse();
            CosmosServiceOrder.Compare(Declared("""{ "type": "object", "properties": { "v": { "type": "string" } } }"""), Text("v"), CosmosOrderOperand.ForLiteral(JsonScalar.From("30"))).IsProved.Should().BeTrue();
        }

        /// <remarks>
        /// A comparison reads the document, which holds a number as written: a stored 2^63 raises a
        /// <c>RETURNING BIGINT</c> there rather than reading back below 2^63 - 1, so no bound is needed.
        /// </remarks>
        [Fact]
        public void ANumberIsComparedWithoutBounds()
        {
            CosmosServiceOrder.Compare(Declared(), Typed("w", SqlTypeName.BIGINT), CosmosOrderOperand.ForParameter(SqlTypeName.BIGINT)).IsProved.Should().BeTrue();
            CosmosServiceOrder.Compare(Declared(), Typed("d", SqlTypeName.DOUBLE), CosmosOrderOperand.ForExpression(SqlTypeName.DOUBLE)).IsProved.Should().BeTrue();
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
