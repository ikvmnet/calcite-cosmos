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
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The geography type on a column, and the translation of the operators over it.
    /// </summary>
    /// <remarks>
    /// The prefix is Calcite's problem rather than the service's: Calcite's own <c>ST_*</c> are planar and
    /// answer in the units of an unprojected system, so the geodesic reading needs a name of its own,
    /// while Cosmos has only the geodesic reading and spells it unprefixed. See <c>DESIGN.md</c> under
    /// <i>Spatial needed a type Calcite does not have, and now has one</i>.
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
            };
        }

        CosmosRexTranslator Translator() => new(_rex, _fields, new CosmosParameterList());

        string Translate(SqlOperator op, params RexNode[] operands) => Translator().Translate(_rex.makeCall(op, operands));

        RexNode Geo() => _rex.makeInputRef(GeographyTypes.Of(_types), 0);

        RexNode Num() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.DOUBLE), 1);

        RexNode Literal() => _rex.makeCall(GeographyOperatorTable.StGeogGeomFromGeoJson, _rex.makeLiteral(Point, _types.createSqlType(SqlTypeName.VARCHAR, Point.Length)));

        // ── The type on a column ──────────────────────────────────────────────────

        /// <remarks>
        /// A spatial path is promoted because it cannot be reached any other way — nothing converts the
        /// <c>ANY</c> a map lookup yields into a geometry.
        /// </remarks>
        [TestMethod]
        public void ADeclaredSpatialPathIsAGeographyColumn()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/category" }, geographyPaths: new[] { "/location" }));

            var rowType = table.getRowType(_types);
            var field = rowType.getField("location", true, false);

            field.Should().NotBeNull();
            GeographyTypes.IsGeography(field.getType()).Should().BeTrue();
        }

        /// <summary>
        /// A container declaring nothing spatial gains no column, and its documents are unaffected.
        /// </summary>
        [TestMethod]
        public void WithoutADeclarationThereIsNoGeographyColumn()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/category" }));

            table.getRowType(_types).getField("location", true, false).Should().BeNull();
        }

        /// <summary>
        /// The type is the marking, so it has to survive being put on a column.
        /// </summary>
        /// <remarks>
        /// <c>RelDataTypeFactoryImpl</c> answers a nullability change on any <c>JavaType</c> with a plain
        /// <c>JavaType</c> over the same class, which would hand back the very <c>GEOMETRY</c> the type
        /// exists to be distinguishable from. Nothing on the way to a row type may do that.
        /// </remarks>
        [TestMethod]
        public void TheMarkingSurvivesTheRowType()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", geographyPaths: new[] { "/location" }));

            var type = table.getRowType(_types).getField("location", true, false).getType();

            type.getFullTypeString().Should().Contain("GEOGRAPHY");
            GeographyTypes.IsGeometry(type).Should().BeFalse();
        }

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
        /// A constructor over anything but a literal is declined rather than guessed at.
        /// </summary>
        /// <remarks>
        /// Rendering one would mean evaluating it, and evaluating it is what the service is being asked
        /// to do. A declined call leaves the predicate in process, where the geography package answers it.
        /// </remarks>
        [TestMethod]
        public void AConstructorOverAValueIsDeclined()
        {
            var call = _rex.makeCall(GeographyOperatorTable.StGeogIsValid,
                _rex.makeCall(GeographyOperatorTable.StGeogGeomFromGeoJson, _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 1)));

            Translator().TryTranslate(call, out _).Should().BeFalse();
        }

    }

}
