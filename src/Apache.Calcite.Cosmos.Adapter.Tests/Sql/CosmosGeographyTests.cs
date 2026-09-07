using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Geography.Rel.Type;
using Apache.Calcite.Geography.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The geography type on a column, and the translation of the operators over it.
    /// </summary>
    /// <remarks>
    /// The prefix is Calcite's problem rather than the service's. Calcite's own <c>ST_*</c> are planar and
    /// answer in the units of an unprojected system, so the geodesic reading needs a name of its own; the
    /// service has only the one reading and spells it unprefixed. There is no type telling the two apart —
    /// a geography and a geometry are the same type carried by the same class, and the operator's name is
    /// the whole of the marking. See <c>DESIGN.md</c>.
    /// </remarks>
    [TestClass]
    public class CosmosGeographyTests
    {

        const string Point = "{\"type\":\"Point\",\"coordinates\":[0.5,0.25]}";

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath> _fields;

        public CosmosGeographyTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath>
            {
                CosmosPath.Root("c").Property("location"),   // 0 — a geography
                CosmosPath.Root("c").Property("n"),          // 1
                CosmosPath.Root("c"),                        // 2 — the document, as _JSON binds it
            };
        }

        CosmosRexTranslator Translator(CosmosContainerMetadata? container = null) => new(_rex, _fields, new CosmosParameterList(), container: container);

        string Translate(SqlOperator op, params RexNode[] operands) => Translator().Translate(_rex.makeCall(op, operands));

        RexNode Geo() => _rex.makeInputRef(GeographyTypes.Of(_types), 0);

        RexNode Document() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 2);

        RexNode Stored() => _rex.makeCall(GeographyOperatorTable.StGeogGeomFromGeoJson,
            _rex.makeCall(SqlStdOperatorTable.JSON_QUERY,
                Document(),
                _rex.makeLiteral("$.location", _types.createSqlType(SqlTypeName.VARCHAR, 11))));

        RexNode Num() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.DOUBLE), 1);

        RexNode Literal() => _rex.makeCall(GeographyOperatorTable.StGeogGeomFromGeoJson, _rex.makeLiteral(Point, _types.createSqlType(SqlTypeName.VARCHAR, Point.Length)));

        // ── Translation ───────────────────────────────────────────────────────────

        /// <remarks>
        /// The prefix goes away because the service has only the one reading of a coordinate.
        /// </remarks>
        [TestMethod]
        public void TheDirectOperatorsDropTheirPrefix()
        {
            Translate(GeographyOperatorTable.StGeogDistance, Geo(), Literal()).Should().Be($"ST_DISTANCE(c.location, {Point})");
            Translate(GeographyOperatorTable.StGeogWithin, Geo(), Literal()).Should().Be($"ST_WITHIN(c.location, {Point})");
            Translate(GeographyOperatorTable.StGeogIntersects, Geo(), Literal()).Should().Be($"ST_INTERSECTS(c.location, {Point})");
            Translate(GeographyOperatorTable.StGeogIsValid, Geo()).Should().Be("ST_ISVALID(c.location)");
        }

        /// <summary>
        /// Cosmos has no <c>ST_DWITHIN</c>, so it is written as the comparison it is defined as.
        /// </summary>
        /// <remarks>
        /// Which is also the form the reference documents the spatial index as answering, so this is the
        /// shape the service wanted rather than a consolation.
        /// </remarks>
        [TestMethod]
        public void DWithinBecomesADistanceComparison()
        {
            Translate(GeographyOperatorTable.StGeogDWithin, Geo(), Literal(), Num())
                .Should().Be($"ST_DISTANCE(c.location, {Point}) <= c.n");
        }

        /// <summary>
        /// A geography in a statement is the GeoJSON object; there is no constructor to translate.
        /// </summary>
        /// <remarks>
        /// Written out rather than parameterised. A string parameter would arrive as a string, and
        /// <c>ST_DISTANCE</c> over one is not the same query.
        /// </remarks>
        [TestMethod]
        public void AConstructorOverALiteralBecomesTheObject()
        {
            Translate(GeographyOperatorTable.StGeogIsValid, Literal()).Should().Be($"ST_ISVALID({Point})");
        }

        /// <summary>
        /// A constructor over a computed string is declined rather than guessed at.
        /// </summary>
        /// <remarks>
        /// A literal is written out and a document path is written as the path; anything else would have to
        /// be evaluated to be rendered, and evaluating it is what the service is being asked to do. Such a
        /// call stays in process, where the geography package answers it.
        /// </remarks>
        [TestMethod]
        public void AConstructorOverAComputedStringIsDeclined()
        {
            var computed = _rex.makeCall(SqlStdOperatorTable.UPPER, _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 1));
            var call = _rex.makeCall(GeographyOperatorTable.StGeogIsValid,
                _rex.makeCall(GeographyOperatorTable.StGeogGeomFromGeoJson, computed));

            Translator().TryTranslate(call, out _).Should().BeFalse();
        }

        /// <summary>
        /// A geodesic call is refused over a container that reads its coordinates as a plane.
        /// </summary>
        /// <remarks>
        /// The Cosmos spelling is the unprefixed one, so what a rendered <c>ST_DISTANCE</c> means at the
        /// service is decided by <c>geospatialConfig</c> rather than by the name in the query. Over a
        /// planar container the service would answer the planar question in the units of the coordinate
        /// system, and say nothing about having done so — which is why this refuses while planning.
        /// </remarks>
        [TestMethod]
        public void APlanarContainerRefusesAGeodesicCall()
        {
            var planar = new CosmosContainerMetadata("products", readsGeography: false);
            var call = _rex.makeCall(GeographyOperatorTable.StGeogIsValid, Geo());

            Translator(planar).TryTranslate(call, out _).Should().BeFalse();

            var geodesic = new CosmosContainerMetadata("products", readsGeography: true);
            Translator(geodesic).TryTranslate(call, out _).Should().BeTrue();
        }


        /// <summary>
        /// A stored geography reaches the service as the path, not as parsed-out text.
        /// </summary>
        /// <remarks>
        /// <c>ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."_JSON", '$.location'))</c> is how a shape in a document
        /// reaches an operator at all — no column is typed as a geometry, so it has to be parsed out of
        /// text. In process that is what happens. Pushed down it is not: Cosmos reads the property itself
        /// as the shape, so the text and the parsing are a round trip it never needed.
        /// </remarks>
        [TestMethod]
        public void AStoredGeographyPushesAsItsPath()
        {
            Translate(GeographyOperatorTable.StGeogIsValid, Stored()).Should().Be("ST_ISVALID(c.location)");

            Translate(GeographyOperatorTable.StGeogDWithin, Stored(), Literal(), Num())
                .Should().Be($"ST_DISTANCE(c.location, {Point}) <= c.n");
        }


        /// <summary>
        /// The geometry type is a GeoJSON member, so it pushes as a path rather than as a function.
        /// </summary>
        /// <remarks>
        /// No spatial function is involved and no spatial index matters — the service reads a property.
        /// The two vocabularies agree for anything a container can hold: JTS also spells `LinearRing`,
        /// which GeoJSON has no member for, so a stored shape cannot be one.
        /// </remarks>
        [TestMethod]
        public void TheGeometryTypeIsAMemberOfTheShape()
        {
            Translate(GeographyOperatorTable.StGeogGeometryType, Stored()).Should().Be("c.location.type");
        }

        /// <summary>
        /// A geometry built in the query has no path, so the member read is declined.
        /// </summary>
        /// <remarks>
        /// Which is also where the two vocabularies could have differed: a `LinearRing` cannot come out
        /// of a document, and this is the case that would have let one in.
        /// </remarks>
        [TestMethod]
        public void AConstructedGeometryHasNoTypeMemberToRead()
        {
            var call = _rex.makeCall(GeographyOperatorTable.StGeogGeometryType, Literal());

            Translator().TryTranslate(call, out _).Should().BeFalse();
        }


        /// <summary>
        /// Serialising a stored geography is the property, read as the JSON the service sent.
        /// </summary>
        /// <remarks>
        /// The document already holds the GeoJSON, so parsing it into a geometry and writing it back out
        /// is a round trip. What comes back is an object where the projection is declared <c>VARCHAR</c>,
        /// which is the reading the <c>_JSON</c> column takes and for the same reason.
        /// </remarks>
        [TestMethod]
        public void SerialisingAStoredGeographyIsTheProperty()
        {
            var call = _rex.makeCall(GeographyOperatorTable.StGeogAsGeoJson, Stored());

            Translator().TranslateProjection(call, out var reading).Should().Be("c.location");
            reading.Should().Be(CosmosReading.Json);
        }

        /// <summary>
        /// A geography built in the query has nothing stored to select, so it is serialised in process.
        /// </summary>
        [TestMethod]
        public void SerialisingAConstructedGeographyIsDeclined()
        {
            var call = _rex.makeCall(GeographyOperatorTable.StGeogAsGeoJson, Literal());

            Translator().TryTranslateProjection(call, out _, out _).Should().BeFalse();
        }

        /// <summary>
        /// In a predicate the same call is declined: the column carries text where the path carries an
        /// object, and a comparison against one is not a comparison against the other.
        /// </summary>
        [TestMethod]
        public void SerialisingIsAProjectionOnly()
        {
            var call = _rex.makeCall(GeographyOperatorTable.StGeogAsGeoJson, Stored());

            Translator().TryTranslate(call, out _).Should().BeFalse();
        }

    }

}
