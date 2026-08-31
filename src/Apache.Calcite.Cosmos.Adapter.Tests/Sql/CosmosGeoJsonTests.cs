using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The GeoJSON literal, which is parsed and re-emitted rather than passed through.
    /// </summary>
    /// <remarks>
    /// A literal reaches the translator from query text, and this is the one place an object is written
    /// into a statement. What comes out is built from what was validated — two known member names, a
    /// type from a fixed list, and numbers — so nothing a caller wrote survives verbatim.
    /// </remarks>
    [TestClass]
    public class CosmosGeoJsonTests
    {

        static string? Write(string text) => CosmosGeoJson.TryWrite(text, out var geometry) ? geometry : null;

        [TestMethod]
        public void APointRendersAsAnObjectLiteral()
        {
            Write("""{"type":"Point","coordinates":[-122.12,47.66]}""")
                .Should().Be("""{ "type": "Point", "coordinates": [-122.12, 47.66] }""");
        }

        /// <remarks>
        /// Member order is this type's, not the caller's: the object is rebuilt rather than reprinted.
        /// </remarks>
        [TestMethod]
        public void MembersAreRenderedInACanonicalOrder()
        {
            Write("""{"coordinates":[1,2],"type":"Point"}""")
                .Should().Be("""{ "type": "Point", "coordinates": [1, 2] }""");
        }

        [TestMethod]
        public void EveryTypeCosmosDocumentsIsRendered()
        {
            Write("""{"type":"Point","coordinates":[1,2]}""").Should().NotBeNull();
            Write("""{"type":"LineString","coordinates":[[1,2],[3,4]]}""").Should().NotBeNull();
            Write("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}""").Should().NotBeNull();
            Write("""{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]]]}""").Should().NotBeNull();
        }

        /// <remarks>
        /// GeoJSON, and not a type Cosmos documents for indexing and querying. Refused rather than
        /// emitted on the assumption that it behaves.
        /// </remarks>
        [TestMethod]
        public void ATypeCosmosDoesNotDocumentIsRefused()
        {
            Write("""{"type":"MultiPoint","coordinates":[[1,2],[3,4]]}""").Should().BeNull();
            Write("""{"type":"MultiLineString","coordinates":[[[1,2],[3,4]]]}""").Should().BeNull();
        }

        /// <remarks>
        /// The nesting a type implies is what says a polygon's rings are not a point's position, which
        /// is a mistake this can refuse where the service would only report it.
        /// </remarks>
        [TestMethod]
        public void CoordinatesMustNestAsTheTypeSays()
        {
            Write("""{"type":"Point","coordinates":[[1,2],[3,4]]}""").Should().BeNull();
            Write("""{"type":"Polygon","coordinates":[1,2]}""").Should().BeNull();
            Write("""{"type":"LineString","coordinates":[[[1,2]]]}""").Should().BeNull();
        }

        /// <remarks>
        /// A position is a longitude and a latitude at least, and takes a third for an altitude.
        /// </remarks>
        [TestMethod]
        public void APositionNeedsTwoOrdinatesAndTakesThree()
        {
            Write("""{"type":"Point","coordinates":[1]}""").Should().BeNull();
            Write("""{"type":"Point","coordinates":[1,2,30]}""").Should().Be("""{ "type": "Point", "coordinates": [1, 2, 30] }""");
        }

        [TestMethod]
        public void AnEmptyArrayIsNotAGeometry()
        {
            Write("""{"type":"Point","coordinates":[]}""").Should().BeNull();
            Write("""{"type":"Polygon","coordinates":[]}""").Should().BeNull();
            Write("""{"type":"Polygon","coordinates":[[]]}""").Should().BeNull();
        }

        /// <remarks>
        /// A coordinate is a number. Anything else is a value this would have to copy rather than
        /// rebuild.
        /// </remarks>
        [TestMethod]
        public void ACoordinateThatIsNotANumberIsRefused()
        {
            Write("""{"type":"Point","coordinates":["-122.12",47.66]}""").Should().BeNull();
            Write("""{"type":"Point","coordinates":[null,47.66]}""").Should().BeNull();
            Write("""{"type":"Point","coordinates":[{"x":1},47.66]}""").Should().BeNull();
        }

        /// <remarks>
        /// Exactly the two members. A <c>bbox</c> or a <c>crs</c> is legal GeoJSON that Cosmos does not
        /// document taking here, and admitting a member this cannot reason about is admitting caller
        /// text into the statement.
        /// </remarks>
        [TestMethod]
        public void AnyOtherMemberIsRefused()
        {
            Write("""{"type":"Point","coordinates":[1,2],"bbox":[0,0,1,1]}""").Should().BeNull();
            Write("""{"type":"Point","coordinates":[1,2],"crs":{"type":"name"}}""").Should().BeNull();
            Write("""{"type":"Point"}""").Should().BeNull();
            Write("""{"coordinates":[1,2]}""").Should().BeNull();
        }

        /// <remarks>
        /// A JSON number can be written large enough to read back as an infinity, which has no literal
        /// to render as. Refused rather than left to throw out of the translator.
        /// </remarks>
        [TestMethod]
        public void ACoordinateThatIsNotFiniteIsRefused()
        {
            Write("""{"type":"Point","coordinates":[1e400,47.66]}""").Should().BeNull();
        }

        [TestMethod]
        public void TextThatIsNotJsonIsRefused()
        {
            Write("Seattle").Should().BeNull();
            Write("").Should().BeNull();
            Write("[1,2]").Should().BeNull();
            Write("""{"type":"Point","coordinates":[1,2]""").Should().BeNull();
        }

        /// <remarks>
        /// The escaping is <see cref="CosmosSql"/>'s, applied to a value this chose rather than to one
        /// the caller supplied — a type outside the fixed list never reaches it. Asserted so that the
        /// route stays the one that escapes.
        /// </remarks>
        [TestMethod]
        public void AQuoteInATypeCannotReachTheStatement()
        {
            Write("""{"type":"Point\", \"x\": \"","coordinates":[1,2]}""").Should().BeNull();
        }

        [TestMethod]
        public void IsGeometryAnswersForALiteralValue()
        {
            CosmosGeoJson.IsGeometry("""{"type":"Point","coordinates":[1,2]}""").Should().BeTrue();
            CosmosGeoJson.IsGeometry("Seattle").Should().BeFalse();
            CosmosGeoJson.IsGeometry(42L).Should().BeFalse();
            CosmosGeoJson.IsGeometry(null).Should().BeFalse();
        }

    }

}
