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
    /// The spatial functions, which arrive through operators of this adapter's own even though Calcite
    /// has four of the same name.
    /// </summary>
    /// <remarks>
    /// Cosmos's are geodesic over GeoJSON and answer in metres; Calcite's are planar over a geometry and
    /// answer in the units of the coordinate system. Same spellings, different numbers — so the
    /// translator dispatches these on operator identity rather than on the name it dispatches everything
    /// else by.
    /// </remarks>
    [TestClass]
    public class CosmosSpatialTests
    {

        const string PointText = """{"type":"Point","coordinates":[-122.12,47.66]}""";

        const string PolygonText = """{"type":"Polygon","coordinates":[[[-123.0,47.0],[-121.0,47.0],[-121.0,48.0],[-123.0,48.0],[-123.0,47.0]]]}""";

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath?> _fields;

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

        RexNode Geometry(string text) => _rex.makeLiteral(text, _types.createSqlType(SqlTypeName.VARCHAR, text.Length));

        string Translate(SqlOperator op, params RexNode[] operands) =>
            Translator().Translate(_rex.makeCall(op, operands));

        bool CanTranslate(SqlOperator op, params RexNode[] operands) =>
            Translator().TryTranslate(_rex.makeCall(op, operands), out _);

        [TestMethod]
        public void DistanceTakesAPathAndAGeometry()
        {
            Translate(CosmosOperators.StDistance, Location(), Geometry(PointText))
                .Should().Be("""ST_DISTANCE(c.location, { "type": "Point", "coordinates": [-122.12, 47.66] })""");
        }

        [TestMethod]
        public void WithinAndIntersectsTakeAPolygon()
        {
            const string rendered = """{ "type": "Polygon", "coordinates": [[[-123, 47], [-121, 47], [-121, 48], [-123, 48], [-123, 47]]] }""";

            Translate(CosmosOperators.StWithin, Location(), Geometry(PolygonText)).Should().Be($"ST_WITHIN(c.location, {rendered})");
            Translate(CosmosOperators.StIntersects, Location(), Geometry(PolygonText)).Should().Be($"ST_INTERSECTS(c.location, {rendered})");
        }

        [TestMethod]
        public void IsValidTakesOneSpatialExpression()
        {
            Translate(CosmosOperators.StIsValid, Location()).Should().Be("ST_ISVALID(c.location)");
        }

        /// <remarks>
        /// The reference's own example asks whether a literal is a valid geometry, so a constant
        /// argument is not a mistake to refuse — an argument that is neither a path nor a geometry is.
        /// </remarks>
        [TestMethod]
        public void IsValidTakesAGeometryToo()
        {
            Translate(CosmosOperators.StIsValid, Geometry(PointText))
                .Should().Be("""ST_ISVALID({ "type": "Point", "coordinates": [-122.12, 47.66] })""");
        }

        /// <remarks>
        /// Both sides may be document paths: the reference calls each argument a spatial expression, and
        /// two paths is a distance between two properties of the same document.
        /// </remarks>
        [TestMethod]
        public void DistanceTakesTwoPaths()
        {
            Translate(CosmosOperators.StDistance, Location(), Location()).Should().Be("ST_DISTANCE(c.location, c.location)");
        }

        /// <remarks>
        /// The document side is held to a path for the reason a full text predicate's is: an expression
        /// cannot be served off the spatial index, and that index is the reason to push the call down.
        /// </remarks>
        [TestMethod]
        public void ACallOverAComputedColumnIsDeclined()
        {
            CanTranslate(CosmosOperators.StDistance, Computed(), Geometry(PointText)).Should().BeFalse();
            CanTranslate(CosmosOperators.StIsValid, Computed()).Should().BeFalse();
        }

        /// <remarks>
        /// A string that is not a geometry is not a geometry. Rendering it would put caller text inside
        /// the statement, which is what parsing and re-emitting exists to prevent.
        /// </remarks>
        [TestMethod]
        public void ACallOverTextThatIsNotAGeometryIsDeclined()
        {
            CanTranslate(CosmosOperators.StDistance, Location(), Geometry("Seattle")).Should().BeFalse();
            CanTranslate(CosmosOperators.StDistance, Location(), Geometry("""{"lat":47.6,"lon":-122.1}""")).Should().BeFalse();
        }

        /// <remarks>
        /// The geometry is inlined rather than bound, which is the one place this adapter departs from
        /// binding a literal. An object in the geometry position is the documented form, and whether a
        /// parameter there is served off the spatial index is unmeasured — see <c>DESIGN.md</c>.
        /// </remarks>
        [TestMethod]
        public void TheGeometryIsInlinedRatherThanBound()
        {
            var parameters = new CosmosParameterList();
            var translator = new CosmosRexTranslator(_rex, _fields, parameters);

            translator.Translate(_rex.makeCall(CosmosOperators.StWithin, Location(), Geometry(PolygonText)));

            parameters.Parameters.Should().BeEmpty();
        }


        // ── The name collision with Calcite's spatial library ──────────────

        /// <remarks>
        /// The collision is real, and this is what says so: Calcite's library defines operators of these
        /// names, and they are not these operators.
        /// </remarks>
        [TestMethod]
        public void CalciteDefinesTheSameFourNames()
        {
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.spatialInstance().getOperatorList();
            var found = new List<string>();

            for (var i = 0; i < operators.size(); i++)
            {
                var op = (SqlOperator)operators.get(i);

                if (CosmosOperators.IsSpatialName(op.getName()))
                {
                    found.Add(op.getName());
                    CosmosOperators.IsSpatial(op).Should().BeFalse("Calcite's '{0}' is not this adapter's", op.getName());
                }
            }

            found.Should().HaveCount(4, "Calcite spells all four the same way");
        }

        /// <remarks>
        /// So a call under one of those names that is not one of these operators declines, and the
        /// message says why rather than reporting an unknown function. Calcite then evaluates it in
        /// process with the planar semantics the caller asked for by naming it.
        /// </remarks>
        [TestMethod]
        public void ACallUnderTheSameNameByAnotherOperatorIsDeclined()
        {
            // A stand-in for Calcite's, which cannot be called over an ANY operand: what is being tested
            // is that the name alone does not admit it.
            var lookalike = SqlBasicFunction.create(
                "ST_Distance",
                ReturnTypes.DOUBLE,
                OperandTypes.variadic(SqlOperandCountRanges.between(2, 2)),
                SqlFunctionCategory.SYSTEM);

            var act = () => Translate(lookalike, Location(), Geometry(PointText));

            act.Should().Throw<CosmosTranslationException>().WithMessage("*not this adapter's spatial operator*");
        }

        [TestMethod]
        public void TheOperatorTableCarriesEverySpatialFunction()
        {
            var names = new List<string>();
            var operators = CosmosOperators.Instance.getOperatorList();

            for (var i = 0; i < operators.size(); i++)
                names.Add(((SqlOperator)operators.get(i)).getName());

            names.Should().Contain(new[] { "ST_DISTANCE", "ST_WITHIN", "ST_INTERSECTS", "ST_ISVALID" });
        }

        /// <remarks>
        /// A distance is a double so that <c>ORDER BY ST_DISTANCE(…)</c> is expressible, and non-nullable
        /// so that the clause is not then refused for a null placement Cosmos will not honour. See
        /// <c>DESIGN.md</c> for what that declaration costs.
        /// </remarks>
        [TestMethod]
        public void ADistanceIsANonNullableDouble()
        {
            var call = _rex.makeCall(CosmosOperators.StDistance, Location(), Geometry(PointText));

            call.getType().getSqlTypeName().Should().Be(SqlTypeName.DOUBLE);
            call.getType().isNullable().Should().BeFalse();
        }


        // ── The comparison a proximity predicate is written as ─────────────

        /// <remarks>
        /// <c>ST_DISTANCE(…) &lt; 5000</c> coerces the integer literal to the function's type, so the
        /// predicate that makes a proximity query cheap arrives with a cast around the constant.
        /// Declining that cast declines the predicate.
        /// </remarks>
        [TestMethod]
        public void AComparisonAgainstACastLiteralRenders()
        {
            var distance = _rex.makeCall(CosmosOperators.StDistance, Location(), Geometry(PointText));
            var bound = _rex.makeCast(_types.createSqlType(SqlTypeName.DOUBLE), _rex.makeExactLiteral(new java.math.BigDecimal(5000)));

            Translate(org.apache.calcite.sql.fun.SqlStdOperatorTable.LESS_THAN, distance, bound)
                .Should().Be("""(ST_DISTANCE(c.location, { "type": "Point", "coordinates": [-122.12, 47.66] }) < @p0)""");
        }

        /// <remarks>
        /// And the value is bound, not inlined — it is the one part of a proximity predicate that varies
        /// with what is being asked.
        /// </remarks>
        [TestMethod]
        public void TheBoundOfAProximityPredicateIsBound()
        {
            var parameters = new CosmosParameterList();
            var translator = new CosmosRexTranslator(_rex, _fields, parameters);

            var distance = _rex.makeCall(CosmosOperators.StDistance, Location(), Geometry(PointText));
            var bound = _rex.makeCast(_types.createSqlType(SqlTypeName.DOUBLE), _rex.makeExactLiteral(new java.math.BigDecimal(5000)));

            translator.Translate(_rex.makeCall(org.apache.calcite.sql.fun.SqlStdOperatorTable.LESS_THAN, distance, bound));

            parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(5000d);
        }

        /// <remarks>
        /// A cast of a <em>document value</em> is still refused, and everything the design says about
        /// that stands: Calcite converts the stored value and the service compares it as it stands.
        /// </remarks>
        [TestMethod]
        public void ACastOfADocumentValueIsStillDeclined()
        {
            var cast = _rex.makeCast(_types.createSqlType(SqlTypeName.DOUBLE), Location());

            Translator().TryTranslate(cast, out _).Should().BeFalse();
        }

    }

}
