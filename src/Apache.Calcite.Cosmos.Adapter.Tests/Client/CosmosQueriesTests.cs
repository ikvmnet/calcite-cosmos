using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

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
        /// <c>CosmosRexTranslator.TryGetExactNumberValue</c>. The plan was made to send the value, so one no value serves
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

    }

}
