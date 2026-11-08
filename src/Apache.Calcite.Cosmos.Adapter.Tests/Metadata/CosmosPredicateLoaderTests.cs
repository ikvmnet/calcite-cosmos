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

        /// <summary>As <see cref="Load"/>, reading the computed places and pairs only S's tests read until h15.</summary>
        PredicateStatement LoadPlaces(RexNode condition) =>
            new CosmosPredicateLoader.Loading(_fields, _readings, "c", places: true).Statement(CosmosFilterStatement.ConjunctsOf(condition));

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

        /// <summary>
        /// A field's raw value names the column its declared type is where S reads places (rawtyped): a promoted path's
        /// <c>VARIANT</c>, and the string it is compared with the box Calcite casts it to (G8); the facts graph's reading names
        /// neither, its terms as they were until h15.
        /// </summary>
        [Fact]
        public void ARawFieldNamesItsColumnWhereSReads()
        {
            var equal = Call(SqlStdOperatorTable.EQUALS, Variant, _rex.makeAbstractCast(_types.createTypeWithNullability(Sql(SqlTypeName.VARIANT), false), Str("b")));
            LoadPlaces(equal).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(new SqlValue.Raw(Category) { Column = "VARIANT" }, "=", new SqlValue.Box("CHAR", Lit("b"))));
            One(equal).Should().Be(Eq(new SqlValue.Raw(Category), "b"));
        }

        /// <summary>
        /// A number Calcite wrote cast is read as the casts' terms where S reads (G8), whatever the reading it is compared with:
        /// what it denotes, and whether the reading takes it, are S's rules and rows, so the 2.5 a cast to <c>DECIMAL(19, 0)</c>
        /// truncates is read as written, and S's row for <c>RETURNING DOUBLE</c> takes no constant a cast moved (audit item 2).
        /// F's reading folds the casts as it did until h15, and <c>RETURNING DOUBLE</c> is none of its conversions.
        /// </summary>
        [Fact]
        public void ANumberCalciteWroteCastIsReadAsTheCastsWhereSReads()
        {
            var price = Call(SqlStdOperatorTable.GREATER_THAN, JsonValue("price", SqlTypeName.DOUBLE),
                _rex.makeAbstractCast(Sql(SqlTypeName.DOUBLE), _rex.makeExactLiteral(new java.math.BigDecimal("10.0"))));
            LoadPlaces(price).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(Typed(P("price"), "DOUBLE"), ">", Cast("DOUBLE", Lit(10m))));
            Load(price).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>("F's terms stay as they were until h15");

            var truncated = _rex.makeCast(Sql(SqlTypeName.DOUBLE), _rex.makeCast(_types.createSqlType(SqlTypeName.DECIMAL, 19, 0), _rex.makeExactLiteral(new java.math.BigDecimal("2.5"))));
            LoadPlaces(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.DOUBLE), truncated)).Conjuncts.Should().ContainSingle().Which.Should().Be(
                Cmp(Typed(N, "DOUBLE"), "=", Cast("DOUBLE", new SqlValue.DecimalCast(19, 0, Lit(2.5m)))), "the truncation is S's to read");
        }

        [Fact]
        public void ANullTestOfJsonQueryAsksWhetherThePathHoldsAStructureWhereSReads()
        {
            var query = _rex.makeCall(Sql(SqlTypeName.VARCHAR), SqlStdOperatorTable.JSON_QUERY, Nodes(Doc, Str("$.label")));
            var structure = SqlPredicate.AnyOf([new SqlPredicate.TypeTest("IS_OBJECT", new SqlValue.Raw(Label)), new SqlPredicate.TypeTest("IS_ARRAY", new SqlValue.Raw(Label))]);
            LoadPlaces(Call(SqlStdOperatorTable.IS_NOT_NULL, query)).Conjuncts.Should().ContainSingle().Which.Should().Be(structure);
            LoadPlaces(Call(SqlStdOperatorTable.IS_NULL, query)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.Not(structure));
            Load(Call(SqlStdOperatorTable.IS_NULL, query)).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>("F's terms stay as they were until h15");
        }

        /// <summary>
        /// A comparison with SQL's null is read as written where S reads (G8): what it answers is S's rule over the operator's
        /// row (<c>strictop</c>), not a refusal of the null literal. F's terms stay as they were until h15.
        /// </summary>
        [Fact]
        public void AComparisonWithANullIsReadAsWrittenWhereSReads()
        {
            var compared = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), _rex.makeNullLiteral(Sql(SqlTypeName.VARCHAR)));
            LoadPlaces(compared).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(Text(Label), "=", new SqlValue.Null("VARCHAR")));
            Load(compared).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>("F's terms stay as they were until h15");
        }

        /// <summary>
        /// A value cast to <c>VARIANT</c> is the box it is where S reads (G8), by the SQL type of what it holds, a reading, a
        /// literal or a parameter, compared and tested for null as written: what each comparison of it means is S's rules' over
        /// <c>Calcite.VariantEqualsOnlyItsOwnBox</c>' rows, which read no atom of its order. F's terms stay as they were until h15.
        /// </summary>
        [Fact]
        public void AValueCastToAVariantIsTheBoxWhereSReads()
        {
            var boxed = _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), JsonValue("label"));
            var (text, bikes) = (new SqlValue.Box("VARCHAR", Text(Label)), new SqlValue.Box("CHAR", Lit("bikes")));
            var equal = Call(SqlStdOperatorTable.EQUALS, boxed, _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), Str("bikes")));
            LoadPlaces(equal).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(text, "=", bikes));
            LoadPlaces(Call(SqlStdOperatorTable.IS_NULL, boxed)).Conjuncts.Should().ContainSingle().Which.Should().Be(IsNull(text));
            LoadPlaces(Call(SqlStdOperatorTable.LESS_THAN, boxed, _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), Str("bikes")))).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(Cmp(text, "<", bikes), "whether a VARIANT's order reads what it holds is the row's to say, and it says not");
            LoadPlaces(Call(SqlStdOperatorTable.EQUALS, Variant, _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), _rex.makeLiteral(true), false))).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(Cmp(new SqlValue.Raw(Category) { Column = "VARIANT" }, "=", new SqlValue.Box("BOOLEAN", Lit(true))));
            LoadPlaces(Call(SqlStdOperatorTable.EQUALS, Variant, _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), _rex.makeDynamicParam(Sql(SqlTypeName.VARCHAR), 0), false))).Conjuncts
                .Should().ContainSingle().Which.Should().Be(Cmp(new SqlValue.Raw(Category) { Column = "VARIANT" }, "=", new SqlValue.Box("VARCHAR", new SqlValue.Parameter(0, "VARCHAR"))),
                    "a box is a comparand whatever it holds, what it equals the box rules' to say");
            Load(equal).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>("F's terms stay as they were until h15");
        }

        [Fact]
        public void ATruthReadingAsAWholeConditionIsItsEqualityWithTrueWhereSReads()
        {
            var flag = JsonValue("flag", SqlTypeName.BOOLEAN);
            LoadPlaces(flag).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(Typed(P("flag"), "BOOLEAN"), "=", Lit(true)));
            LoadPlaces(Call(SqlStdOperatorTable.NOT, flag)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.Not(Cmp(Typed(P("flag"), "BOOLEAN"), "=", Lit(true))));
            Load(flag).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>("F's terms stay as they were until h15");
        }

        [Fact]
        public void TheTextCastToBooleanIsItsParseWhereSReads()
        {
            var flag = P("flag");
            var parsed = new SqlValue.Cast("BOOLEAN", Text(flag));
            var cast = _rex.makeAbstractCast(Sql(SqlTypeName.BOOLEAN), JsonValue("flag"));
            LoadPlaces(cast).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(parsed, "=", Lit(true)));
            LoadPlaces(Call(SqlStdOperatorTable.NOT, cast)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.Not(Cmp(parsed, "=", Lit(true))));
            LoadPlaces(Call(SqlStdOperatorTable.EQUALS, cast, _rex.makeLiteral(false))).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(parsed, "=", Lit(false)));
            LoadPlaces(Call(SqlStdOperatorTable.IS_NULL, cast)).Conjuncts.Should().ContainSingle().Which.Should().Be(IsNull(parsed));
            LoadPlaces(Call(SqlStdOperatorTable.IS_NOT_TRUE, cast)).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(new SqlPredicate.Case(Cmp(parsed, "=", Lit(true)), new SqlPredicate.Constant(false), new SqlPredicate.Constant(true)));

            // A text field cast is the same parse; a raw value's cast no row measures, and F's terms stay as they were until h15.
            LoadPlaces(_rex.makeAbstractCast(Sql(SqlTypeName.BOOLEAN), TextField)).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(new SqlValue.Cast("BOOLEAN", Text(Label)), "=", Lit(true)));
            LoadPlaces(_rex.makeAbstractCast(Sql(SqlTypeName.BOOLEAN), Variant)).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>();
            Load(cast).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>("F's terms stay as they were until h15");
            new CosmosPredicateLoader.Loading(_fields, _readings, "c", legacy: true, places: true).Statement([cast]).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>();
        }

        /// <summary>
        /// A schema's own <c>IS_DEFINED</c>, Calcite's wrapping of the declaration <see cref="CosmosSchemaFunctions"/> holds, is
        /// the operator's test where S reads, of the reading as it stands, which the body takes apart as the rows say (G8);
        /// a function merely named so, or a declaration of it this table does not hold, is
        /// no test, but a call S's rows key nothing of, so a place with no image (G6); the schema's is opaque in F's reading
        /// until h15.
        /// </summary>
        [Fact]
        public void ASchemasOwnIsDefinedIsTheTestWhereSReads()
        {
            org.apache.calcite.sql.SqlOperator Wrapped(org.apache.calcite.schema.Function function) =>
                new org.apache.calcite.sql.validate.SqlUserDefinedFunction(new org.apache.calcite.sql.SqlIdentifier("IS_DEFINED", org.apache.calcite.sql.parser.SqlParserPos.ZERO),
                    org.apache.calcite.sql.SqlKind.OTHER_FUNCTION, ReturnTypes.BOOLEAN, null, null, function);

            var declared = CosmosSchemaFunctions.Instance.get("IS_DEFINED");
            declared.size().Should().Be(1);
            var schema = Call(Wrapped((org.apache.calcite.schema.Function)declared.iterator().next()), JsonValue("label"));
            LoadPlaces(schema).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.IsDefined(Text(Label)));
            LoadPlaces(Call(SqlStdOperatorTable.NOT, schema)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.Not(new SqlPredicate.IsDefined(Text(Label))));
            Load(schema).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>("F's terms stay as they were until h15");

            var unheld = Call(Wrapped(new CosmosSchemaFunction(CosmosOperators.IsDefined, 1)), JsonValue("label"));
            LoadPlaces(unheld).Conjuncts.Should().ContainSingle().Which.Should().NotBeOfType<SqlPredicate.IsDefined>("a declaration this table does not hold is no schema's own");
            var named = new org.apache.calcite.sql.SqlFunction("IS_DEFINED", org.apache.calcite.sql.SqlKind.OTHER_FUNCTION, ReturnTypes.BOOLEAN, null, OperandTypes.ANY,
                org.apache.calcite.sql.SqlFunctionCategory.USER_DEFINED_FUNCTION);
            LoadPlaces(Call(named, JsonValue("label"))).Conjuncts.Should().ContainSingle().Which.Should().NotBeOfType<SqlPredicate.IsDefined>("a function merely named IS_DEFINED may mean anything");
            LoadPlaces(Call(CosmosOperators.IsString, JsonValue("label"))).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.TypeTest>();
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
        /// <c>RETURNING DECIMAL</c> addresses no path — <see cref="CosmosRexShapes.IsParsingJsonValue"/> — and is read
        /// through the cast of the text it is, <see cref="CosmosRexShapes.TryNumericCastOperand(RexNode, RexBuilder)"/>:
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
            var like = Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a[x]%"));
            var other = Call(SqlStdOperatorTable.LIKE, JsonValue("kind"), Str("b[y]%"));
            var statement = Load(_rex.makeCall(SqlStdOperatorTable.AND, like, Call(SqlStdOperatorTable.OR, other, like)));

            statement.Conjuncts.Should().Equal(Opaque(0), Or(Opaque(1), Opaque(0)));
        }

        /// <summary>
        /// Two predicates that print alike and are not alike are two terms: the text accessor and <c>RETURNING DECIMAL</c>
        /// both print <c>JSON_VALUE($0, '$.a')</c>, and a comparison of two of either is a different predicate, the one of
        /// renderings and the other of the numbers they parse as: since h11 each a comparison of two readings, of its own.
        /// </summary>
        [Fact]
        public void WhatIsNotReadIsNumberedByItsTypeAsWellAsItsSpelling()
        {
            var texts = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), JsonValue("n"));
            var numbers = Call(SqlStdOperatorTable.EQUALS, JsonValue("label", SqlTypeName.DECIMAL), JsonValue("n", SqlTypeName.DECIMAL));
            texts.toString().Should().Be(numbers.toString(), "Calcite's digest prints no call's type");

            var statement = Load(_rex.makeCall(SqlStdOperatorTable.AND, numbers, Call(SqlStdOperatorTable.NOT, texts)));

            statement.Conjuncts.Should().Equal(Opaque(0), new SqlPredicate.Not(Opaque(1)));
            var (label, n) = (JsonDocumentPath.Root.Property("label"), JsonDocumentPath.Root.Property("n"));
            LoadPlaces(_rex.makeCall(SqlStdOperatorTable.AND, numbers, Call(SqlStdOperatorTable.NOT, texts))).Conjuncts.Should().Equal(
                new SqlPredicate.Compare(SqlPredicate.Compare.Equal, new SqlValue.Typed(label, "DECIMAL"), new SqlValue.Typed(n, "DECIMAL")),
                new SqlPredicate.Not(new SqlPredicate.Compare(SqlPredicate.Compare.Equal, new SqlValue.Text(label), new SqlValue.Text(n))));
            Oracle(Theory()).Admit(statement).IsContradictory(OracleLicence.Kept).Should().BeFalse(
                "a stored \" 2 \" and a stored 2 are one number and two texts");

            // And two nodes that are one predicate are one term, however they were built: a weakening spelled as a
            // conjunct beneath is that conjunct.
            var again = Load(_rex.makeCall(SqlStdOperatorTable.AND,
                Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a[x]%")), Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a[x]%"))));
            again.Conjuncts.Should().Equal(Opaque(0), Opaque(0));
        }

        /// <summary>
        /// An opaque conjunct is read as a value beside its term (spec g04): its top call with every operand the loader
        /// reads, at every depth; a predicate compared with a boolean that is no literal as that comparison, whose
        /// strictness is the rows';
        /// an operand that is no value left out; and a reading that is the predicate as itself. Neither the conjuncts nor
        /// the digest move.
        /// </summary>
        [Fact]
        public void AnOpaqueConjunctIsReadAsAValueBesideItsTerm()
        {
            var upper = Call(SqlStdOperatorTable.EQUALS, Call(SqlStdOperatorTable.UPPER, JsonValue("label")), Str("X"));
            var compared = Call(SqlStdOperatorTable.NOT_EQUALS, upper, JsonValue("flag", SqlTypeName.BOOLEAN));
            var unread = Call(SqlStdOperatorTable.EQUALS, Call(SqlStdOperatorTable.UPPER, JsonValue("label")), _rex.makeNullLiteral(Sql(SqlTypeName.VARCHAR)));
            // The legacy reading: one that reads places reads UPPER(label) = 'X' as an atom over a computed place (h10).
            var statement = new CosmosPredicateLoader.Loading(_fields, _readings, "c", legacy: true)
                .Statement(CosmosFilterStatement.ConjunctsOf(_rex.makeCall(SqlStdOperatorTable.AND, upper, compared, unread, JsonValue("flag", SqlTypeName.BOOLEAN))));

            statement.Conjuncts.Should().Equal(Opaque(0), Opaque(1), Opaque(2), Opaque(3));
            LoadPlaces(upper).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Compare>();
            Load(upper).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>("F reads no computed place until h15");
            var read = new SqlValue.Call("=", [new SqlValue.Call("UPPER", [Text(Label)]), Lit("X")]);
            Enumerable.Range(0, 4).Select(i => statement.Opaques[i]).Should().Equal(read, new SqlValue.Call("<>", [read, Typed(P("flag"), "BOOLEAN")]),
                new SqlValue.Call("=", [new SqlValue.Call("UPPER", [Text(Label)])]), Typed(P("flag"), "BOOLEAN"));
            statement.Digest.Should().Be(new PredicateStatement(statement.Conjuncts).Digest);
        }

        /// <summary>
        /// The shapes the facts graph reads from h07: a boolean literal, the truth tests, <c>COALESCE(p, FALSE)</c>, the null
        /// test of a predicate and a boolean <c>CASE</c> as cases of what they define (<c>Calcite.TruthTestsAreCases</c>,
        /// <c>Calcite.CoalesceIsACase</c>), <c>LIKE</c> of text against a literal pattern, the type tests of a path alone,
        /// a reading of an element, and a value <c>CASE</c> with its conditions.
        /// </summary>
        [Fact]
        public void EachNewShapeIsRead()
        {
            var x = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("x"));
            var y = Call(SqlStdOperatorTable.EQUALS, JsonValue("kind"), Str("y"));
            var (px, py) = (Eq(Text(Label), "x"), Eq(Text(Kind), "y"));
            SqlPredicate T = new SqlPredicate.Constant(true), F = new SqlPredicate.Constant(false);

            One(_rex.makeLiteral(true)).Should().Be(T);
            One(Call(SqlStdOperatorTable.IS_TRUE, x)).Should().Be(new SqlPredicate.Case(px, T, F));
            One(Call(SqlStdOperatorTable.IS_NOT_TRUE, x)).Should().Be(new SqlPredicate.Case(px, F, T));
            One(Call(SqlStdOperatorTable.IS_FALSE, x)).Should().Be(new SqlPredicate.Case(Not(px), T, F));
            One(Call(SqlStdOperatorTable.IS_NOT_FALSE, x)).Should().Be(new SqlPredicate.Case(Not(px), F, T));
            One(Call(SqlStdOperatorTable.EQUALS, x, _rex.makeLiteral(true))).Should().Be(px);
            One(Call(SqlStdOperatorTable.NOT_EQUALS, x, _rex.makeLiteral(false))).Should().Be(px);
            One(Call(SqlStdOperatorTable.EQUALS, x, _rex.makeLiteral(false))).Should().Be(Not(px));
            One(Call(SqlStdOperatorTable.COALESCE, x, _rex.makeLiteral(false))).Should().Be(new SqlPredicate.Case(px, T, F));
            One(Call(SqlStdOperatorTable.IS_NULL, x)).Should().Be(new SqlPredicate.Case(px, F, new SqlPredicate.Case(Not(px), F, T)));
            One(Call(SqlStdOperatorTable.IS_NOT_NULL, x)).Should().Be(new SqlPredicate.Case(px, T, new SqlPredicate.Case(Not(px), T, F)));
            One(Call(SqlStdOperatorTable.CASE, x, y, _rex.makeLiteral(false))).Should().Be(new SqlPredicate.Case(px, py, F));

            One(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"))).Should().Be(new SqlPredicate.Affix(PredicateAtom.LikeTest, Text(Label), "a%"));
            One(Call(SqlStdOperatorTable.NOT_LIKE, JsonValue("label"), Str("a%"))).Should().Be(Not(new SqlPredicate.Affix(PredicateAtom.LikeTest, Text(Label), "a%")));
            One(Call(CosmosOperators.IsString, JsonValue("label"))).Should().Be(new SqlPredicate.TypeTest("IS_STRING", Raw(Label)));
            One(Call(CosmosOperators.IsArray, Variant)).Should().Be(new SqlPredicate.TypeTest("IS_ARRAY", Raw(Category)));
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("tags[0]"), Str("x"))).Should().Be(Eq(Text(P("tags").Element(0)), "x"));

            var chosen = Call(SqlStdOperatorTable.EQUALS, Call(SqlStdOperatorTable.CASE, x, JsonValue("label"), JsonValue("kind")), Str("z"));
            var statement = Load(chosen);
            statement.Conjuncts.Should().Equal(Opaque(0));
            statement.Opaques[0].Should().Be(new SqlValue.Call("=", [new SqlValue.Case([px], [Text(Label), Text(Kind)]), Lit("z")]));
        }

        /// <summary>
        /// <c>LIKE</c> is read in F's terms only as two operands over text against a literal holding no bracket: an
        /// <c>ESCAPE</c>, a computed pattern, a bracket, which the service reads as a class and Calcite as itself, and a
        /// subject that is no reading of a path stay opaque, and so does a type test over a call. Where S reads, a bracket is a
        /// unit of the pattern like any, which each side's rows read (G8), and a type test is of the reading as it stands.
        /// </summary>
        [Fact]
        public void EachRefusedLikeStaysOpaque()
        {
            LoadPlaces(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a[b]%"))).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(new SqlPredicate.Affix(PredicateAtom.LikeTest, Text(Label), "a[b]%"));
            LoadPlaces(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"), Str("!"))).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>();
            LoadPlaces(Call(CosmosOperators.IsString, JsonValue("label"))).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.TypeTest>().Which.Value.Should().Be(Text(Label));
            LoadPlaces(Call(CosmosOperators.IsString, Variant)).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.TypeTest>()
                .Which.Value.Should().Be(new SqlValue.Raw(Category) { Column = "VARIANT" });
            One(Call(CosmosOperators.IsString, JsonValue("label"))).Should().BeOfType<SqlPredicate.TypeTest>().Which.Value.Should().Be(Raw(Label), "F's terms stay as they were until h15");

            One(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"), Str("!"))).Should().Be(Opaque(0));
            One(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), JsonValue("kind"))).Should().Be(Opaque(0));
            One(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a[b]%"))).Should().Be(Opaque(0));
            One(Call(SqlStdOperatorTable.LIKE, Call(SqlStdOperatorTable.UPPER, JsonValue("label")), Str("A%"))).Should().Be(Opaque(0));
            One(Call(CosmosOperators.IsString, Call(SqlStdOperatorTable.UPPER, JsonValue("label")))).Should().Be(Opaque(0));
        }

        /// <summary>
        /// The recall cases P8 and P18 of spec §3.1, opaque before h07: the null test of a comparison and <c>NOT COALESCE</c>
        /// of one are read with nothing opaque in them.
        /// </summary>
        [Fact]
        public void RecallP8AndP18AreRead()
        {
            var compared = Call(SqlStdOperatorTable.NOT_EQUALS, JsonValue("category"), Str("bikes"));
            var p8 = One(Call(SqlStdOperatorTable.IS_NULL, compared));
            var p18 = One(Call(SqlStdOperatorTable.NOT, Call(SqlStdOperatorTable.COALESCE, compared, _rex.makeLiteral(false))));

            p8.Subterms.Should().NotContain(t => t is SqlPredicate.Opaque);
            p18.Subterms.Should().NotContain(t => t is SqlPredicate.Opaque);
            p18.Should().Be(Not(new SqlPredicate.Case(Cmp(Text(Category), "<>", Lit("bikes")), new SqlPredicate.Constant(true), new SqlPredicate.Constant(false))));
        }

        /// <summary>
        /// The view S reads until its switch (h15) reads none of it: each new shape opaque, numbered as before.
        /// </summary>
        [Fact]
        public void TheLegacyViewReadsNoNewShape()
        {
            var x = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), Str("x"));
            var legacy = new CosmosPredicateLoader.Loading(_fields, _readings, "c", legacy: true);
            var shapes = new[]
            {
                _rex.makeLiteral(true), Call(SqlStdOperatorTable.IS_TRUE, x), Call(SqlStdOperatorTable.EQUALS, x, _rex.makeLiteral(true)),
                Call(SqlStdOperatorTable.COALESCE, x, _rex.makeLiteral(false)), Call(SqlStdOperatorTable.IS_NULL, x),
                Call(SqlStdOperatorTable.CASE, x, x, _rex.makeLiteral(false)), Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%")),
                Call(CosmosOperators.IsString, JsonValue("label")), Call(SqlStdOperatorTable.EQUALS, JsonValue("tags[0]"), Str("x")),
            };

            legacy.Statement(shapes).Conjuncts.Should().Equal(Enumerable.Range(0, shapes.Length).Select(i => Opaque(i)));
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
        /// .NET's cast keeps, which read 1.0000000000000002 as 1 and so let S prove <c>c.category != 1</c> of a row Calcite drops;
        /// one past a decimal keeps its approximate spelling, as before.
        /// </summary>
        [Theory]
        [InlineData("DOUBLE", "1.0000000000000002", "opaque")]
        [InlineData("DOUBLE", "9007199254740993", "exact")]
        [InlineData("DOUBLE", "1234567890123456789", "exact")]
        [InlineData("DOUBLE", "1.5", "exact")]
        [InlineData("DOUBLE", "0.1", "opaque")]
        [InlineData("DOUBLE", "1E30", "approximate")]
        [InlineData("REAL", "0.1", "exact")]
        [InlineData("REAL", "2.5", "exact")]
        [InlineData("DECIMAL", "0.1", "exact")]
        public void ANumberLiteralIsReadAsCalcitesValueOrNotAtAll(string type, string written, string read)
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
                if (read == "opaque")
                    conjunct.Should().BeOfType<SqlPredicate.Opaque>($"no decimal holds the {type} {written} Calcite evaluates");
                else if (((SqlValue.Literal)((SqlPredicate.Compare)conjunct).Right).Value is var held && read == "exact")
                    JsonValues.NumberOf(held).ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(evaluated.stripTrailingZeros().toPlainString(), $"{conjunct} holds the {type} {written} Calcite evaluates");
                else
                    JsonValues.ApproximateOf(held).Should().Be(value.doubleValue(), $"{conjunct} holds the double no decimal holds, approximately");
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
                ? TestReasoning.Bind(new CosmosContainerMetadata("items", new[] { "/pk" }))
                : TestReasoning.Bind(new CosmosContainerMetadata("items", new[] { "/pk" })).WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(schema)));

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
