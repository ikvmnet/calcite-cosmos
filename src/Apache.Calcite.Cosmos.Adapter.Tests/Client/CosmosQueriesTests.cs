using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;
using FluentAssertions.Execution;

using org.apache.calcite.sql.type;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Covers the binding of a statement's parameters at execution: the values a plan left open, read
    /// out of the data context and shaped into what the service is to receive.
    /// </summary>
    public class CosmosQueriesTests
    {

        /// <summary>
        /// A data context that answers the values it was given and nothing else.
        /// </summary>
        sealed class Context : org.apache.calcite.DataContext
        {

            readonly Dictionary<string, object?> _values;

            public Context(params (string Name, object? Value)[] values)
            {
                _values = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var (name, value) in values)
                    _values[name] = value;
            }

            public org.apache.calcite.schema.SchemaPlus getRootSchema() => null!;

            public org.apache.calcite.adapter.java.JavaTypeFactory getTypeFactory() => null!;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => _values.TryGetValue(name, out var value) ? value! : null!;

        }

        const string PointJson = "{\"type\":\"Point\",\"coordinates\":[-111.5,38.3]}";

        static org.locationtech.jts.geom.Geometry Point() =>
            Apache.Calcite.Geography.Runtime.GeographyFunctions.FromGeoJson(PointJson);

        static CosmosQuery Query(params CosmosParameter[] parameters) =>
            new("SELECT VALUE { \"d\": ST_DISTANCE(c.location, @p0) } FROM products c", parameters);

        static CosmosQuery BindPoint() =>
            CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0))),
                new Context(("?0", Point())));

        /// <summary>
        /// A statement with nothing open is handed back as it stands.
        /// </summary>
        [Fact]
        public void AStatementWithNoOpenSlotIsUnchanged()
        {
            var query = Query(new CosmosParameter("@p0", 42L));

            CosmosQueries.Bind(query, new Context()).Should().Be(query);
        }

        /// <remarks>
        /// <para>
        /// A string holding a lone surrogate cannot be sent: the SDK's UTF-8 encoder refuses it while it writes the
        /// statement, and threw <c>EncoderFallbackException</c>, which names nothing a caller wrote
        /// (<c>Service.LoneSurrogateParameterIsNeverSent</c>). Bound when the statement runs, the parameter is refused by
        /// name before the encoder is reached. A pair is a character and is sent.
        /// </para>
        /// </remarks>
        [Fact]
        public void AParameterHoldingALoneSurrogateIsRefusedByName()
        {
            var query = new CosmosQuery("SELECT VALUE c.id FROM c WHERE (c.s = @p0)", new[] { new CosmosParameter("@p0", new CosmosDynamicValue(0)) });

            var act = () => CosmosQueries.Bind(query, new Context(("?0", "a\uD83D")));
            act.Should().Throw<CosmosExecutionException>().WithMessage("Parameter ?0 holds a lone surrogate*cannot be sent*");

            CosmosQueries.Bind(query, new Context(("?0", "a😀"))).Parameters[0].Value.Should().Be("a😀");
        }

        /// <remarks>
        /// The widths Calcite boxes a number in are collapsed to the two a statement carries, so that
        /// a value arriving late is indistinguishable from one written into the statement.
        /// </remarks>
        [Fact]
        public void ANumberBindsAsTheWidthAStatementCarries()
        {
            var bound = CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0))),
                new Context(("?0", java.lang.Integer.valueOf(7))));

            bound.Parameters[0].Value.Should().Be(7L);
        }

        /// <summary>
        /// An exact number arriving late binds as a literal of it would: a decimal where its double serves, a long where
        /// the double would be another whole number, and nothing where neither is the number.
        /// </summary>
        /// <remarks>
        /// The service reads a decimal as a double, and 2^60 + 1 sent as one matched the document holding 2^60 —
        /// <c>CosmosRexLiterals.TryGetExactNumberValue</c>. The plan was made to send the value, so one no value serves
        /// fails the execution rather than being sent as a number it is not.
        /// </remarks>
        [Fact]
        public void AnExactNumberBindsAsALiteralOfItWould()
        {
            CosmosQuery Bind(string number) => CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0))),
                new Context(("?0", new java.math.BigDecimal(number))));

            Bind("2.5").Parameters[0].Value.Should().BeOfType<decimal>().Which.Should().Be(2.5m);
            Bind("1152921504606846977").Parameters[0].Value.Should().BeOfType<long>().Which.Should().Be(1152921504606846977L);

            FluentActions.Invoking(() => Bind("1.9999999999999999")).Should().Throw<CosmosExecutionException>();
        }

        // -- The parameter's type ------------------------------------------------------------------

        /// <summary>Binds one value into a slot of a type, through a data context holding it as it stands.</summary>
        static object? BindAs(SqlTypeName? type, object? held, int? length = null) => CosmosQueries.Bind(
            Query(new CosmosParameter("@p0", new CosmosDynamicValue(0) { TypeName = type, Length = length })),
            new Context(("?0", held))).Parameters[0].Value;

        /// <summary>
        /// A value is sent as Calcite compares it, converted to the parameter's type, whichever class the data context
        /// holds: each case <c>CalciteParameterMeasurementTests.AParameterIsConvertedToItsTypeWhenRead</c> measures, as
        /// Calcite's own driver leaves the caller's class there and as the ADO.NET driver writes the type's own.
        /// </summary>
        /// <remarks>
        /// Sent as the data context held it, a long bound to a <c>DOUBLE</c> parameter past 2^53 was compared by the
        /// service as another number than Calcite's, 3,000,000,000 bound to an <c>INTEGER</c> one unwrapped, 1.5 bound to
        /// a <c>BIGINT</c> one uncut, the string <c>'2'</c> bound to a number as a string the service leaves undefined
        /// against a number, and the long 30 bound to text as a number it leaves undefined against a string
        /// (<c>Calcite.AParameterIsConvertedToItsTypeWhenRead</c>).
        /// </remarks>
        [Fact]
        public void AValueIsSentAsCalciteConvertsItToTheParametersType()
        {
            var cases = new (string Case, SqlTypeName Type, object Held, int? Length, object Sent)[]
            {
                // Calcite's own driver: the caller's class.
                ("DOUBLE, long 2^53 + 1", SqlTypeName.DOUBLE, java.lang.Long.valueOf(9007199254740993L), null, 9007199254740992d),
                ("INTEGER, long 3,000,000,000", SqlTypeName.INTEGER, java.lang.Long.valueOf(3000000000L), null, -1294967296L),
                ("SMALLINT, long 65,537", SqlTypeName.SMALLINT, java.lang.Long.valueOf(65537L), null, 1L),
                ("REAL, long 2^24 + 1", SqlTypeName.REAL, java.lang.Long.valueOf(16777217L), null, 16777216d),
                ("BIGINT, double 1.5", SqlTypeName.BIGINT, java.lang.Double.valueOf(1.5), null, 1L),
                ("BIGINT, string '2'", SqlTypeName.BIGINT, "2", null, 2L),
                ("DECIMAL, string '2.50'", SqlTypeName.DECIMAL, "2.50", null, 2.5m),
                ("RETURNING INTEGER, string '2'", SqlTypeName.INTEGER, "2", null, 2L),
                ("VARCHAR, long 30", SqlTypeName.VARCHAR, java.lang.Long.valueOf(30L), null, "30"),
                ("VARCHAR, double 1e20", SqlTypeName.VARCHAR, java.lang.Double.valueOf(1e20), null, "1.0E20"),
                ("JSON_VALUE's VARCHAR(2000), long 30", SqlTypeName.VARCHAR, java.lang.Long.valueOf(30L), 2000, "30"),
                ("projected DOUBLE, long 2^53 + 1", SqlTypeName.DOUBLE, java.lang.Long.valueOf(9007199254740993L), null, 9007199254740992d),

                // The ADO.NET driver: the type's own class, which converts to itself.
                ("DOUBLE, double 2^53", SqlTypeName.DOUBLE, java.lang.Double.valueOf(9007199254740992d), null, 9007199254740992d),
                ("REAL, float 2^24", SqlTypeName.REAL, java.lang.Float.valueOf(16777216f), null, 16777216d),
                ("BIGINT, long 2", SqlTypeName.BIGINT, java.lang.Long.valueOf(2L), null, 2L),
                ("DECIMAL, decimal 2.50", SqlTypeName.DECIMAL, new java.math.BigDecimal("2.50"), null, 2.5m),
                ("INTEGER, integer 2", SqlTypeName.INTEGER, java.lang.Integer.valueOf(2), null, 2L),
                ("BOOLEAN, boolean true", SqlTypeName.BOOLEAN, java.lang.Boolean.TRUE, null, true),
                ("VARCHAR, string '30'", SqlTypeName.VARCHAR, "30", null, "30"),
                ("VARCHAR, string '1E+20'", SqlTypeName.VARCHAR, "1E+20", null, "1E+20"),
            };

            using var scope = new AssertionScope();
            foreach (var (name, type, held, length, sent) in cases)
                BindAs(type, held, length).Should().Be(sent, name);
        }

        /// <summary>
        /// A value Calcite cannot convert to the parameter's type fails the statement by name, as Calcite's own plan fails
        /// reading it: a NaN bound to a <c>BIGINT</c> or a <c>DOUBLE</c> parameter, an infinity bound to a <c>DOUBLE</c>,
        /// a string that spells no number bound to a <c>BIGINT</c>, and a string bound to a <c>BOOLEAN</c>.
        /// </summary>
        [Fact]
        public void AValueCalciteCannotConvertFailsByName()
        {
            var cases = new (SqlTypeName Type, object Held, string Raised)[]
            {
                (SqlTypeName.BIGINT, java.lang.Double.valueOf(double.NaN), "NumberFormatException"),
                (SqlTypeName.DOUBLE, java.lang.Double.valueOf(double.NaN), "NumberFormatException"),
                (SqlTypeName.DOUBLE, java.lang.Double.valueOf(double.NegativeInfinity), "NumberFormatException"),
                (SqlTypeName.DOUBLE, java.lang.Double.valueOf(double.PositiveInfinity), "NumberFormatException"),
                (SqlTypeName.BIGINT, "x", "NumberFormatException"),
                (SqlTypeName.BOOLEAN, "true", "ClassCastException"),
            };

            using var scope = new AssertionScope();
            foreach (var (type, held, raised) in cases)
                FluentActions.Invoking(() => BindAs(type, held)).Should().Throw<CosmosExecutionException>()
                    .WithMessage($"Parameter ?0 cannot be converted to its type, {type.getName()}, as Calcite converts it when a row reads it ({raised}: *");
        }

        /// <summary>
        /// The conversion comes before the type's length holds a string and before a bound is written from it, and a slot of
        /// a type Calcite reads as it is, or of none, sends the value as it arrived.
        /// </summary>
        [Fact]
        public void TheConvertedValueIsHeldToItsLengthAndAnyPassesAsItIs()
        {
            BindAs(SqlTypeName.VARCHAR, java.lang.Long.valueOf(30L), length: 1).Should().Be("3", "the text '30' held to one unit");
            BindAs(SqlTypeName.ANY, java.lang.Long.valueOf(30L)).Should().Be(30L, "an ANY parameter is read as the class it holds");
            BindAs(SqlTypeName.ANY, "30").Should().Be("30");
            BindAs(null, "2").Should().Be("2", "a slot made without a type binds the value as it arrives");
            BindAs(SqlTypeName.BIGINT, null).Should().BeNull("a null is no value to convert");

            var bounded = CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0) { TypeName = SqlTypeName.VARCHAR, Bound = CosmosAnchorBound.Lower })),
                new Context(("?0", java.lang.Long.valueOf(30L))));
            bounded.Parameters[0].Value.Should().Be(CosmosServiceOrder.LowerBound("30"), "the bound is written from the converted text");
        }

        // -- A geography ---------------------------------------------------------------------------

        /// <summary>
        /// A geography binds as the GeoJSON object a statement means by one, which is what #154 said
        /// it did not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The value the context hands over is a JTS geometry, and a JTS geometry is not a document
        /// value.</b> Left alone it reached <c>QueryDefinition.WithParameter</c> as itself and the
        /// SDK's serializer wrote the IKVM object graph -- <c>$0</c>-keyed, assembly-qualified, three
        /// kilobytes for this point -- so the service received that in place of a shape. Where the
        /// statement also projected the parameter it came back and was read as a geography, which is
        /// the stack the issue reports.
        /// </para>
        /// <para>
        /// A geography is the one type whose constant form is not a bound value at all: a geography in
        /// a Cosmos statement <em>is</em> a GeoJSON object, so <c>WriteGeographyLiteral</c> writes one
        /// into the SQL. That is why the agreement between a literal and a parameter has to be reached
        /// here rather than inherited from <c>GetLiteralValue</c>, which refuses a <c>GEOMETRY</c>
        /// literal outright.
        /// </para>
        /// </remarks>
        [Fact]
        public void AGeographyBindsAsGeoJson()
        {
            var bound = BindPoint();

            bound.Parameters[0].Value.Should().NotBeAssignableTo<org.locationtech.jts.geom.Geometry>(
                "a geometry is not a document value, and the SDK would serialize its object graph");

            var shape = bound.Parameters[0].Value.Should().BeOfType<Dictionary<string, object?>>().Subject;

            shape["type"].Should().Be("Point");
            shape.Should().NotContainKey("crs",
                "the constant form sends none, and a geography has no second reference system to be in");
        }

        /// <summary>
        /// What the service receives is the object, under either serializer.
        /// </summary>
        /// <remarks>
        /// The parameter goes to <c>QueryDefinition.WithParameter</c> and is written by whichever
        /// serializer the client carries, so the bound value is a dictionary and a list rather than
        /// one serializer's own tree. Both are asserted because which one runs is the caller's choice.
        /// </remarks>
        [Fact]
        public void TheBoundValueSerializesAsTheShape()
        {
            var bound = BindPoint();

            Newtonsoft.Json.JsonConvert.SerializeObject(bound.Parameters[0].Value).Should().Be(PointJson);
            System.Text.Json.JsonSerializer.Serialize(bound.Parameters[0].Value).Should().Be(PointJson);
        }

        /// <summary>
        /// And it is what the definition the executor builds carries.
        /// </summary>
        [Fact]
        public void TheQueryDefinitionCarriesTheShape()
        {
            var definition = CosmosQueryExecutor.CreateDefinition(BindPoint());

            foreach (var parameter in definition.GetQueryParameters())
                parameter.Value.Should().BeOfType<Dictionary<string, object?>>();
        }

        /// <summary>
        /// The shape the service echoes back reads as the geometry that was bound.
        /// </summary>
        /// <remarks>
        /// The half #154 reports, closed. A statement that also projects its parameter gets the value
        /// returned to it, and that column is read by <c>CosmosJson.GetGeography</c>; sending the
        /// shape is what makes it a round trip rather than a failure.
        /// </remarks>
        [Fact]
        public void TheShapeReadsBackAsTheGeometryThatWasBound()
        {
            var point = Point();
            var sent = Newtonsoft.Json.JsonConvert.SerializeObject(BindPoint().Parameters[0].Value);

            using var echoed = System.Text.Json.JsonDocument.Parse(sent);
            var read = CosmosJson.GetValue(echoed.RootElement, org.apache.calcite.sql.type.SqlTypeName.GEOMETRY);

            var geometry = read.Should().BeAssignableTo<org.locationtech.jts.geom.Geometry>().Subject;

            geometry.equalsExact(point).Should().BeTrue();
            geometry.getSRID().Should().Be(4326);
        }

        /// <summary>
        /// A parameter the statement names as stored (<see cref="CosmosQuery.Stored"/>) is sent in the emulator's spelling,
        /// U+0000 as U+FDD0 <c>0</c> and U+FDD0 as U+FDD0 <c>1</c>, after its type's length holds it as Calcite does, which
        /// truncates what it was given; any other is sent as it arrived.
        /// </summary>
        [Fact]
        public void AStoredParameterIsSentInTheStoredSpellingAfterItsLengthHoldsIt()
        {
            var query = new CosmosQuery("SELECT VALUE c.id FROM c WHERE (c.s = @p0) AND (c.t = @p1)",
                [new("@p0", new CosmosDynamicValue(0) { Length = 3 }), new("@p1", new CosmosDynamicValue(1))], Stored: ["@p0"]);

            var bound = CosmosQueries.Bind(query, new Context(("?0", "\0﷐\0x"), ("?1", "a\0")));
            bound.Parameters[0].Value.Should().Be("﷐0﷐1﷐0", "truncated to three units, then each escaped");
            bound.Parameters[1].Value.Should().Be("a\0", "a parameter not named stored is sent as it arrived");

            CosmosQueries.Bind(query, new Context(("?0", "abc"), ("?1", null))).Parameters[0].Value.Should().Be("abc", "a value holding neither is its own spelling");
        }

/// <remarks>
        /// A slot the context has no value for is a null parameter, not a shape.
        /// </remarks>
        [Fact]
        public void AnAbsentValueBindsAsNull()
        {
            var bound = CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0))),
                new Context());

            bound.Parameters[0].Value.Should().BeNull();
        }

        /// <summary>
        /// A guard's slot binds whether its value is there, and never the value: a UUID as Calcite boxes it is no type
        /// the service has.
        /// </summary>
        [Fact]
        public void APresenceSlotBindsTrueForAValueAndNullForNone()
        {
            CosmosQuery Bind(object? value) => CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0) { Presence = true })),
                value is null ? new Context() : new Context(("?0", value)));

            Bind(java.util.UUID.fromString("3a900000-0000-4000-8000-000000000001")).Parameters[0].Value.Should().Be(true);
            Bind("x").Parameters[0].Value.Should().Be(true);
            Bind(null).Parameters[0].Value.Should().BeNull();
        }

    }

}
