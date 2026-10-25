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

        PredicateStatement Load(RexNode condition) =>
            new CosmosPredicateLoader.Loading(_fields, _readings, "c").Statement(CosmosFilterStatement.ConjunctsOf(condition));

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

        /// <summary>
        /// <c>RETURNING DECIMAL</c> addresses no path — <see cref="CosmosRexTranslator.IsParsingJsonValue"/> — and is read
        /// through the cast of the text it is, <see cref="CosmosRexTranslator.TryNumericCastOperand(RexNode, RexBuilder)"/>:
        /// the reading whose meaning is the parse (<c>typed:DECIMAL</c>), with the literal cast to its scale read as the
        /// window reads it, and one its whole digits do not fit read as nothing.
        /// </summary>
        [Fact]
        public void ADecimalReturningIsTheParseOfTheText()
        {
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.DECIMAL), Num(2))).Should().Be(Eq(Typed(N, "DECIMAL"), 2m));

            var truncated = _rex.makeCast(_types.createSqlType(SqlTypeName.DECIMAL, 19, 0), _rex.makeExactLiteral(new java.math.BigDecimal("2.5")));
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.DECIMAL), truncated)).Should().Be(Eq(Typed(N, "DECIMAL"), 2.5m),
                "the literal Calcite truncates is within one of what it was, as the window reads it");

            var overflowing = _rex.makeCast(_types.createSqlType(SqlTypeName.DECIMAL, 19, 18), Num(50));
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.DECIMAL), overflowing)).Should().BeOfType<SqlPredicate.Opaque>(
                "fifty has two whole digits and DECIMAL(19, 18) one: the cast raises, and there is no constant");
        }

        /// <summary>
        /// A number compared with a cast to a number is read through the casts the window reads through — to
        /// <c>DOUBLE</c> and to <c>DECIMAL</c> — and as the value the translator sends: past 2^53, the whole number it is.
        /// A cast that drops scale is read only against <c>RETURNING DECIMAL</c>, whose reading knows the literal moves.
        /// </summary>
        [Fact]
        public void ANumberComparedWithACastIsReadAsTheWindowReadsIt()
        {
            var widened = _rex.makeCast(Sql(SqlTypeName.DOUBLE), _rex.makeExactLiteral(new java.math.BigDecimal("30.5")));
            One(Call(SqlStdOperatorTable.LESS_THAN_OR_EQUAL, _rex.makeCast(Sql(SqlTypeName.DOUBLE), JsonValue("n")), widened))
                .Should().Be(Cmp(Cast("DOUBLE", Text(N)), "<=", Lit(30.5m)));

            var big = _rex.makeExactLiteral(new java.math.BigDecimal("1152921504606846977"));
            One(Call(SqlStdOperatorTable.EQUALS, _rex.makeCast(Sql(SqlTypeName.BIGINT), JsonValue("n")), big))
                .Should().Be(Cmp(Cast("BIGINT", Text(N)), "=", Lit(1152921504606846977m)));

            var truncated = _rex.makeCast(_types.createSqlType(SqlTypeName.DECIMAL, 19, 0), _rex.makeExactLiteral(new java.math.BigDecimal("2.5")));
            One(Call(SqlStdOperatorTable.EQUALS, _rex.makeCast(Sql(SqlTypeName.INTEGER), JsonValue("n")), truncated)).Should().BeOfType<SqlPredicate.Opaque>(
                "a cast compares the 2 Calcite truncated to, which is not 2.5");
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

        /// <summary>
        /// Two predicates that print alike and are not alike are two terms: the text accessor and <c>RETURNING DECIMAL</c>
        /// both print <c>JSON_VALUE($0, '$.a')</c>, and a comparison of two of either is a different predicate, the one of
        /// renderings and the other of the numbers they parse as.
        /// </summary>
        [Fact]
        public void WhatIsNotReadIsNumberedByItsTypeAsWellAsItsSpelling()
        {
            var texts = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), JsonValue("n"));
            var numbers = Call(SqlStdOperatorTable.EQUALS, JsonValue("label", SqlTypeName.DECIMAL), JsonValue("n", SqlTypeName.DECIMAL));
            texts.toString().Should().Be(numbers.toString(), "Calcite's digest prints no call's type");

            var statement = Load(_rex.makeCall(SqlStdOperatorTable.AND, numbers, Call(SqlStdOperatorTable.NOT, texts)));

            statement.Conjuncts.Should().Equal(Opaque(0), new SqlPredicate.Not(Opaque(1)));
            Oracle(Theory()).Admit(statement).IsContradictory(OracleLicence.Kept).Should().BeFalse(
                "a stored \" 2 \" and a stored 2 are one number and two texts");

            // And two nodes that are one predicate are one term, however they were built: a weakening spelled as a
            // conjunct beneath is that conjunct.
            var again = Load(_rex.makeCall(SqlStdOperatorTable.AND,
                Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%")), Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"))));
            again.Conjuncts.Should().Equal(Opaque(0), Opaque(0));
        }

        /// <summary>
        /// An opaque conjunct is read as a value beside its term (spec g04): its top call with every operand the loader
        /// reads, at every depth; a predicate compared with a boolean as that comparison, whose strictness is the rows';
        /// an operand that is no value left out; and a reading that is the predicate as itself. Neither the conjuncts nor
        /// the digest move.
        /// </summary>
        [Fact]
        public void AnOpaqueConjunctIsReadAsAValueBesideItsTerm()
        {
            var upper = Call(SqlStdOperatorTable.EQUALS, Call(SqlStdOperatorTable.UPPER, JsonValue("label")), Str("X"));
            var compared = Call(SqlStdOperatorTable.NOT_EQUALS, upper, _rex.makeLiteral(false));
            var unread = Call(SqlStdOperatorTable.EQUALS, Call(SqlStdOperatorTable.UPPER, JsonValue("label")), _rex.makeNullLiteral(Sql(SqlTypeName.VARCHAR)));
            var statement = Load(_rex.makeCall(SqlStdOperatorTable.AND, upper, compared, unread, JsonValue("flag", SqlTypeName.BOOLEAN)));

            statement.Conjuncts.Should().Equal(Opaque(0), Opaque(1), Opaque(2), Opaque(3));
            var read = new SqlValue.Call("=", [new SqlValue.Call("UPPER", [Text(Label)]), Lit("X")]);
            Enumerable.Range(0, 4).Select(i => statement.Opaques[i]).Should().Equal(read, new SqlValue.Call("<>", [read, Lit(false)]),
                new SqlValue.Call("=", [new SqlValue.Call("UPPER", [Text(Label)])]), Typed(P("flag"), "BOOLEAN"));
            statement.Digest.Should().Be(new PredicateStatement(statement.Conjuncts).Digest);
        }

        [Fact]
        public void ADisjunctionOverTwoPathsIsNoMembership()
        {
            One(Call(SqlStdOperatorTable.OR, Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("x")), Call(SqlStdOperatorTable.EQUALS, JsonValue("kind"), Str("y"))))
                .Should().Be(Or(Eq(Text(Label), "x"), Eq(Text(Kind), "y")));
        }

        /// <summary>
        /// A number literal is read as the value Calcite evaluates it as, exactly, or its conjunct is opaque: against a typed
        /// reading and boxed for a <c>VARIANT</c> alike. An approximate one is its double's own value, never the fifteen digits
        /// .NET's cast keeps, which read 1.0000000000000002 as 1 and so let S prove <c>c.category != 1</c> of a row Calcite drops.
        /// </summary>
        [Theory]
        [InlineData("DOUBLE", "1.0000000000000002", false)]
        [InlineData("DOUBLE", "9007199254740993", true)]
        [InlineData("DOUBLE", "1234567890123456789", true)]
        [InlineData("DOUBLE", "1.5", true)]
        [InlineData("DOUBLE", "0.1", false)]
        [InlineData("REAL", "0.1", true)]
        [InlineData("REAL", "2.5", true)]
        [InlineData("DECIMAL", "0.1", true)]
        public void ANumberLiteralIsReadAsCalcitesValueOrNotAtAll(string type, string written, bool read)
        {
            var value = new java.math.BigDecimal(written);
            var (literal, evaluated) = type switch
            {
                "DECIMAL" => (_rex.makeExactLiteral(value), value),
                "REAL" => (_rex.makeApproxLiteral(value, Sql(SqlTypeName.REAL)), new java.math.BigDecimal((double)value.floatValue())),
                _ => (_rex.makeApproxLiteral(value), new java.math.BigDecimal(value.doubleValue())),
            };

            var boxed = _rex.makeCast(_types.createTypeWithNullability(Sql(SqlTypeName.VARIANT), true), literal);
            foreach (var conjunct in new[] { One(Call(SqlStdOperatorTable.LESS_THAN, JsonValue("n", SqlTypeName.DOUBLE), literal)), One(Call(SqlStdOperatorTable.EQUALS, Variant, boxed)) })
                if (read)
                    JsonValues.NumberOf(((SqlValue.Literal)((SqlPredicate.Compare)conjunct).Right).Value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        .Should().Be(evaluated.stripTrailingZeros().toPlainString(), $"{conjunct} holds the {type} {written} Calcite evaluates");
                else
                    conjunct.Should().BeOfType<SqlPredicate.Opaque>($"no decimal holds the {type} {written} Calcite evaluates");
        }

        /// <summary>
        /// Read as 1, the literal below made the facts graph prune to nothing a statement Calcite keeps a stored
        /// 1.0000000000000002 for: the raw value equal to 1 and its text to <c>'1.0000000000000002'</c>.
        /// </summary>
        [Fact]
        public void AnApproximateLiteralPrunesNoPlanItsDoubleKeeps()
        {
            var sql = """SELECT c."id" FROM typed AS c WHERE c."$.category" = CAST(1.0000000000000002E0 AS VARIANT) AND JSON_VALUE(c."DOC", '$.category') = '1.0000000000000002'""";
            org.apache.calcite.plan.RelOptUtil.toString(Facts.Rendering.RenderingInputs.Differential("loader", [sql], Facts.Rendering.RenderingInputs.Containers).Single().Plan())
                .Should().NotContain("tuples=[[]]").And.Contain("CosmosFilter");
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

        /// <summary>
        /// A filter as the planner reads it, its conjuncts loaded as these tests load them and rendered by today's
        /// translator.
        /// </summary>
        CosmosFilterStatement Admit(RexNode condition, string? schema)
        {
            var container = schema is null
                ? new CosmosContainerMetadata("items", new[] { "/pk" })
                : new CosmosContainerMetadata("items", new[] { "/pk" }).WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(schema)));

            return CosmosFilterStatement.Of(condition, _fields, _readings, container, _rex);
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
            undeclared.IsAdmitted(0).Should().BeFalse();
            undeclared.FactsFor(0).Knows(new JsonFact(Label, new JsonClaim.OfType(JsonType.String, OrNull: true))).Should().BeFalse();

            Admit(thirty, StringLabel).StratumOf(0).Should().Be(1);
        }

        [Fact]
        public void TheTranslatorAdmitsWhatRendersUnaidedInTheFirstStratum()
        {
            var scope = Admit(_rex.makeCall(SqlStdOperatorTable.AND,
                Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("bikes")),
                Call(SqlStdOperatorTable.LIKE, JsonValue("kind"), Str("%a%"))), null);

            scope.StratumOf(0).Should().Be(1);
            scope.After(OracleLicence.Select).Knows(new JsonFact(Label, new JsonClaim.EqualTo("bikes"))).Should().BeTrue();
        }

    }

}
