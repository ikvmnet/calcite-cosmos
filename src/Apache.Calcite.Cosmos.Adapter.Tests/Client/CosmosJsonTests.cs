using System;
using System.Collections.Generic;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;

using FluentAssertions;

using org.apache.calcite.sql.type;
using Xunit;


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
    public class CosmosJsonTests
    {

        static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement;

        static object? Read(string json, SqlTypeName typeName) => CosmosJson.GetValue(Value(json), typeName);

        // ── What this reads at all ──────────────────────────────────────────────────

        /// <summary>
        /// <c>CanRead</c> answers for every SQL type exactly what <c>GetValue</c> does, and the list
        /// is Calcite's rather than one written here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes <c>CanRead</c> a claim about the reader rather than a second list
        /// beside it.</b> The two are separate switches on purpose — the reader is on the per-value
        /// path and a table of delegates would price every row for a question asked once per
        /// statement — so the thing that keeps them the same switch is this walk over
        /// <c>SqlTypeName.values()</c>. A type added to one and not the other fails here, which is the
        /// failure this whole guard exists to move earlier.
        /// </para>
        /// <para>
        /// The probe value is a JSON string, so a readable type may still refuse it — reading
        /// <c>"x"</c> as an <c>INTEGER</c> is a failure about the <em>value</em>. Only the
        /// <em>no reading</em> message is the answer being compared, which is why that message has one
        /// spelling and both sides use it.
        /// </para>
        /// <para>
        /// Calcite carried 57 type names when this was written and 32 of them have no reading here:
        /// thirteen intervals, four unsigned integers, three further temporal spellings, and the rest
        /// internal things no column is ever typed. None of them is reachable through a pushed
        /// projection today — <c>GEOMETRY</c> was, which is #149 — so what this pins is the boundary
        /// rather than a live gap.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryTypeAgreesWithWhatCanReadSays()
        {
            var values = (SqlTypeName[])SqlTypeName.values();

            values.Should().HaveCountGreaterThan(40, "this walks Calcite's own enumeration rather than a list of its own");

            foreach (var type in values)
            {
                bool hasReading;

                try
                {
                    CosmosJson.GetValue(Value("\"x\""), type);
                    hasReading = true;
                }
                catch (CosmosMaterializationException e)
                {
                    // A readable type may still refuse this particular value; only the absence of a
                    // reading is what is being compared.
                    hasReading = e.Message.Contains("No Cosmos JSON reading") == false;
                }

                CosmosJson.CanRead(type).Should().Be(hasReading, "for " + type.name());
            }
        }

        /// <summary>
        /// A collection is readable only where its element type is, the failure otherwise arriving one
        /// level down.
        /// </summary>
        /// <remarks>
        /// An element is read as the type the <c>RETURNING</c> named — see
        /// <c>ShouldReadArrayElementsAsTheDeclaredComponentType</c> — so an unreadable element is an
        /// unreadable column. Where no element type is given the elements are read by their own JSON
        /// type and there is nothing to refuse.
        /// </remarks>
        [Fact]
        public void ACollectionIsReadableOnlyWhereItsElementIs()
        {
            CosmosJson.CanRead(SqlTypeName.ARRAY, SqlTypeName.VARCHAR).Should().BeTrue();
            CosmosJson.CanRead(SqlTypeName.ARRAY, SqlTypeName.INTERVAL_DAY).Should().BeFalse();
            CosmosJson.CanRead(SqlTypeName.MULTISET, SqlTypeName.INTERVAL_DAY).Should().BeFalse();

            CosmosJson.CanRead(SqlTypeName.ARRAY).Should().BeTrue("an element type nobody named is read by its own JSON type");
        }

        /// <remarks>
        /// The two types a container has to earn, asserted by name because each arrived with a defect
        /// of its own: #150 for the UUID's box, #149 for the geography's absence.
        /// </remarks>
        [Fact]
        public void TheEarnedTypesAreReadable()
        {
            CosmosJson.CanRead(SqlTypeName.UUID).Should().BeTrue();
            CosmosJson.CanRead(SqlTypeName.GEOMETRY).Should().BeTrue();
        }

        // -- Writing a geography back out ----------------------------------------------------------

        /// <summary>
        /// A geometry converts to the GeoJSON object a Cosmos statement means by a geography.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The other direction of <c>GetGeography</c>, and the one a bound parameter needs: a
        /// geography in a statement <em>is</em> a GeoJSON object, so a constant is written into the
        /// SQL as one and a parameter has to be bound as the same thing. Left as the geometry, the
        /// SDK's serializer wrote the IKVM object graph instead (#154).
        /// </para>
        /// <para>
        /// Nested dictionaries and lists rather than one serializer's own tree, because which
        /// serializer the client carries is the caller's choice — asserted through both.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("{\"type\":\"Point\",\"coordinates\":[-111.5,38.3]}")]
        [InlineData("{\"type\":\"Polygon\",\"coordinates\":[[[0.0,0.0],[1.0,0.0],[1.0,1.0],[0.0,1.0],[0.0,0.0]]]}")]
        [InlineData("{\"type\":\"GeometryCollection\",\"geometries\":[{\"type\":\"Point\",\"coordinates\":[1.0,2.0]},{\"type\":\"LineString\",\"coordinates\":[[0.0,0.0],[1.0,1.0]]}]}")]
        public void AGeometryWritesAsItsGeoJson(string geoJson)
        {
            var geometry = Apache.Calcite.Geography.Runtime.GeographyFunctions.FromGeoJson(geoJson);
            var value = CosmosJson.ToGeoJsonValue(geometry);

            Newtonsoft.Json.JsonConvert.SerializeObject(value).Should().Be(geoJson);

            using var round = System.Text.Json.JsonDocument.Parse(
                Newtonsoft.Json.JsonConvert.SerializeObject(value));

            CosmosJson.GetValue(round.RootElement, SqlTypeName.GEOMETRY)
                .Should().BeAssignableTo<org.locationtech.jts.geom.Geometry>()
                .Which.equalsExact(geometry).Should().BeTrue("what is sent is what would be read back");
        }

        /// <summary>
        /// The <c>crs</c> member the writer adds is dropped, and only at the top level because that is
        /// the only place it appears.
        /// </summary>
        /// <remarks>
        /// <c>AsGeoJson</c> writes <c>"crs":{"type":"name","properties":{"name":"EPSG:4326"}}</c>
        /// beside the shape. A geography is WGS84 and has no second reference system to be in, so it
        /// says nothing; what decides it is that the constant form — whose every emitted shape has been
        /// executed against an account — carries the caller's own text and no <c>crs</c>. Sending one
        /// where the working spelling sends none would be a second convention, tested nowhere.
        /// </remarks>
        [Fact]
        public void TheReferenceSystemMemberIsDropped()
        {
            var geometry = Apache.Calcite.Geography.Runtime.GeographyFunctions.FromGeoJson(
                "{\"type\":\"Point\",\"coordinates\":[-111.5,38.3]}");

            Apache.Calcite.Geography.Runtime.GeographyFunctions.AsGeoJson(geometry)
                .Should().Contain("crs", "the writer adds one, which is what this is about");

            CosmosJson.ToGeoJsonValue(geometry).Should().BeOfType<Dictionary<string, object?>>()
                .Which.Should().NotContainKey("crs");
        }

        [Fact]
        public void NoGeometryAtAllIsRefused()
        {
            var act = () => CosmosJson.ToGeoJsonValue(null!);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void NoTypeAtAllIsNotReadable()
        {
            CosmosJson.CanRead((SqlTypeName?)null).Should().BeFalse();
            CosmosJson.CanRead((org.apache.calcite.rel.type.RelDataType?)null).Should().BeFalse();
        }

        [Fact]
        public void ShouldReadStringAsString()
        {
            Read("\"widget\"", SqlTypeName.VARCHAR).Should().Be("widget");
        }

        [Fact]
        public void ShouldReadBooleanAsJavaBoolean()
        {
            Read("true", SqlTypeName.BOOLEAN).Should().BeOfType<java.lang.Boolean>().And.Be(java.lang.Boolean.TRUE);
        }

        [Fact]
        public void ShouldReadIntegerAsJavaInteger()
        {
            Read("42", SqlTypeName.INTEGER).Should().BeOfType<java.lang.Integer>().And.Be(java.lang.Integer.valueOf(42));
        }

        [Fact]
        public void ShouldReadBigintAsJavaLong()
        {
            Read("1717171717", SqlTypeName.BIGINT).Should().BeOfType<java.lang.Long>().And.Be(java.lang.Long.valueOf(1717171717L));
        }

        [Fact]
        public void ShouldReadDoubleAsJavaDouble()
        {
            Read("1.5", SqlTypeName.DOUBLE).Should().BeOfType<java.lang.Double>().And.Be(java.lang.Double.valueOf(1.5d));
        }

        /// <remarks>
        /// A Cosmos number is an IEEE double, so an integral column legitimately arrives with a fractional
        /// part written out. It is the same value and reads as one.
        /// </remarks>
        [Fact]
        public void ShouldReadWholeNumberWithFractionalNotationAsInteger()
        {
            Read("42.0", SqlTypeName.INTEGER).Should().Be(java.lang.Integer.valueOf(42));
        }

        /// <remarks>
        /// Rounding here would answer a question the query did not ask. Refusing is the only reading that
        /// cannot be silently wrong.
        /// </remarks>
        [Fact]
        public void ShouldRefuseFractionalNumberAsInteger()
        {
            var act = () => Read("42.5", SqlTypeName.INTEGER);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*not a whole number*");
        }

        [Fact]
        public void ShouldRefuseNumberOutsideTheRangeOfItsType()
        {
            var act = () => Read("2147483648", SqlTypeName.INTEGER);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*outside the range*");
        }

        /// <remarks>
        /// Built from the digits JSON carried rather than from a double, which is the whole point of a
        /// decimal.
        /// </remarks>
        [Fact]
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
        [Fact]
        public void ShouldRefuseNumberAsString()
        {
            var act = () => Read("42", SqlTypeName.VARCHAR);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*Expected a JSON string*");
        }

        [Fact]
        public void ShouldReadNullAsNull()
        {
            Read("null", SqlTypeName.VARCHAR).Should().BeNull();
        }

        /// <remarks>
        /// <para>
        /// Cosmos has no UUID type, so a column the plan types one is a string property whose spelling
        /// a container's declared facts pinned. The box is <c>org.apache.calcite.util.UuidValue</c>
        /// because that is what Calcite holds a <c>UUID</c> in, and the conversion is Calcite's own
        /// — <c>UuidValue.fromString</c>, which is the method <c>BuiltInMethod.UUID_FROM_STRING</c>
        /// names — so a pushed projection and the in-process cast it replaced cannot answer
        /// differently.
        /// </para>
        /// <para>
        /// <b>The class is asserted and not just the value</b>, because the two are indistinguishable
        /// everywhere but the one place it mattered: reading as the <c>java.util.UUID</c> inside the
        /// wrapper gave the right value in the wrong box, which only a single-column row ever cast
        /// (#150). See
        /// <see cref="Rel.Convert.CosmosToClrEnumerableConverterTests.ShouldReadALoneUuidColumn"/>.
        /// </para>
        /// </remarks>
        [Fact]
        public void ShouldReadStringAsUuidValue()
        {
            Read("\"123e4567-e89b-12d3-a456-426614174000\"", SqlTypeName.UUID)
                .Should().BeOfType<org.apache.calcite.util.UuidValue>()
                .And.Be(org.apache.calcite.util.UuidValue.fromString("123e4567-e89b-12d3-a456-426614174000"));
        }

        /// <remarks>
        /// Either spelling reads, which is what lets a container written in uppercase be projected on
        /// the same terms as one written in lowercase. The value is the same value.
        /// </remarks>
        [Fact]
        public void ShouldReadEitherSpellingAsTheSameUuid()
        {
            Read("\"123E4567-E89B-12D3-A456-426614174000\"", SqlTypeName.UUID)
                .Should().Be(org.apache.calcite.util.UuidValue.fromString("123e4567-e89b-12d3-a456-426614174000"));
        }

        /// <summary>
        /// Every shape <c>CosmosStoredForms</c> recognises reads back, which is what lets a projection
        /// hand the stored text over untouched.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the measurement the brace and hyphenless forms rest on, and it is a claim about
        /// the <em>reader</em> rather than about the pattern table: <c>CosmosRexTranslator</c> renders
        /// <c>CAST(&lt;path&gt; AS UUID)</c> as the raw path, so whatever a form admits has to be
        /// something this converts. All four shapes are, in either case.
        /// </para>
        /// <para>
        /// The two refused are why <c>urn:uuid:</c> and the parenthesised spelling are not forms: each
        /// is as canonical and as sortable as the others, and a projection over one would raise at
        /// read time rather than answer.
        /// </para>
        /// </remarks>
        [Fact]
        public void ShouldReadEverySpellingThisRecognises()
        {
            var expected = org.apache.calcite.util.UuidValue.fromString("0123456f-89ab-7cde-8f01-23456789abcd");

            foreach (var text in new[]
            {
                "0123456f-89ab-7cde-8f01-23456789abcd",
                "{0123456f-89ab-7cde-8f01-23456789abcd}",
                "0123456f89ab7cde8f0123456789abcd",
                "{0123456f89ab7cde8f0123456789abcd}",
                "0123456F-89AB-7CDE-8F01-23456789ABCD",
                "{0123456F89AB7CDE8F0123456789ABCD}",
            })
                Read($"\"{text}\"", SqlTypeName.UUID).Should().Be(expected, "for " + text);

            foreach (var text in new[]
            {
                "urn:uuid:0123456f-89ab-7cde-8f01-23456789abcd",
                "(0123456f-89ab-7cde-8f01-23456789abcd)",
            })
            {
                var act = () => Read($"\"{text}\"", SqlTypeName.UUID);

                act.Should().Throw<CosmosMaterializationException>("for " + text);
            }
        }

        [Fact]
        public void ShouldReadNullAsNullUuid()
        {
            Read("null", SqlTypeName.UUID).Should().BeNull();
        }

        /// <remarks>
        /// A document contradicting the declaration is the one thing the fact model cannot check in
        /// advance, so it fails rather than answering a null the query would take for a missing value.
        /// Calcite's own cast fails over the same input, which is the agreement that matters.
        /// </remarks>
        [Fact]
        public void ShouldRefuseAStringThatIsNotAUuid()
        {
            var act = () => Read("\"bikes\"", SqlTypeName.UUID);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*not one*");
        }

        [Fact]
        public void ShouldRefuseANumberAsUuid()
        {
            var act = () => Read("30", SqlTypeName.UUID);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*Expected a JSON string*");
        }

        [Fact]
        public void ShouldReadObjectAsJavaMapPreservingDocumentOrder()
        {
            var map = (java.util.Map)Read("""{ "b": 1, "a": "x" }""", SqlTypeName.MAP)!;

            map.size().Should().Be(2);
            map.get("a").Should().Be("x");
            map.get("b").Should().Be(java.lang.Long.valueOf(1L));
            ((string)map.keySet().toArray()[0]).Should().Be("b");
        }

        [Fact]
        public void ShouldReadArrayAsJavaList()
        {
            var list = (java.util.List)Read("""[ 1, "two", null ]""", SqlTypeName.ARRAY)!;

            list.size().Should().Be(3);
            list.get(0).Should().Be(java.lang.Long.valueOf(1L));
            list.get(1).Should().Be("two");
            list.get(2).Should().BeNull();
        }

        /// <summary>
        /// An element is read as the declared component type where the caller has one.
        /// </summary>
        /// <remarks>
        /// The reading above has no element type to consult and discovers each element's; a column
        /// typed <c>INTEGER ARRAY</c> does have one, and the difference shows: an integral JSON number
        /// is a <see cref="java.lang.Long"/> discovered and a <see cref="java.lang.Integer"/> declared,
        /// and a plan that said <c>INTEGER</c> wants the latter.
        /// </remarks>
        [Fact]
        public void ShouldReadArrayElementsAsTheDeclaredComponentType()
        {
            var list = CosmosJson.GetList(Value("""[ 1, 2 ]"""), SqlTypeName.INTEGER);

            list.size().Should().Be(2);
            list.get(0).Should().Be(java.lang.Integer.valueOf(1));
            list.get(1).Should().Be(java.lang.Integer.valueOf(2));
        }

        /// <remarks>
        /// And refuses one that contradicts it, rather than coercing — the same refusal a scalar
        /// column makes, for the same reason.
        /// </remarks>
        [Fact]
        public void ShouldRefuseAnElementThatContradictsTheDeclaredComponentType()
        {
            var act = () => CosmosJson.GetList(Value("""[ "a", 2 ]"""), SqlTypeName.VARCHAR);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*Expected a JSON string*");
        }

        [Fact]
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
        [Fact]
        public void ShouldReadWholeNumberUnderAnyAsLong()
        {
            Read("7", SqlTypeName.ANY).Should().BeOfType<java.lang.Long>();
            Read("7.25", SqlTypeName.ANY).Should().BeOfType<java.lang.Double>();
        }

        /// <remarks>
        /// Calcite's internal encoding, not a date object: days since 1970-01-01.
        /// </remarks>
        [Fact]
        public void ShouldReadIsoDateAsDayCount()
        {
            Read("\"1970-01-11\"", SqlTypeName.DATE).Should().Be(java.lang.Integer.valueOf(10));
        }

        [Fact]
        public void ShouldReadIsoTimestampAsEpochMilliseconds()
        {
            Read("\"1970-01-01T00:00:01Z\"", SqlTypeName.TIMESTAMP).Should().Be(java.lang.Long.valueOf(1000L));
        }

        /// <remarks>
        /// Cosmos elides a property whose value is undefined, so absence is the ordinary case rather than
        /// an exceptional one, and reads as SQL NULL.
        /// </remarks>
        [Fact]
        public void ShouldReadAbsentPropertyAsNull()
        {
            CosmosJson.GetProperty(Value("""{ "id": "a" }"""), "missing", SqlTypeName.VARCHAR).Should().BeNull();
        }

        [Fact]
        public void ShouldReadPresentPropertyByName()
        {
            CosmosJson.GetProperty(Value("""{ "id": "a" }"""), "id", SqlTypeName.VARCHAR).Should().Be("a");
        }

        [Fact]
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

        [Fact]
        public void ShouldRenderAStringAsItself()
        {
            CosmosJson.GetText(Value("\"bikes\"")).Should().Be("bikes");
        }

        [Fact]
        public void ShouldRenderAnIntegralNumberWithoutAFraction()
        {
            CosmosJson.GetText(Value("30")).Should().Be("30");
        }

        [Fact]
        public void ShouldRenderAFractionalNumberAsJavaDoes()
        {
            CosmosJson.GetText(Value("30.7")).Should().Be("30.7");
        }

        /// <remarks>
        /// Java's rendering of a double, not the JSON text: <c>1e30</c> arrives written that way and
        /// comes back as <c>1.0E30</c>, which is what the in-process cast returns.
        /// </remarks>
        [Fact]
        public void ShouldRenderALargeNumberInJavaNotation()
        {
            CosmosJson.GetText(Value("1e30")).Should().Be("1.0E30");
        }

        /// <remarks>
        /// Lower case, which is Java's and not SQL's — a <c>BOOLEAN</c> column renders <c>TRUE</c>.
        /// This is a rendering of an <c>ANY</c> value and follows the box it is held in.
        /// </remarks>
        [Fact]
        public void ShouldRenderABooleanInLowerCase()
        {
            CosmosJson.GetText(Value("true")).Should().Be("true");
        }

        /// <remarks>
        /// Java's collection rendering rather than JSON's: no quotes, and a space after each comma.
        /// </remarks>
        [Fact]
        public void ShouldRenderAnArrayAsAJavaList()
        {
            CosmosJson.GetText(Value("""["x","y"]""")).Should().Be("[x, y]");
        }

        [Fact]
        public void ShouldRenderAnObjectAsAJavaMap()
        {
            CosmosJson.GetText(Value("""{ "v": "bikes" }""")).Should().Be("{v=bikes}");
        }

        [Fact]
        public void ShouldRenderJsonNullAsNull()
        {
            CosmosJson.GetText(Value("null")).Should().BeNull();
        }

        [Fact]
        public void ShouldRenderAnAbsentPropertyAsNull()
        {
            CosmosJson.GetTextProperty(Value("""{ "id": "a" }"""), "missing").Should().BeNull();
        }

        [Fact]
        public void ShouldRenderAPresentPropertyByName()
        {
            CosmosJson.GetTextProperty(Value("""{ "n": 30 }"""), "n").Should().Be("30");
        }

        [Fact]
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
        [Fact]
        public void RenderingDoesNotLoosenTheTypedReading()
        {
            var act = () => CosmosJson.GetValue(Value("30"), SqlTypeName.VARCHAR);
            act.Should().Throw<CosmosMaterializationException>().WithMessage("*Expected a JSON string*");
        }

    }

}
