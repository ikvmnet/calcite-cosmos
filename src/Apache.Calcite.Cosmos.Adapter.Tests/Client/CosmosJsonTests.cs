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

        // ── Rendering back to text ─────────────────────────────────────────

        /// <remarks>
        /// <para>
        /// <b>A document value renders as the JSON it is.</b> Calcite converts an <c>ANY</c> to text by
        /// calling <c>toString</c>, and a plain <c>LinkedHashMap</c> answers Java's notation —
        /// <c>{type=Point, coordinates=[0.5, 0.25]}</c> — which nothing downstream can parse. Measured,
        /// that was the single thing standing between a stored document value and every one of
        /// Calcite's spatial and JSON functions.
        /// </para>
        /// <para>
        /// So this is load bearing rather than cosmetic: <c>ST_GEOMFROMGEOJSON(c."_MAP"['location'])</c>
        /// works because of it.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void AnObjectRendersAsJson()
        {
            CosmosJson.GetMap(Value("""{"type":"Point","coordinates":[0.5,0.25]}""")).ToString()
                .Should().Be("""{"type":"Point","coordinates":[0.5,0.25]}""");
        }

        [TestMethod]
        public void AnArrayRendersAsJson()
        {
            CosmosJson.GetList(Value("""["outdoor","steel"]""")).ToString()
                .Should().Be("""["outdoor","steel"]""");
        }

        /// <remarks>
        /// Nesting, booleans and nulls included, because a document is not flat.
        /// </remarks>
        [TestMethod]
        public void NestingRendersAsJson()
        {
            CosmosJson.GetMap(Value("""{"a":{"b":[1,2.5,true,null]},"c":"x"}""")).ToString()
                .Should().Be("""{"a":{"b":[1,2.5,true,null]},"c":"x"}""");
        }

        /// <remarks>
        /// A key or value carrying a quote has to come back escaped, or the text is not JSON.
        /// </remarks>
        [TestMethod]
        public void TextIsEscapedWhenRendered()
        {
            CosmosJson.GetMap(Value("""{"a\"b":"c\"d"}""")).ToString()
                .Should().Be("""{"a\"b":"c\"d"}""");
        }

        /// <remarks>
        /// <b>Scalars are untouched, and that is what keeps the text-cast equivalence.</b>
        /// <c>CosmosRexTranslator.TryTextCastOperand</c> drops a cast around a document value compared
        /// against text, and its argument is that a stored string renders as itself while every other
        /// value renders as something recognisable — a number as digits, an object or array "with a
        /// bracket". Rendering an object as JSON keeps the bracket; rendering a string as a quoted JSON
        /// string would have broken the first half.
        /// </remarks>
        [TestMethod]
        public void AScalarStillRendersAsItself()
        {
            CosmosJson.GetNatural(Value("\"widget\"")).ToString().Should().Be("widget");
            CosmosJson.GetNatural(Value("42")).ToString().Should().Be("42");
            CosmosJson.GetNatural(Value("2.5")).ToString().Should().Be("2.5");
            CosmosJson.GetNatural(Value("true")).ToString().Should().Be("true");
        }

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

    }

}
