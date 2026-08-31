using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Covers the reading of a returned JSON value into the representation Calcite holds a value of that
    /// SQL type in.
    /// </summary>
    /// <remarks>
    /// The assertions are mostly about the <em>type</em> of what comes back rather than its value, and
    /// that is the point. A row of CLR primitives would compile and then fail at the first Calcite
    /// operator that casts, which is a long way from here; asserting the box is what keeps that from
    /// being discovered at run time.
    /// </remarks>
    [TestClass]
    public class CosmosJsonTests
    {

        static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement;

        static object? Read(string json, SqlTypeName typeName) => CosmosJson.GetValue(Value(json), typeName);

        [TestMethod]
        public void ShouldReadStringAsString()
        {
            Read("\"widget\"", SqlTypeName.VARCHAR).Should().Be("widget");
        }

        [TestMethod]
        public void ShouldReadBooleanAsJavaBoolean()
        {
            Read("true", SqlTypeName.BOOLEAN).Should().BeOfType<java.lang.Boolean>().And.Be(java.lang.Boolean.TRUE);
        }

        [TestMethod]
        public void ShouldReadIntegerAsJavaInteger()
        {
            Read("42", SqlTypeName.INTEGER).Should().BeOfType<java.lang.Integer>().And.Be(java.lang.Integer.valueOf(42));
        }

        [TestMethod]
        public void ShouldReadBigintAsJavaLong()
        {
            Read("1717171717", SqlTypeName.BIGINT).Should().BeOfType<java.lang.Long>().And.Be(java.lang.Long.valueOf(1717171717L));
        }

        [TestMethod]
        public void ShouldReadDoubleAsJavaDouble()
        {
            Read("1.5", SqlTypeName.DOUBLE).Should().BeOfType<java.lang.Double>().And.Be(java.lang.Double.valueOf(1.5d));
        }

        /// <remarks>
        /// A Cosmos number is an IEEE double, so an integral column legitimately arrives with a fractional
        /// part written out. It is the same value and reads as one.
        /// </remarks>
        [TestMethod]
        public void ShouldReadWholeNumberWithFractionalNotationAsInteger()
        {
            Read("42.0", SqlTypeName.INTEGER).Should().Be(java.lang.Integer.valueOf(42));
        }

        /// <remarks>
        /// Rounding here would answer a question the query did not ask. Refusing is the only reading that
        /// cannot be silently wrong.
        /// </remarks>
        [TestMethod]
        public void ShouldRefuseFractionalNumberAsInteger()
        {
            var act = () => Read("42.5", SqlTypeName.INTEGER);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*not a whole number*");
        }

        [TestMethod]
        public void ShouldRefuseNumberOutsideTheRangeOfItsType()
        {
            var act = () => Read("2147483648", SqlTypeName.INTEGER);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*outside the range*");
        }

        /// <remarks>
        /// Built from the digits JSON carried rather than from a double, which is the whole point of a
        /// decimal.
        /// </remarks>
        [TestMethod]
        public void ShouldReadDecimalLosslessly()
        {
            Read("0.1234567890123456789012345", SqlTypeName.DECIMAL)
                .Should().BeOfType<java.math.BigDecimal>()
                .And.Subject.ToString().Should().Be("0.1234567890123456789012345");
        }

        /// <remarks>
        /// A container is schemaless and a property may hold a number where the plan expected text.
        /// Coercing it would make the row type a suggestion rather than a declaration.
        /// </remarks>
        [TestMethod]
        public void ShouldRefuseNumberAsString()
        {
            var act = () => Read("42", SqlTypeName.VARCHAR);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*Expected a JSON string*");
        }

        [TestMethod]
        public void ShouldReadNullAsNull()
        {
            Read("null", SqlTypeName.VARCHAR).Should().BeNull();
        }

        [TestMethod]
        public void ShouldReadObjectAsJavaMapPreservingDocumentOrder()
        {
            var map = (java.util.Map)Read("""{ "b": 1, "a": "x" }""", SqlTypeName.MAP)!;

            map.size().Should().Be(2);
            map.get("a").Should().Be("x");
            map.get("b").Should().Be(java.lang.Long.valueOf(1L));
            ((string)map.keySet().toArray()[0]).Should().Be("b");
        }

        [TestMethod]
        public void ShouldReadArrayAsJavaList()
        {
            var list = (java.util.List)Read("""[ 1, "two", null ]""", SqlTypeName.ARRAY)!;

            list.size().Should().Be(3);
            list.get(0).Should().Be(java.lang.Long.valueOf(1L));
            list.get(1).Should().Be("two");
            list.get(2).Should().BeNull();
        }

        [TestMethod]
        public void ShouldReadNestedStructureUnderAny()
        {
            var map = (java.util.Map)Read("""{ "inner": { "n": 2 }, "list": [ true ] }""", SqlTypeName.ANY)!;

            ((java.util.Map)map.get("inner")).get("n").Should().Be(java.lang.Long.valueOf(2L));
            ((java.util.List)map.get("list")).get(0).Should().Be(java.lang.Boolean.TRUE);
        }

        /// <remarks>
        /// A whole JSON number reads as a Long so that an identifier or a count does not surface as a
        /// double. The choice is the value's, there being no schema to consult.
        /// </remarks>
        [TestMethod]
        public void ShouldReadWholeNumberUnderAnyAsLong()
        {
            Read("7", SqlTypeName.ANY).Should().BeOfType<java.lang.Long>();
            Read("7.25", SqlTypeName.ANY).Should().BeOfType<java.lang.Double>();
        }

        /// <remarks>
        /// Calcite's internal encoding, not a date object: days since 1970-01-01.
        /// </remarks>
        [TestMethod]
        public void ShouldReadIsoDateAsDayCount()
        {
            Read("\"1970-01-11\"", SqlTypeName.DATE).Should().Be(java.lang.Integer.valueOf(10));
        }

        [TestMethod]
        public void ShouldReadIsoTimestampAsEpochMilliseconds()
        {
            Read("\"1970-01-01T00:00:01Z\"", SqlTypeName.TIMESTAMP).Should().Be(java.lang.Long.valueOf(1000L));
        }

        /// <remarks>
        /// Cosmos elides a property whose value is undefined, so absence is the ordinary case rather than
        /// an exceptional one, and reads as SQL NULL.
        /// </remarks>
        [TestMethod]
        public void ShouldReadAbsentPropertyAsNull()
        {
            CosmosJson.GetProperty(Value("""{ "id": "a" }"""), "missing", SqlTypeName.VARCHAR).Should().BeNull();
        }

        [TestMethod]
        public void ShouldReadPresentPropertyByName()
        {
            CosmosJson.GetProperty(Value("""{ "id": "a" }"""), "id", SqlTypeName.VARCHAR).Should().Be("a");
        }

        [TestMethod]
        public void ShouldRefuseARowThatIsNotAnObject()
        {
            var act = () => CosmosJson.GetProperty(Value("42"), "id", SqlTypeName.VARCHAR);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*Expected a JSON object for the row*");
        }

        // ── Rendering a value as text ────────────────────────────────────────────
        //
        // What a projection that dropped a CAST(… AS VARCHAR) reads back with. The expected strings
        // are not this method's invention: each is what the in-process plan returns for that document,
        // measured against the emulator, and the table is in DESIGN.md. A change here that these do
        // not catch is a change the differential oracle will.

        [TestMethod]
        public void ShouldRenderAStringAsItself()
        {
            CosmosJson.GetText(Value("\"bikes\"")).Should().Be("bikes");
        }

        [TestMethod]
        public void ShouldRenderAnIntegralNumberWithoutAFraction()
        {
            CosmosJson.GetText(Value("30")).Should().Be("30");
        }

        [TestMethod]
        public void ShouldRenderAFractionalNumberAsJavaDoes()
        {
            CosmosJson.GetText(Value("30.7")).Should().Be("30.7");
        }

        /// <remarks>
        /// Java's rendering of a double, not the JSON text: <c>1e30</c> arrives written that way and
        /// comes back as <c>1.0E30</c>, which is what the in-process cast returns.
        /// </remarks>
        [TestMethod]
        public void ShouldRenderALargeNumberInJavaNotation()
        {
            CosmosJson.GetText(Value("1e30")).Should().Be("1.0E30");
        }

        /// <remarks>
        /// Lower case, which is Java's and not SQL's — a <c>BOOLEAN</c> column renders <c>TRUE</c>.
        /// This is a rendering of an <c>ANY</c> value and follows the box it is held in.
        /// </remarks>
        [TestMethod]
        public void ShouldRenderABooleanInLowerCase()
        {
            CosmosJson.GetText(Value("true")).Should().Be("true");
        }

        /// <remarks>
        /// Java's collection rendering rather than JSON's: no quotes, and a space after each comma.
        /// </remarks>
        [TestMethod]
        public void ShouldRenderAnArrayAsAJavaList()
        {
            CosmosJson.GetText(Value("""["x","y"]""")).Should().Be("[x, y]");
        }

        [TestMethod]
        public void ShouldRenderAnObjectAsAJavaMap()
        {
            CosmosJson.GetText(Value("""{ "v": "bikes" }""")).Should().Be("{v=bikes}");
        }

        [TestMethod]
        public void ShouldRenderJsonNullAsNull()
        {
            CosmosJson.GetText(Value("null")).Should().BeNull();
        }

        [TestMethod]
        public void ShouldRenderAnAbsentPropertyAsNull()
        {
            CosmosJson.GetTextProperty(Value("""{ "id": "a" }"""), "missing").Should().BeNull();
        }

        [TestMethod]
        public void ShouldRenderAPresentPropertyByName()
        {
            CosmosJson.GetTextProperty(Value("""{ "n": 30 }"""), "n").Should().Be("30");
        }

        [TestMethod]
        public void ShouldRefuseARowThatIsNotAnObjectWhenRendering()
        {
            var act = () => CosmosJson.GetTextProperty(Value("42"), "id");
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*Expected a JSON object for the row*");
        }

        /// <remarks>
        /// The reading is per ordinal and does not change what a declared column does: a <c>VARCHAR</c>
        /// column reached the ordinary way still refuses a number rather than coercing it, which is what
        /// keeps the row type from becoming a suggestion.
        /// </remarks>
        [TestMethod]
        public void RenderingDoesNotLoosenTheTypedReading()
        {
            var act = () => CosmosJson.GetValue(Value("30"), SqlTypeName.VARCHAR);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*Expected a JSON string*");
        }

    }

}
