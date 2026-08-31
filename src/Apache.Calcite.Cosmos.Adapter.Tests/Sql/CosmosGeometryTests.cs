using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The storage decode: a stored GeoJSON value read as the geometry Calcite computes in.
    /// </summary>
    /// <remarks>
    /// Over the shapes the row model actually produces — <c>java.util.LinkedHashMap</c> for an object,
    /// <c>java.util.ArrayList</c> for an array, and boxed longs and doubles for numbers — because that
    /// is what reaches this at run time, and a decode written against anything else would pass its
    /// tests and fail on a document.
    /// </remarks>
    [TestClass]
    public class CosmosGeometryTests
    {

        static java.util.List List(params object?[] values)
        {
            var list = new java.util.ArrayList();
            foreach (var value in values)
                list.add(value);

            return list;
        }

        /// <remarks>
        /// A long, because the row model boxes an integral JSON number as one — <c>[0, 1]</c> in a
        /// document does not arrive as doubles.
        /// </remarks>
        static java.util.List Position(double x, double y) =>
            List(java.lang.Double.valueOf(x), java.lang.Double.valueOf(y));

        static java.util.Map Geometry(string type, object coordinates)
        {
            var map = new java.util.LinkedHashMap();
            map.put("type", type);
            map.put("coordinates", coordinates);

            return map;
        }

        [TestMethod]
        public void APointDecodes()
        {
            var geometry = CosmosGeometry.FromDocument(Geometry("Point", Position(-122.12, 47.66)));

            geometry.Should().NotBeNull();
            geometry!.getGeometryType().Should().Be("Point");
            geometry.getCoordinate().x.Should().Be(-122.12);
            geometry.getCoordinate().y.Should().Be(47.66);
        }

        /// <remarks>
        /// An integral ordinate arrives boxed as a long rather than a double, which is the row model's
        /// choice and not the schema's — there being no schema.
        /// </remarks>
        [TestMethod]
        public void AnIntegralOrdinateDecodes()
        {
            var position = List(java.lang.Long.valueOf(1), java.lang.Long.valueOf(2));
            var geometry = CosmosGeometry.FromDocument(Geometry("Point", position));

            geometry.Should().NotBeNull();
            geometry!.getCoordinate().x.Should().Be(1d);
            geometry.getCoordinate().y.Should().Be(2d);
        }

        [TestMethod]
        public void ALineStringDecodes()
        {
            var geometry = CosmosGeometry.FromDocument(Geometry("LineString", List(Position(0, 0), Position(1, 1))));

            geometry.Should().NotBeNull();
            geometry!.getGeometryType().Should().Be("LineString");
            geometry.getNumPoints().Should().Be(2);
        }

        [TestMethod]
        public void APolygonDecodes()
        {
            var ring = List(Position(0, 0), Position(1, 0), Position(1, 1), Position(0, 0));
            var geometry = CosmosGeometry.FromDocument(Geometry("Polygon", List(ring)));

            geometry.Should().NotBeNull();
            geometry!.getGeometryType().Should().Be("Polygon");
            geometry.getArea().Should().BeApproximately(0.5d, 1e-9);
        }

        [TestMethod]
        public void AMultiPolygonDecodes()
        {
            var ring = List(Position(0, 0), Position(1, 0), Position(1, 1), Position(0, 0));
            var geometry = CosmosGeometry.FromDocument(Geometry("MultiPolygon", List(List(ring))));

            geometry.Should().NotBeNull();
            geometry!.getGeometryType().Should().Be("MultiPolygon");
        }

        /// <remarks>
        /// A GeoJSON ring is closed — its last position repeats its first — which is what JTS requires
        /// of a linear ring. Closing it here would invent an edge the document did not have.
        /// </remarks>
        [TestMethod]
        public void AnUnclosedRingIsRefused()
        {
            var ring = List(Position(0, 0), Position(1, 0), Position(1, 1), Position(0, 1));

            CosmosGeometry.FromDocument(Geometry("Polygon", List(ring))).Should().BeNull();
        }

        /// <remarks>
        /// Null rather than an exception, for everything that is not a geometry. A spatial predicate
        /// over a document holding something else at the path is a row that does not match, not a query
        /// that fails — and a container guarantees nothing about what a path holds.
        /// </remarks>
        [TestMethod]
        public void AnythingThatIsNotAGeometryDecodesToNull()
        {
            CosmosGeometry.FromDocument(null).Should().BeNull();
            CosmosGeometry.FromDocument("Seattle").Should().BeNull();
            CosmosGeometry.FromDocument(java.lang.Long.valueOf(42)).Should().BeNull();
            CosmosGeometry.FromDocument(List(Position(0, 0))).Should().BeNull();

            // An object, but not one of these.
            var other = new java.util.LinkedHashMap();
            other.put("sku", "B-2");
            CosmosGeometry.FromDocument(other).Should().BeNull();

            // The right members, a type Cosmos does not document.
            CosmosGeometry.FromDocument(Geometry("MultiPoint", List(Position(0, 0)))).Should().BeNull();

            // The right type, coordinates that are not positions.
            CosmosGeometry.FromDocument(Geometry("Point", List("x", "y"))).Should().BeNull();
        }

        /// <remarks>
        /// The decode is what <c>COSMOS_GEOMETRY</c> runs, so the operator has to find it. A rename of
        /// the method would otherwise fail at the first query rather than at the build.
        /// </remarks>
        [TestMethod]
        public void TheOperatorIsBoundToTheDecode()
        {
            CosmosOperators.Geometry.getName().Should().Be("COSMOS_GEOMETRY");
            CosmosOperators.Geometry.Should().BeAssignableTo<org.apache.calcite.sql.validate.SqlUserDefinedFunction>();
        }

    }

}
