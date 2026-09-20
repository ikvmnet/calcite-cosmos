using System;
using System.Collections.Generic;

using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the geography package's GeoJSON constructor does with each thing a document path can hold,
    /// and what an in-process plan therefore answers — which is the agreement a pushed projection of a
    /// geography has to reproduce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No service and no adapter. The left-hand side is
    /// <c>GeographyFunctions.FromGeoJson</c>, which is what <c>CLR_ST_GEOG_GEOMFROMGEOJSON</c> is
    /// implemented as and what <c>CosmosJson.GetGeography</c> calls; the right-hand side is the whole
    /// expression a query writes, run through Calcite's own driver over a literal document.
    /// </para>
    /// <para>
    /// <b>This is the measurement that decided the guard.</b> Pushed down, the constructor disappears
    /// and the path is sent — so whatever the service finds at the path is what the reader converts.
    /// The bare path and the in-process expression agree for an object and for an array; over a
    /// <em>scalar</em> they do not, because <c>JSON_QUERY</c> answers null for one and the constructor
    /// is never reached. Sending the path under the accessor's own guard is what closes that, and
    /// <c>CosmosRexTranslator.TryStoredGeographyProjection</c> is where it goes in.
    /// </para>
    /// <para>
    /// If the package ever answers null for text it cannot parse, rather than raising, the array row
    /// here changes and the reader's refusal has to be revisited with it.
    /// </para>
    /// </remarks>
    public class CalciteGeographyReadingMeasurementTests
    {

        const string Point = "{\"type\":\"Point\",\"coordinates\":[0.5,0.25]}";

        /// <summary>
        /// Evaluates <c>CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(&lt;document&gt;, '$.location'))</c> — the
        /// expression a query writes for a stored shape — over a literal document, through Calcite's
        /// own driver.
        /// </summary>
        /// <returns>The rendered value, or <c>null</c>.</returns>
        static string? InProcess(string document)
        {
            java.lang.Class.forName("org.apache.calcite.jdbc.Driver");

            var connection = (org.apache.calcite.jdbc.CalciteConnection)java.sql.DriverManager.getConnection("jdbc:calcite:", new java.util.Properties());

            try
            {
                Apache.Calcite.Geography.Schema.GeographySchema.AddTo(connection.getRootSchema());

                var results = connection.createStatement().executeQuery(
                    $"SELECT CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY('{document}', '$.location'))");

                results.next();

                return results.getObject(1)?.ToString();
            }
            finally
            {
                connection.close();
            }
        }

        /// <summary>
        /// The constructor is the geography package's, and it stamps WGS84.
        /// </summary>
        /// <remarks>
        /// The SRID is why the reading is this function rather than Calcite's own planar GeoJSON
        /// reader: a geography carries 4326 and a geometry does not, so reading a pushed column the
        /// other way would hand back something the in-process plan would not have.
        /// </remarks>
        [Fact]
        public void TheConstructorStampsWgs84()
        {
            var geometry = Apache.Calcite.Geography.Runtime.GeographyFunctions.FromGeoJson(Point);

            geometry.toText().Should().Be("POINT (0.5 0.25)");
            geometry.getSRID().Should().Be(4326);
        }

        /// <summary>
        /// An object at the path is the shape, both ways.
        /// </summary>
        [Fact]
        public void AnObjectAtThePathIsTheShape()
        {
            InProcess($"{{\"location\":{Point}}}").Should().Be("POINT (0.5 0.25)");

            Apache.Calcite.Geography.Runtime.GeographyFunctions.FromGeoJson(Point)
                .toText().Should().Be("POINT (0.5 0.25)", "and the pushed path sends exactly that object");
        }

        /// <summary>
        /// A <b>scalar</b> at the path is null in process, and is the one case a bare path would have
        /// got wrong.
        /// </summary>
        /// <remarks>
        /// <c>JSON_QUERY</c> answers null for a scalar — it is the accessor for structure — so the
        /// constructor never runs and the column is null. Handed the scalar directly, as the bare path
        /// would have sent it, the constructor <em>raises</em>. An error where the engine answers a
        /// value is the trade this adapter refuses, and the guard is what removes the case.
        /// </remarks>
        [Fact]
        public void AScalarAtThePathIsNullInProcessAndUnreadableDirectly()
        {
            InProcess("{\"location\":\"moab\"}").Should().BeNull("JSON_QUERY answers null for a scalar");

            var direct = () => Apache.Calcite.Geography.Runtime.GeographyFunctions.FromGeoJson("\"moab\"");
            direct.Should().Throw<java.lang.RuntimeException>("which is what the bare path would have handed the reader");
        }

        /// <summary>
        /// An absent path is null in process, and absent from a Cosmos row for the same answer.
        /// </summary>
        [Fact]
        public void AnAbsentPathIsNull()
        {
            InProcess("{}").Should().BeNull();
        }

        /// <summary>
        /// An <b>array</b> at the path raises in process, so the guard admits it and the reader raises
        /// over it too.
        /// </summary>
        /// <remarks>
        /// The half that says the guard is <c>JSON_QUERY</c>'s rather than ''object only'': an array
        /// reaches the constructor in process and fails there, so excluding it would turn a failure
        /// into a null. Both sides fail, which is agreement.
        /// </remarks>
        [Fact]
        public void AnArrayAtThePathRaisesBothWays()
        {
            var inProcess = () => InProcess("{\"location\":[1,2]}");
            inProcess.Should().Throw<java.lang.RuntimeException>();

            var direct = () => Apache.Calcite.Geography.Runtime.GeographyFunctions.FromGeoJson("[1,2]");
            direct.Should().Throw<java.lang.RuntimeException>();
        }

    }

}
