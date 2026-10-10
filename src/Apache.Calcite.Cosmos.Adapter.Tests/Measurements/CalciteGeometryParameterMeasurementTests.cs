using System;

using Apache.Calcite.Data;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What a <c>GEOMETRY</c> parameter holds by the time a plan reads it, measured through the ADO.NET
    /// driver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The question a pushed distance rests on.</b> The adapter writes a geography parameter where
    /// its cast stood — <c>ST_DISTANCE(c.location, @p0)</c> — and binds the value as GeoJSON (#187,
    /// #156). That is right only if the value is a geometry: a string bound there would reach the
    /// service as a string, <c>ST_DISTANCE</c> over one is undefined, and every row would be dropped
    /// in silence. So what arrives is the thing to know.
    /// </para>
    /// <para>
    /// <b>The driver converts before execution, and that settles it.</b> WKT text arrives in the data
    /// context as a JTS geometry, not as the text; GeoJSON text and a number fail with <em>Unable to
    /// parse WKT</em> before either plan runs. So the slot holds a geometry or nothing, in process and
    /// pushed alike, and the type the plan already has is the whole of what the rendering needs.
    /// </para>
    /// <para>
    /// No service and no adapter: a recording table, as <see cref="CalciteTemporalParameterMeasurementTests"/>
    /// has, reads <c>?0</c> out of the data context its scan is handed — the context
    /// <c>CosmosQueries.Bind</c> reads. Its own, so the two classes running at once do not read each
    /// other's value.
    /// </para>
    /// </remarks>
    public class CalciteGeometryParameterMeasurementTests
    {

        public sealed class RecordingTable : org.apache.calcite.schema.impl.AbstractTable, org.apache.calcite.schema.ScannableTable
        {

            static volatile object? _seen;

            public static object? Seen => _seen;

            public static void Clear() => _seen = null;

            public override org.apache.calcite.rel.type.RelDataType getRowType(org.apache.calcite.rel.type.RelDataTypeFactory typeFactory) =>
                typeFactory.builder().add("X", org.apache.calcite.sql.type.SqlTypeName.INTEGER).build();

            public org.apache.calcite.linq4j.Enumerable scan(org.apache.calcite.DataContext root)
            {
                _seen = root.get("?0");
                return org.apache.calcite.linq4j.Linq4j.asEnumerable(new object[] { new object[] { java.lang.Integer.valueOf(1) } });
            }

        }

        public sealed class RecordingSchemaFactory : org.apache.calcite.schema.SchemaFactory
        {

            public org.apache.calcite.schema.Schema create(org.apache.calcite.schema.SchemaPlus parentSchema, string name, java.util.Map operand) => new Schema();

            sealed class Schema : org.apache.calcite.schema.impl.AbstractSchema
            {

                protected override java.util.Map getTableMap()
                {
                    var map = new java.util.HashMap();
                    map.put("T", new RecordingTable());
                    return map;
                }

            }

        }

        const string Model = """
            inline:{ "version": "1.0", "defaultSchema": "R",
              "schemas": [ { "name": "R", "type": "custom",
                "factory": "Apache.Calcite.Cosmos.Adapter.Tests.Measurements.CalciteGeometryParameterMeasurementTests+RecordingSchemaFactory, Apache.Calcite.Cosmos.Adapter.Tests" } ] }
            """;

        /// <summary>
        /// Runs a statement filtering on the parameter, returning what the scan saw or what was raised.
        /// </summary>
        static (object? Seen, string? Threw) Bind(object value)
        {
            _ = new RecordingSchemaFactory();
            RecordingTable.Clear();

            try
            {
                // LENIENT admits GEOMETRY as a type name, as it does for any spatial caller.
                using var connection = new CalciteConnection(new CalciteConnectionStringBuilder { Model = Model }.ConnectionString + ";conformance=LENIENT");
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = """SELECT 1 FROM "T" WHERE CAST(? AS GEOMETRY) IS NOT NULL""";

                var parameter = command.CreateParameter();
                parameter.Value = value;
                command.Parameters.Add(parameter);

                using var reader = command.ExecuteReader();
                reader.Read();

                return (RecordingTable.Seen, null);
            }
            catch (Exception e)
            {
                var inner = e;
                while (inner.InnerException is Exception next)
                    inner = next;

                return (null, inner.Message);
            }
        }

        /// <summary>
        /// A geometry arrives as itself.
        /// </summary>
        [Fact]
        public void AGeometryArrivesAsAGeometry()
        {
            var point = new org.locationtech.jts.geom.GeometryFactory().createPoint(new org.locationtech.jts.geom.Coordinate(1, 2));

            var answer = Bind(point);

            answer.Threw.Should().BeNull();
            answer.Seen.Should().BeAssignableTo<org.locationtech.jts.geom.Geometry>();
        }

        /// <summary>
        /// WKT text arrives as the geometry it spells — the conversion is the driver's, before the plan.
        /// </summary>
        [Fact]
        public void WktTextArrivesAsAGeometry()
        {
            var answer = Bind("POINT (1 2)");

            answer.Threw.Should().BeNull();
            answer.Seen.Should().BeAssignableTo<org.locationtech.jts.geom.Geometry>(
                "the text is parsed on the way in, so no string reaches the slot");
        }

        /// <summary>
        /// Anything else fails before execution, so neither plan is handed a value it cannot measure.
        /// </summary>
        [Fact]
        public void GeoJsonTextAndANumberFailBeforeExecution()
        {
            Bind("""{"type":"Point","coordinates":[1,2]}""").Threw.Should().Contain("Unable to parse WKT",
                "GeoJSON text is not the driver's spelling of a geometry");

            Bind(42).Threw.Should().Contain("Unable to parse WKT");
        }

    }

}
