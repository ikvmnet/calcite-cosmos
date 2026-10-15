using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Reading a filter into the IR: each spelling of a path Calcite produces becomes the reading it means, and what is not
    /// read is opaque.
    /// </summary>
    public sealed class CosmosPredicateLoaderTests
    {

        static readonly JsonDocumentPath Label = P("label");
        static readonly JsonDocumentPath Kind = P("kind");
        static readonly JsonDocumentPath Category = P("category");
        static readonly JsonDocumentPath N = P("n");

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath?> _fields;
        readonly List<CosmosReading> _readings;

        public CosmosPredicateLoaderTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath?>
            {
                CosmosPath.Root("c"),                       // 0: DOC
                CosmosPath.Root("c").Property("category"),  // 1: a promoted VARIANT column
                CosmosPath.Root("c").Property("label"),     // 2: a projection of JSON_VALUE, read as text
            };
            _readings = new List<CosmosReading> { CosmosReading.Typed, CosmosReading.Typed, CosmosReading.Text };
        }

        org.apache.calcite.rel.type.RelDataType Sql(SqlTypeName name) => _types.createSqlType(name);

        RexNode Doc => _rex.makeInputRef(Sql(SqlTypeName.VARCHAR), 0);

        RexNode Variant => _rex.makeInputRef(_types.createTypeWithNullability(Sql(SqlTypeName.VARIANT), true), 1);

        RexNode TextField => _rex.makeInputRef(Sql(SqlTypeName.VARCHAR), 2);

        static java.util.List Nodes(params RexNode[] nodes)
        {
            var list = new java.util.ArrayList();
            foreach (var node in nodes)
                list.add(node);
            return list;
        }

        RexNode JsonValue(string path, SqlTypeName? type = null) =>
            _rex.makeCall(Sql(type ?? SqlTypeName.VARCHAR), SqlStdOperatorTable.JSON_VALUE, Nodes(Doc, Str("$." + path)));

        RexNode Str(string value) => _rex.makeLiteral(value);

        RexNode Num(long value) => _rex.makeExactLiteral(new java.math.BigDecimal(value));

        RexNode Call(org.apache.calcite.sql.SqlOperator op, params RexNode[] operands) => _rex.makeCall(op, operands);

        PredicateStatement Load(RexNode condition) => CosmosPredicateLoader.Load(condition, _fields, _readings, "c");

        SqlPredicate One(RexNode condition) => Load(condition).Conjuncts.Should().ContainSingle().Subject;

        [Fact]
        public void AnEqualityOverTheAccessorComparesItsText()
        {
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("x"))).Should().Be(Eq(Text(Label), "x"));
        }

        [Fact]
        public void ALiteralOnTheLeftIsReadAsTheMirroredComparison()
        {
            One(Call(SqlStdOperatorTable.LESS_THAN, Str("m"), JsonValue("label"))).Should().Be(Cmp(Text(Label), ">", Lit("m")));
        }

        [Fact]
        public void AnInIsOneMembership()
        {
            One(_rex.makeIn(JsonValue("kind"), Nodes(Str("a"), Str("b")))).Should().Be(In(Text(Kind), "a", "b"));
        }

        [Fact]
        public void ANotInSaysEachValueIsNotHeld()
        {
            var statement = Load(Call(SqlStdOperatorTable.NOT, _rex.makeIn(JsonValue("kind"), Nodes(Str("a"), Str("b")))));
            var facts = Oracle(Theory()).Admit(statement, (_, _) => true).Facts(OracleLicence.Select);

            facts.Knows(new JsonFact(Kind, new JsonClaim.NotEqualTo("a"))).Should().BeTrue();
            facts.Knows(new JsonFact(Kind, new JsonClaim.NotEqualTo("b"))).Should().BeTrue();
            facts.Knows(new JsonFact(Kind, new JsonClaim.Present())).Should().BeTrue();
        }

        [Fact]
        public void IsNotNullIsTheNegationOfANullTest()
        {
            One(Call(SqlStdOperatorTable.IS_NOT_NULL, JsonValue("label"))).Should().Be(IsNotNull(Text(Label)));
            One(Call(SqlStdOperatorTable.IS_NULL, JsonValue("label"))).Should().Be(IsNull(Text(Label)));
        }

        [Fact]
        public void ANumericCastIsACastOfTheReadingItConverts()
        {
            One(Call(SqlStdOperatorTable.GREATER_THAN, _rex.makeCast(Sql(SqlTypeName.INTEGER), JsonValue("n")), Num(500)))
                .Should().Be(Cmp(Cast("INTEGER", Text(N)), ">", Lit(500m)));
        }

        [Fact]
        public void AReturningIsATypedReading()
        {
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.INTEGER), Num(30))).Should().Be(Eq(Typed(N, "INTEGER"), 30m));
        }

        [Fact]
        public void APromotedColumnIsTheRawValueAndItsLiteralTheBoxedOne()
        {
            var boxed = _rex.makeCast(_types.createTypeWithNullability(Sql(SqlTypeName.VARIANT), true), Str("bikes"));

            One(Call(SqlStdOperatorTable.EQUALS, Variant, boxed)).Should().Be(Eq(Raw(Category), "bikes"));
        }

        [Fact]
        public void TheAccessorRetypedAnyIsTheRawValue()
        {
            var raw = _rex.makeCall(Sql(SqlTypeName.ANY), SqlStdOperatorTable.JSON_VALUE, Nodes(Doc, Str("$.label")));

            One(Call(SqlStdOperatorTable.EQUALS, raw, Str("x"))).Should().Be(Eq(Raw(Label), "x"));
        }

        [Fact]
        public void AFieldReadAsTextIsTheRendering()
        {
            One(Call(SqlStdOperatorTable.EQUALS, TextField, Str("x"))).Should().Be(Eq(Text(Label), "x"));
        }

        [Fact]
        public void AParameterHasATypeAndNoValue()
        {
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), _rex.makeDynamicParam(Sql(SqlTypeName.VARCHAR), 0)))
                .Should().Be(Cmp(Text(Label), "=", Param(0, "VARCHAR")));
        }

        [Fact]
        public void WhatIsNotReadIsOpaqueAndNumberedByWhatItIs()
        {
            var like = Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"));
            var other = Call(SqlStdOperatorTable.LIKE, JsonValue("kind"), Str("b%"));
            var statement = Load(_rex.makeCall(SqlStdOperatorTable.AND, like, Call(SqlStdOperatorTable.OR, other, like)));

            statement.Conjuncts.Should().Equal(Opaque(0), Or(Opaque(1), Opaque(0)));
        }

        [Fact]
        public void ADisjunctionOverTwoPathsIsNoMembership()
        {
            One(Call(SqlStdOperatorTable.OR, Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("x")), Call(SqlStdOperatorTable.EQUALS, JsonValue("kind"), Str("y"))))
                .Should().Be(Or(Eq(Text(Label), "x"), Eq(Text(Kind), "y")));
        }

        [Fact]
        public void ALiteralOfAKindTheReadingIsNeverComparedWithIsOpaque()
        {
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.INTEGER), Str("30"))).Should().BeOfType<SqlPredicate.Opaque>();
        }

        [Fact]
        public void TheDigestDoesNotDependOnTheOrderOfTheConjuncts()
        {
            var a = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("x"));
            var b = Call(SqlStdOperatorTable.IS_NOT_NULL, JsonValue("kind"));

            Load(_rex.makeCall(SqlStdOperatorTable.AND, a, b)).Digest.Should().Be(Load(_rex.makeCall(SqlStdOperatorTable.AND, b, a)).Digest);
            Load(_rex.makeCall(SqlStdOperatorTable.AND, a, b)).Sources.Should().Equal(a, b);
        }

        const string StringLabel = """{ "properties": { "label": { "type": "string" } } }""";

        CosmosStatementScope Admit(RexNode condition, string? schema)
        {
            var container = schema is null
                ? new CosmosContainerMetadata("items", new[] { "/pk" })
                : new CosmosContainerMetadata("items", new[] { "/pk" }).WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(schema)));

            var statement = Load(condition);
            return CosmosOracle.For(container.Facts).Admit(statement, CosmosPredicateLoader.Renderer(statement, _rex, _fields, _readings, container));
        }

        /// <summary>
        /// #117 through today's translator. The equality with text a number renders as is not exact over an undeclared
        /// path, and the strata never hand the translator the facts it establishes, so nothing can make it so; a declared
        /// string makes it exact from the declaration, in the first stratum.
        /// </summary>
        [Fact]
        public void TheTranslatorRendersALabelEqualityFromTheDeclarationAndNeverFromItself()
        {
            var thirty = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("30"));

            var undeclared = Admit(thirty, null);
            undeclared.Residual.Should().Equal(0);
            undeclared.Facts(OracleLicence.RenderConjunct(0)).Knows(new JsonFact(Label, new JsonClaim.OfType(JsonType.String, OrNull: true))).Should().BeFalse();

            Admit(thirty, StringLabel).StratumOf(0).Should().Be(1);
        }

        [Fact]
        public void TheTranslatorAdmitsWhatRendersUnaidedInTheFirstStratum()
        {
            var scope = Admit(_rex.makeCall(SqlStdOperatorTable.AND,
                Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("bikes")),
                Call(SqlStdOperatorTable.LIKE, JsonValue("kind"), Str("%a%"))), null);

            scope.StratumOf(0).Should().Be(1);
            scope.Facts(OracleLicence.Select).Knows(new JsonFact(Label, new JsonClaim.EqualTo("bikes"))).Should().BeTrue();
        }

    }

}
