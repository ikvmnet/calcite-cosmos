using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// Pushing Calcite's spatial predicates, which this adapter does not define.
    /// </summary>
    /// <remarks>
    /// The whole translation turns on one thing: <c>ST_GEOMFROMGEOJSON</c> over a document path is how
    /// a query gets a geometry out of a schemaless container, so a predicate whose arguments are that
    /// constructor is a predicate the service can answer about the stored value. Stripping the
    /// constructors is the translation.
    /// </remarks>
    [TestClass]
    public class CosmosSpatialTests
    {

        const string PolygonText = """{"type":"Polygon","coordinates":[[[-123.0,47.0],[-121.0,47.0],[-121.0,48.0],[-123.0,48.0],[-123.0,47.0]]]}""";

        const string PolygonSql = """{ "type": "Polygon", "coordinates": [[[-123, 47], [-121, 47], [-121, 48], [-123, 48], [-123, 47]]] }""";

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath?> _fields;

        /// <summary>
        /// Stand-ins for Calcite's, which cannot be called over an <c>ANY</c> operand from a test: what
        /// is being exercised is the translator's recognition, and it matches by name.
        /// </summary>
        static readonly SqlFunction Within = Spatial("ST_WITHIN", ReturnTypes.BOOLEAN_NULLABLE, 2);

        static readonly SqlFunction Intersects = Spatial("ST_INTERSECTS", ReturnTypes.BOOLEAN_NULLABLE, 2);

        static readonly SqlFunction IsValid = Spatial("ST_ISVALID", ReturnTypes.BOOLEAN_NULLABLE, 1);

        static readonly SqlFunction Distance = Spatial("ST_DISTANCE", ReturnTypes.DOUBLE, 2);

        static readonly SqlFunction FromGeoJson = Spatial("ST_GEOMFROMGEOJSON", ReturnTypes.@explicit(SqlTypeName.ANY), 1);

        static SqlFunction Spatial(string name, SqlReturnTypeInference returnType, int operands) =>
            SqlBasicFunction.create(name, returnType, OperandTypes.variadic(SqlOperandCountRanges.between(operands, operands)), SqlFunctionCategory.SYSTEM);

        public CosmosSpatialTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath?>
            {
                CosmosPath.Root("c").Property("location"),   // 0
                null,                                        // 1 — a computed projection
            };
        }

        CosmosRexTranslator Translator() => new(_rex, _fields, new CosmosParameterList());

        RexNode Location() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.ANY), 0);

        RexNode Computed() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.ANY), 1);

        RexNode Text(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

        RexNode Geometry(RexNode of) => _rex.makeCall(FromGeoJson, of);

        /// <summary>
        /// The cast Calcite inserts around an untyped value handed to a function taking text.
        /// </summary>
        RexNode Coerced(RexNode of) => _rex.makeAbstractCast(_types.createSqlType(SqlTypeName.VARCHAR), of, false);

        string Translate(SqlOperator op, params RexNode[] operands) =>
            Translator().Translate(_rex.makeCall(op, operands));

        bool CanTranslate(SqlOperator op, params RexNode[] operands) =>
            Translator().TryTranslate(_rex.makeCall(op, operands), out _);

        /// <remarks>
        /// The constructors come off: the service reads the stored GeoJSON at the path directly, and the
        /// literal becomes the object beside it.
        /// </remarks>
        [TestMethod]
        public void APredicateOverAPathAndALiteralPushes()
        {
            Translate(Within, Geometry(Location()), Geometry(Text(PolygonText)))
                .Should().Be($"ST_WITHIN(c.location, {PolygonSql})");

            Translate(Intersects, Geometry(Location()), Geometry(Text(PolygonText)))
                .Should().Be($"ST_INTERSECTS(c.location, {PolygonSql})");
        }

        [TestMethod]
        public void IsValidTakesOneArgument()
        {
            Translate(IsValid, Geometry(Location())).Should().Be("ST_ISVALID(c.location)");
        }

        /// <remarks>
        /// Calcite coerces an <c>ANY</c> handed to a function taking text, so the cast is in the tree
        /// whether or not the query wrote one. Looking through it is what makes both spellings push.
        /// </remarks>
        [TestMethod]
        public void TheCoercionCalciteInsertsIsLookedThrough()
        {
            Translate(Within, Geometry(Coerced(Location())), Geometry(Coerced(Text(PolygonText))))
                .Should().Be($"ST_WITHIN(c.location, {PolygonSql})");
        }

        /// <remarks>
        /// <b>Without the constructor there is nothing to recognise.</b> A geometry reaching the
        /// predicate any other way is one Calcite computed, and the service has no way to be handed it.
        /// </remarks>
        [TestMethod]
        public void APredicateWithoutTheConstructorIsDeclined()
        {
            CanTranslate(Within, Location(), Text(PolygonText)).Should().BeFalse();
            CanTranslate(Within, Geometry(Location()), Text(PolygonText)).Should().BeFalse();
        }

        /// <remarks>
        /// The document side must resolve to a path, which is what the service reads. A computed column
        /// addresses nothing it can name.
        /// </remarks>
        [TestMethod]
        public void APredicateOverAComputedColumnIsDeclined()
        {
            CanTranslate(Within, Geometry(Computed()), Geometry(Text(PolygonText))).Should().BeFalse();
        }

        /// <remarks>
        /// And text that is not a geometry is not one — rendering it would put caller text inside the
        /// statement.
        /// </remarks>
        [TestMethod]
        public void APredicateOverTextThatIsNotAGeometryIsDeclined()
        {
            CanTranslate(Within, Geometry(Location()), Geometry(Text("Seattle"))).Should().BeFalse();
        }

        /// <remarks>
        /// <b><c>ST_DISTANCE</c> is not pushed at all</b>, and it is the one whose disagreement is a
        /// wrong number rather than a boundary case: Cosmos answers geodesic metres where Calcite
        /// answers planar degrees, so a bound compared against it would mean something else entirely.
        /// </remarks>
        [TestMethod]
        public void DistanceIsNotPushed()
        {
            CanTranslate(Distance, Geometry(Location()), Geometry(Text(PolygonText))).Should().BeFalse();
        }

        /// <remarks>
        /// The geometry is inlined rather than bound, which is this adapter's one departure from binding
        /// a literal: an object in that position is the documented form, and whether a parameter there
        /// is served off the spatial index is unmeasured.
        /// </remarks>
        [TestMethod]
        public void TheGeometryIsInlinedRatherThanBound()
        {
            var parameters = new CosmosParameterList();
            var translator = new CosmosRexTranslator(_rex, _fields, parameters);

            translator.Translate(_rex.makeCall(Within, Geometry(Location()), Geometry(Text(PolygonText))));

            parameters.Parameters.Should().BeEmpty();
        }

    }

}
