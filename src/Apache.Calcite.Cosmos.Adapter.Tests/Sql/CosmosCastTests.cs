using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The one cast that renders, and the many that do not.
    /// </summary>
    /// <remarks>
    /// A cast over a <em>document value</em> is refused, because Calcite converts the stored value and
    /// the service compares it as it stands, so the two select different documents. A cast over a
    /// <em>literal</em> is the exception: its value is known while the statement is being written.
    /// </remarks>
    [TestClass]
    public class CosmosCastTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath?> _fields;

        public CosmosCastTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath?> { CosmosPath.Root("c").Property("embedding") };
        }

        CosmosRexTranslator Translator() => new(_rex, _fields, new CosmosParameterList());

        RexNode Path() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.ANY), 0);

        RexNode Bound(double value) =>
            _rex.makeCast(_types.createSqlType(SqlTypeName.DOUBLE), _rex.makeExactLiteral(new java.math.BigDecimal(value)));

        /// <remarks>
        /// Comparing against a function that returns a double coerces the literal to match, so the
        /// comparison arrives with a cast wrapped around the constant. Declining that cast declines the
        /// predicate â€” which for <c>VECTORDISTANCE</c> is the predicate that bounds the search.
        /// </remarks>
        [TestMethod]
        public void AComparisonAgainstACastLiteralRenders()
        {
            var distance = _rex.makeCall(CosmosOperators.VectorDistance, Path(), Path());

            Translator().Translate(_rex.makeCall(SqlStdOperatorTable.LESS_THAN, distance, Bound(0.5)))
                .Should().Be("(VECTORDISTANCE(c.embedding, c.embedding) < @p0)");
        }

        /// <remarks>
        /// And the value is bound, not inlined â€” it is the part that varies with what is being asked.
        /// </remarks>
        [TestMethod]
        public void TheCastLiteralIsBound()
        {
            var parameters = new CosmosParameterList();
            var translator = new CosmosRexTranslator(_rex, _fields, parameters);
            var distance = _rex.makeCall(CosmosOperators.VectorDistance, Path(), Path());

            translator.Translate(_rex.makeCall(SqlStdOperatorTable.LESS_THAN, distance, Bound(0.5)));

            parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(0.5d);
        }

        /// <remarks>
        /// A cast of a document value is still refused, and everything the design says about that
        /// stands.
        /// </remarks>
        [TestMethod]
        public void ACastOfADocumentValueIsDeclined()
        {
            Translator().TryTranslate(_rex.makeCast(_types.createSqlType(SqlTypeName.DOUBLE), Path()), out _)
                .Should().BeFalse();
        }

        /// <remarks>
        /// And a cast of a literal to an exact type, which truncates or throws depending on the value â€”
        /// a question worth answering when something asks it, and nothing does.
        /// </remarks>
        [TestMethod]
        public void ACastToAnExactTypeIsDeclined()
        {
            // Abstract, because makeCast folds a cast of a literal to a literal and there would be no
            // cast left to decline.
            var cast = _rex.makeAbstractCast(_types.createSqlType(SqlTypeName.INTEGER), _rex.makeExactLiteral(new java.math.BigDecimal(5)), false);

            Translator().TryTranslate(cast, out _).Should().BeFalse();
        }

    }

}
