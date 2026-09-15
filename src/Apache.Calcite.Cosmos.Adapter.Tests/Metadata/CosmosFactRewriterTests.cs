using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// The one rewrite the declared facts drive: a comparison against a <c>UUID</c>, lowered to the
    /// string comparison the service can evaluate.
    /// </summary>
    /// <remarks>
    /// What is being pinned is not the rendering but the <em>condition</em> on it. The same predicate
    /// lowers or does not depending on what the container declared and on what the query proved, and
    /// both directions have to hold or the feature is either useless or wrong.
    /// </remarks>
    [TestClass]
    public class CosmosFactRewriterTests
    {

        const string Canonical = "123e4567-e89b-12d3-a456-426614174000";

        const string Discriminated = """
        {
          "oneOf": [
            { "properties": { "kind": { "const": "A" } } },
            { "properties": { "kind": { "const": "B" },
                              "ref": { "type": "string",
                                       "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }
          ]
        }
        """;

        const string Unconditional = """
        { "properties": { "ref": { "type": "string",
                                   "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }
        """;

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath?> _fields;

        public CosmosFactRewriterTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath?>
            {
                CosmosPath.Root("c").Property("ref"),    // 0
                CosmosPath.Root("c").Property("kind"),   // 1
            };
        }

        static CosmosContainerMetadata Container(string? schema) =>
            schema is null
                ? new CosmosContainerMetadata("items", new[] { "/ref" })
                : new CosmosContainerMetadata("items", new[] { "/ref" })
                    .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(schema)));

        RexNode Ref(int index, SqlTypeName type) => _rex.makeInputRef(_types.createSqlType(type), index);

        RexNode Uuid(string value) =>
            _rex.makeLiteral(java.util.UUID.fromString(value), _types.createSqlType(SqlTypeName.UUID), false);

        RexNode Str(string value) => _rex.makeLiteral(value);

        /// <summary>
        /// <c>CAST(&lt;ref&gt; AS UUID) = UUID'…'</c>, the shape a view over a typed column produces.
        /// </summary>
        RexNode UuidEquality() =>
            _rex.makeCall(SqlStdOperatorTable.EQUALS,
                _rex.makeCast(_types.createSqlType(SqlTypeName.UUID), Ref(0, SqlTypeName.VARCHAR)),
                Uuid(Canonical));

        RexNode KindIs(string value) => _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1, SqlTypeName.VARCHAR), Str(value));

        RexNode And(params RexNode[] operands)
        {
            var list = new java.util.ArrayList();
            foreach (var operand in operands)
                list.add(operand);

            return RexUtil.composeConjunction(_rex, list);
        }

        string Rewrite(RexNode condition, string? schema) =>
            CosmosFactRewriter.Rewrite(condition, _fields, Container(schema), "c", _rex).ToString();

        [TestMethod]
        public void ADeclaredFormLowersTheComparisonToAStringEquality()
        {
            Rewrite(UuidEquality(), Unconditional).Should().Be($"=($0, '{Canonical}')",
                "the stored spelling is the canonical one, so comparing the strings answers what comparing the values answers");
        }

        [TestMethod]
        public void NothingDeclaredLeavesThePredicateAlone()
        {
            var original = UuidEquality();

            Rewrite(original, null).Should().Be(original.ToString(),
                "which is every container today, and asking has to cost nothing");
        }

        [TestMethod]
        public void AGuardedFormNeedsTheQueryToHaveProvenTheGuard()
        {
            var alone = UuidEquality();

            Rewrite(alone, Discriminated).Should().Be(alone.ToString(),
                "a document of the other kind may not carry the path at all, so nothing is known about it");

            Rewrite(And(KindIs("A"), UuidEquality()), Discriminated).Should().Contain("CAST",
                "the wrong discriminator proves nothing");

            Rewrite(And(KindIs("B"), UuidEquality()), Discriminated).Should().Be($"AND(=($1, 'B'), =($0, '{Canonical}'))",
                "the guard is proven by a conjunct of the very predicate being rewritten, and that conjunct stays");
        }

        [TestMethod]
        public void TheLoweredFormIsWhatRoutingReads()
        {
            var rewritten = CosmosFactRewriter.Rewrite(UuidEquality(), _fields, Container(Unconditional), "c", _rex);

            CosmosPartitionKeyExtractor.TryExtract(rewritten, _fields, Container(Unconditional), "c", out var values)
                .Should().BeTrue("a plain string equality on the partition key path is what pins a partition");

            values.Should().ContainSingle().Which.Should().Be(Canonical);

            CosmosPartitionKeyExtractor.TryExtract(UuidEquality(), _fields, Container(Unconditional), "c", out _)
                .Should().BeFalse("and the cast form pins nothing, which is the state of things today");
        }

        [TestMethod]
        public void TheLiteralIsWrittenInTheStoredSpelling()
        {
            var upper = _rex.makeCall(SqlStdOperatorTable.EQUALS,
                _rex.makeCast(_types.createSqlType(SqlTypeName.UUID), Ref(0, SqlTypeName.VARCHAR)),
                _rex.makeLiteral(java.util.UUID.fromString(Canonical.ToUpperInvariant()), _types.createSqlType(SqlTypeName.UUID), false));

            Rewrite(upper, Unconditional).Should().Be($"=($0, '{Canonical}')",
                "the value is the same however the query spelled it, and the stored form is the canonical one");
        }

        [TestMethod]
        public void OnlyAConjunctionIsDescendedInto()
        {
            var disjunction = _rex.makeCall(SqlStdOperatorTable.OR, UuidEquality(), KindIs("B"));

            Rewrite(disjunction, Unconditional).Should().Be(disjunction.ToString(),
                "a fact is proven from the conjuncts beside it, and under a disjunction those do not hold of every row a branch keeps");
        }

        [TestMethod]
        public void APathTheSchemaSaysNothingAboutIsLeftAlone()
        {
            var other = _rex.makeCall(SqlStdOperatorTable.EQUALS,
                _rex.makeCast(_types.createSqlType(SqlTypeName.UUID), Ref(1, SqlTypeName.VARCHAR)),
                Uuid(Canonical));

            Rewrite(other, Unconditional).Should().Be(other.ToString());
        }

    }

}
