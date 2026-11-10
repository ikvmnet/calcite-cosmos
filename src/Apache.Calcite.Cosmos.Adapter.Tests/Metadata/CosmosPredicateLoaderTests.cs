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

        /// <summary>The text field's reading, written apart as a field (h15b).</summary>
        static SqlValue LabelField => new SqlValue.Text(Label) { Form = PredicateAtom.FieldForm };

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

        /// <summary>
        /// A field's raw value names the column its declared type is (rawtyped): a promoted path's <c>VARIANT</c>, and the
        /// string it is compared with the box Calcite casts it to (G8), one reading for both graphs since the switch (h15).
        /// </summary>
        [Fact]
        public void ARawFieldNamesItsColumn()
        {
            var equal = Call(SqlStdOperatorTable.EQUALS, Variant, _rex.makeAbstractCast(_types.createTypeWithNullability(Sql(SqlTypeName.VARIANT), false), Str("b")));
            Load(equal).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(new SqlValue.Raw(Category) { Column = "VARIANT" }, "=", new SqlValue.Box("CHAR", Lit("b"))));
        }

        /// <summary>
        /// A number Calcite wrote cast is read as the casts' terms (G8), whatever the reading it is compared with:
        /// what it denotes, and whether the reading takes it, are S's rules and rows, so the 2.5 a cast to <c>DECIMAL(19, 0)</c>
        /// truncates is read as written, and S's row for <c>RETURNING DOUBLE</c> takes no constant a cast moved (audit item 2).
        /// </summary>
        [Fact]
        public void ANumberCalciteWroteCastIsReadAsTheCasts()
        {
            var price = Call(SqlStdOperatorTable.GREATER_THAN, JsonValue("price", SqlTypeName.DOUBLE),
                _rex.makeAbstractCast(Sql(SqlTypeName.DOUBLE), _rex.makeExactLiteral(new java.math.BigDecimal("10.0"))));
            Load(price).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(Typed(P("price"), "DOUBLE"), ">", Cast("DOUBLE", Lit(10m))));

            var truncated = _rex.makeCast(Sql(SqlTypeName.DOUBLE), _rex.makeCast(_types.createSqlType(SqlTypeName.DECIMAL, 19, 0), _rex.makeExactLiteral(new java.math.BigDecimal("2.5"))));
            Load(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.DOUBLE), truncated)).Conjuncts.Should().ContainSingle().Which.Should().Be(
                Cmp(Typed(N, "DOUBLE"), "=", Cast("DOUBLE", new SqlValue.DecimalCast(19, 0, Lit(2.5m)))), "the truncation is S's to read");
        }

        /// <summary>
        /// A <c>JSON_QUERY</c> with no clause is its own reading of the path (G8), tested for null as written:
        /// what it answers of each class is its row's (<c>Calcite.JsonQueryAnswersStructureOnly</c>), the structure test its
        /// template's.
        /// </summary>
        [Fact]
        public void AJsonQueryIsItsOwnReading()
        {
            var query = _rex.makeCall(Sql(SqlTypeName.VARCHAR), SqlStdOperatorTable.JSON_QUERY, Nodes(Doc, Str("$.label")));
            Load(Call(SqlStdOperatorTable.IS_NOT_NULL, query)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.Not(new SqlPredicate.IsNull(new SqlValue.Query(Label))));
            Load(Call(SqlStdOperatorTable.IS_NULL, query)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.IsNull(new SqlValue.Query(Label)));
        }

        /// <summary>
        /// A comparison with SQL's null is read as written (G8): what it answers is the shared vocabulary's rule over the
        /// operator's row (<c>strictop</c>), not a refusal of the null literal.
        /// </summary>
        [Fact]
        public void AComparisonWithANullIsReadAsWritten()
        {
            var compared = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), _rex.makeNullLiteral(Sql(SqlTypeName.VARCHAR)));
            Load(compared).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(Text(Label), "=", new SqlValue.Null("VARCHAR")));
        }

        /// <summary>
        /// A value cast to <c>VARIANT</c> is the box it is (G8), by the SQL type of what it holds, a reading, a
        /// literal or a parameter, compared and tested for null as written: what each comparison of it means is S's rules' over
        /// <c>Calcite.VariantEqualsOnlyItsOwnBox</c>' rows, which read no atom of its order.
        /// </summary>
        [Fact]
        public void AValueCastToAVariantIsTheBox()
        {
            var boxed = _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), JsonValue("label"));
            var (text, bikes) = (new SqlValue.Box("VARCHAR", Text(Label)), new SqlValue.Box("CHAR", Lit("bikes")));
            var equal = Call(SqlStdOperatorTable.EQUALS, boxed, _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), Str("bikes")));
            Load(equal).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(text, "=", bikes));
            Load(Call(SqlStdOperatorTable.IS_NULL, boxed)).Conjuncts.Should().ContainSingle().Which.Should().Be(IsNull(text));
            Load(Call(SqlStdOperatorTable.LESS_THAN, boxed, _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), Str("bikes"), false))).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(Cmp(text, "<", bikes), "whether a VARIANT's order reads what it holds is the row's to say, and it says not");
            Load(Call(SqlStdOperatorTable.EQUALS, Variant, _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), _rex.makeLiteral(true), false))).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(Cmp(new SqlValue.Raw(Category) { Column = "VARIANT" }, "=", new SqlValue.Box("BOOLEAN", Lit(true))));
            Load(Call(SqlStdOperatorTable.EQUALS, Variant, _rex.makeAbstractCast(Sql(SqlTypeName.VARIANT), _rex.makeDynamicParam(Sql(SqlTypeName.VARCHAR), 0), false))).Conjuncts
                .Should().ContainSingle().Which.Should().Be(Cmp(new SqlValue.Raw(Category) { Column = "VARIANT" }, "=", new SqlValue.Box("VARCHAR", new SqlValue.Parameter(0, "VARCHAR"))),
                    "a box is a comparand whatever it holds, what it equals the box rules' to say");
        }

        [Fact]
        public void ATruthReadingAsAWholeConditionIsItsEqualityWithTrue()
        {
            var flag = JsonValue("flag", SqlTypeName.BOOLEAN);
            Load(flag).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(Typed(P("flag"), "BOOLEAN"), "=", Lit(true)));
            Load(Call(SqlStdOperatorTable.NOT, flag)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.Not(Cmp(Typed(P("flag"), "BOOLEAN"), "=", Lit(true))));
        }

        [Fact]
        public void TheTextCastToBooleanIsItsParse()
        {
            var flag = P("flag");
            var parsed = new SqlValue.Cast("BOOLEAN", Text(flag));
            var cast = _rex.makeAbstractCast(Sql(SqlTypeName.BOOLEAN), JsonValue("flag"));
            Load(cast).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(parsed, "=", Lit(true)));
            Load(Call(SqlStdOperatorTable.NOT, cast)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.Not(Cmp(parsed, "=", Lit(true))));
            Load(Call(SqlStdOperatorTable.EQUALS, cast, _rex.makeLiteral(false))).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(parsed, "=", Lit(false)));
            Load(Call(SqlStdOperatorTable.IS_NULL, cast)).Conjuncts.Should().ContainSingle().Which.Should().Be(IsNull(parsed));
            Load(Call(SqlStdOperatorTable.IS_NOT_TRUE, cast)).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(new SqlPredicate.Case(Cmp(parsed, "=", Lit(true)), new SqlPredicate.Constant(false), new SqlPredicate.Constant(true)));

            // A text field cast is the same parse; a raw value's cast is read as the conversion it is, which no row measures,
            // so the shared cast rule reads nothing of it (measuredcast, h15b).
            Load(_rex.makeAbstractCast(Sql(SqlTypeName.BOOLEAN), TextField, false)).Conjuncts.Should().ContainSingle().Which.Should().Be(Cmp(new SqlValue.Cast("BOOLEAN", LabelField), "=", Lit(true)));
            Load(_rex.makeAbstractCast(Sql(SqlTypeName.BOOLEAN), Variant, false)).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(Cmp(new SqlValue.Cast("BOOLEAN", new SqlValue.Raw(Category) { Column = "VARIANT" }), "=", Lit(true)));
        }

        /// <summary>
        /// A schema's own <c>IS_DEFINED</c>, Calcite's wrapping of the declaration <see cref="CosmosSchemaFunctions"/> holds, is
        /// the operator's test, of the reading as it stands, which the body takes apart as the rows say (G8); a function
        /// merely named so, or a declaration of it this table does not hold, is no test, but a call S's rows key nothing of,
        /// so a place with no image (G6).
        /// </summary>
        [Fact]
        public void ASchemasOwnIsDefinedIsTheTest()
        {
            org.apache.calcite.sql.SqlOperator Wrapped(org.apache.calcite.schema.Function function) =>
                new org.apache.calcite.sql.validate.SqlUserDefinedFunction(new org.apache.calcite.sql.SqlIdentifier("IS_DEFINED", org.apache.calcite.sql.parser.SqlParserPos.ZERO),
                    org.apache.calcite.sql.SqlKind.OTHER_FUNCTION, ReturnTypes.BOOLEAN, null, null, function);

            var declared = CosmosSchemaFunctions.Instance.get("IS_DEFINED");
            declared.size().Should().Be(1);
            var schema = Call(Wrapped((org.apache.calcite.schema.Function)declared.iterator().next()), JsonValue("label"));
            Load(schema).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.IsDefined(Text(Label)));
            Load(Call(SqlStdOperatorTable.NOT, schema)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.Not(new SqlPredicate.IsDefined(Text(Label))));

            var unheld = Call(Wrapped(new CosmosSchemaFunction(CosmosOperators.IsDefined, 1)), JsonValue("label"));
            Load(unheld).Conjuncts.Should().ContainSingle().Which.Should().NotBeOfType<SqlPredicate.IsDefined>("a declaration this table does not hold is no schema's own");
            var named = new org.apache.calcite.sql.SqlFunction("IS_DEFINED", org.apache.calcite.sql.SqlKind.OTHER_FUNCTION, ReturnTypes.BOOLEAN, null, OperandTypes.ANY,
                org.apache.calcite.sql.SqlFunctionCategory.USER_DEFINED_FUNCTION);
            Load(Call(named, JsonValue("label"))).Conjuncts.Should().ContainSingle().Which.Should().NotBeOfType<SqlPredicate.IsDefined>("a function merely named IS_DEFINED may mean anything");
            Load(Call(CosmosOperators.IsString, JsonValue("label"))).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.TypeTest>();
        }

        /// <summary>
        /// A one-place test over a cast of the text to <c>VARCHAR</c> is asked over the cast written apart as its form, whose
        /// <c>testreads</c> row says the body raises (review 3), the schema's own <c>IS_DEFINED</c> and a type test alike; a
        /// comparison over the same cast reads it as the text it renders.
        /// </summary>
        [Fact]
        public void ATestOverACastOfTheTextIsAskedOverTheCastWrittenApart()
        {
            var cast = _rex.makeAbstractCast(Sql(SqlTypeName.VARCHAR), JsonValue("label"), false);
            var apart = new SqlValue.Text(Label) { Form = PredicateAtom.TextCastForm };
            Load(Call(CosmosOperators.IsDefined, cast)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.IsDefined(apart));
            Load(Call(CosmosOperators.IsString, cast)).Conjuncts.Should().ContainSingle().Which.Should().Be(new SqlPredicate.TypeTest("IS_STRING", apart));
            Load(Call(SqlStdOperatorTable.EQUALS, cast, Str("x"))).Conjuncts.Should().ContainSingle().Which.Should().Be(Eq(Text(Label), "x"));
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
        /// <c>RETURNING DECIMAL</c> addresses no path to the translator — <see cref="CosmosRexShapes.IsParsingJsonValue"/> —
        /// and is read as the accessor's own reading of the path it parses the rendering of, <c>typed:DECIMAL</c>, with the
        /// literal cast to its scale read as the casts' terms (G8), what each denotes the shared vocabulary's: one its whole
        /// digits do not fit denotes nothing.
        /// </summary>
        [Fact]
        public void ADecimalReturningIsTheParseOfTheText()
        {
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.DECIMAL), Num(2))).Should().Be(Eq(Typed(N, "DECIMAL"), 2m));

            var truncated = _rex.makeCast(_types.createSqlType(SqlTypeName.DECIMAL, 19, 0), _rex.makeExactLiteral(new java.math.BigDecimal("2.5")));
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.DECIMAL), truncated)).Should().Be(Cmp(Typed(N, "DECIMAL"), "=", new SqlValue.DecimalCast(19, 0, Lit(2.5m))));

            var overflowing = _rex.makeCast(_types.createSqlType(SqlTypeName.DECIMAL, 19, 18), Num(50));
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("n", SqlTypeName.DECIMAL), overflowing)).Should().Be(Cmp(Typed(N, "DECIMAL"), "=", new SqlValue.DecimalCast(19, 18, Lit(50m))),
                "fifty has two whole digits and DECIMAL(19, 18) one: the cast raises, which the vocabulary reads as no constant");
        }

        /// <summary>
        /// A number compared with a cast to a number is read as the casts' terms (G8) — to <c>DOUBLE</c> and to
        /// <c>DECIMAL</c> — over the value the translator sends: past 2^53, the whole number it is. What a cast that drops
        /// scale denotes, and which readings take it, are the shared vocabulary's rules and rows (<c>denotes</c>,
        /// <c>takesconstant</c>), never the loader's.
        /// </summary>
        [Fact]
        public void ANumberComparedWithACastIsReadAsTheWindowReadsIt()
        {
            var widened = _rex.makeCast(Sql(SqlTypeName.DOUBLE), _rex.makeExactLiteral(new java.math.BigDecimal("30.5")));
            One(Call(SqlStdOperatorTable.LESS_THAN_OR_EQUAL, _rex.makeCast(Sql(SqlTypeName.DOUBLE), JsonValue("n")), widened))
                .Should().Be(Cmp(Cast("DOUBLE", Text(N)), "<=", Cast("DOUBLE", Lit(30.5m))));

            var big = _rex.makeExactLiteral(new java.math.BigDecimal("1152921504606846977"));
            One(Call(SqlStdOperatorTable.EQUALS, _rex.makeCast(Sql(SqlTypeName.BIGINT), JsonValue("n")), big))
                .Should().Be(Cmp(Cast("BIGINT", Text(N)), "=", Lit(1152921504606846977m)));

            var truncated = _rex.makeCast(_types.createSqlType(SqlTypeName.DECIMAL, 19, 0), _rex.makeExactLiteral(new java.math.BigDecimal("2.5")));
            One(Call(SqlStdOperatorTable.EQUALS, _rex.makeCast(Sql(SqlTypeName.INTEGER), JsonValue("n")), truncated)).Should().Be(
                Cmp(Cast("INTEGER", Text(N)), "=", new SqlValue.DecimalCast(19, 0, Lit(2.5m))), "what the truncation denotes is the vocabulary's to read");
        }

        [Fact]
        public void APromotedColumnIsTheRawValueAndItsLiteralTheBoxedOne()
        {
            var boxed = _rex.makeCast(_types.createTypeWithNullability(Sql(SqlTypeName.VARIANT), true), Str("bikes"));

            One(Call(SqlStdOperatorTable.EQUALS, Variant, boxed)).Should().Be(Cmp(new SqlValue.Raw(Category) { Column = "VARIANT" }, "=", new SqlValue.Box("CHAR", Lit("bikes"))));
        }

        [Fact]
        public void TheAccessorRetypedAnyIsTheRawValue()
        {
            var raw = _rex.makeCall(Sql(SqlTypeName.ANY), SqlStdOperatorTable.JSON_VALUE, Nodes(Doc, Str("$.label")));

            One(Call(SqlStdOperatorTable.EQUALS, raw, Str("x"))).Should().Be(Eq(Raw(Label), "x"));
        }

        /// <summary>
        /// <c>RETURNING VARIANT</c> is a <c>RETURNING</c> of its own type, not the raw value: it holds what it reads in no box
        /// (<c>Calcite.VariantEqualsOnlyItsOwnBox</c>), so beside a boxed literal it is no comparison the box rules read, an
        /// element's as a path's.
        /// </summary>
        [Fact]
        public void AReturningVariantIsATypedReadingAndNoRawValue()
        {
            var boxed = _rex.makeCast(_types.createTypeWithNullability(Sql(SqlTypeName.VARIANT), true), Str("bikes"));
            foreach (var (path, read) in new[] { ("$.label", Label), ("$.tags[0]", P("tags").Element(0)) })
            {
                var returned = _rex.makeCall(_types.createTypeWithNullability(Sql(SqlTypeName.VARIANT), true), SqlStdOperatorTable.JSON_VALUE, Nodes(Doc, Str(path)));
                One(Call(SqlStdOperatorTable.EQUALS, returned, boxed)).Should().Be(Cmp(Typed(read, "VARIANT"), "=", new SqlValue.Box("CHAR", Lit("bikes"))), path);
            }
        }

        [Fact]
        public void AFieldReadAsTextIsTheRendering()
        {
            One(Call(SqlStdOperatorTable.EQUALS, TextField, Str("x"))).Should().Be(Eq(LabelField, "x"));
        }

        /// <summary>
        /// A field read as text or as a scalar type, an item chain and a cast to <c>ANY</c> are each written apart as its
        /// form (h15b), which a test's body cannot take apart; the accessor re-typed <c>ANY</c> is the raw value itself, and a
        /// path holding an element is read as any other, the element reading being the shared vocabulary's.
        /// </summary>
        [Fact]
        public void EachReadingAFieldAnItemOrACastHoldsIsWrittenApart()
        {
            var ts = new CosmosPredicateLoader.Loading([CosmosPath.Root("c"), CosmosPath.Root("c").Property("_ts")], [CosmosReading.Typed, CosmosReading.Typed], "c");
            ts.Statement(CosmosFilterStatement.ConjunctsOf(Call(SqlStdOperatorTable.EQUALS, _rex.makeInputRef(Sql(SqlTypeName.BIGINT), 1), Num(3)))).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(Eq(new SqlValue.Typed(P("_ts"), "BIGINT") { Form = PredicateAtom.FieldForm }, 3L));

            var raw = _rex.makeCall(Sql(SqlTypeName.ANY), SqlStdOperatorTable.JSON_VALUE, Nodes(Doc, Str("$.label")));
            One(Call(CosmosOperators.IsString, _rex.makeAbstractCast(Sql(SqlTypeName.ANY), JsonValue("label"), false))).Should()
                .Be(new SqlPredicate.TypeTest("IS_STRING", new SqlValue.Raw(Label) { Form = PredicateAtom.AnyCastForm }));
            One(Call(CosmosOperators.IsString, raw)).Should().Be(new SqlPredicate.TypeTest("IS_STRING", Raw(Label)));

            var tags = _rex.makeCall(Sql(SqlTypeName.ANY), SqlStdOperatorTable.JSON_VALUE, Nodes(Doc, Str("$.tags")));
            var item = _rex.makeCall(SqlStdOperatorTable.ITEM, tags, _rex.makeExactLiteral(java.math.BigDecimal.ONE));
            One(Call(CosmosOperators.IsString, item)).Should().Be(new SqlPredicate.TypeTest("IS_STRING", new SqlValue.Raw(P("tags").Element(0)) { Form = PredicateAtom.ItemForm }),
                "ITEM counts from one");
            One(Call(SqlStdOperatorTable.EQUALS, _rex.makeCall(Sql(SqlTypeName.ANY), SqlStdOperatorTable.JSON_VALUE, Nodes(Doc, Str("$.tags[0]"))), Str("x")))
                .Should().Be(Eq(Raw(P("tags").Element(0)), "x"));
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
            var like = Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"), Str("!"));
            var other = Call(SqlStdOperatorTable.LIKE, JsonValue("kind"), Str("b%"), Str("!"));
            var statement = Load(_rex.makeCall(SqlStdOperatorTable.AND, like, Call(SqlStdOperatorTable.OR, other, like)));

            statement.Conjuncts.Should().Equal(Opaque(0), Or(Opaque(1), Opaque(0)));
        }

        /// <summary>
        /// Two predicates that print alike and are not alike are two terms: the text accessor and <c>RETURNING DECIMAL</c>
        /// both print <c>JSON_VALUE($0, '$.a')</c>, and a predicate of either is a different predicate, the one of renderings
        /// and the other of the numbers they parse as: since h11 a comparison of two of either a comparison of two readings,
        /// of its own, and an opaque one over either a term of its own.
        /// </summary>
        [Fact]
        public void WhatIsNotReadIsNumberedByItsTypeAsWellAsItsSpelling()
        {
            var texts = Call(SqlStdOperatorTable.EQUALS, JsonValue("label"), JsonValue("n"));
            var numbers = Call(SqlStdOperatorTable.EQUALS, JsonValue("label", SqlTypeName.DECIMAL), JsonValue("n", SqlTypeName.DECIMAL));
            texts.toString().Should().Be(numbers.toString(), "Calcite's digest prints no call's type");

            var (label, n) = (JsonDocumentPath.Root.Property("label"), JsonDocumentPath.Root.Property("n"));
            var pairs = Load(_rex.makeCall(SqlStdOperatorTable.AND, numbers, Call(SqlStdOperatorTable.NOT, texts)));
            pairs.Conjuncts.Should().Equal(
                new SqlPredicate.Compare(SqlPredicate.Compare.Equal, new SqlValue.Typed(label, "DECIMAL"), new SqlValue.Typed(n, "DECIMAL")),
                new SqlPredicate.Not(new SqlPredicate.Compare(SqlPredicate.Compare.Equal, new SqlValue.Text(label), new SqlValue.Text(n))));
            Oracle(Theory()).Admit(pairs).IsContradictory(OracleLicence.Kept).Should().BeFalse(
                "a stored \" 2 \" and a stored 2 are one number and two texts");

            RexNode Escaped(RexNode subject) => Call(SqlStdOperatorTable.LIKE, _rex.makeAbstractCast(Sql(SqlTypeName.VARCHAR), subject, false), Str("2%"), Str("!"));
            var (textLike, numberLike) = (Escaped(JsonValue("label")), Escaped(JsonValue("label", SqlTypeName.DECIMAL)));
            textLike.toString().Should().Be(numberLike.toString(), "Calcite's digest prints no call's type");
            var statement = Load(_rex.makeCall(SqlStdOperatorTable.AND, numberLike, Call(SqlStdOperatorTable.NOT, textLike)));
            statement.Conjuncts.Should().Equal(Opaque(0), new SqlPredicate.Not(Opaque(1)));
            Oracle(Theory()).Admit(statement).IsContradictory(OracleLicence.Kept).Should().BeFalse();

            // And two nodes that are one predicate are one term, however they were built: a weakening spelled as a
            // conjunct beneath is that conjunct.
            var again = Load(_rex.makeCall(SqlStdOperatorTable.AND,
                Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"), Str("!")), Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"), Str("!"))));
            again.Conjuncts.Should().Equal(Opaque(0), Opaque(0));
        }

        /// <summary>
        /// An opaque conjunct is read as a value beside its term (spec g04): its top call with every operand the loader
        /// reads, at every depth; a predicate compared with a boolean that is no literal as that comparison, whose
        /// strictness is the rows'; and an operand that is no value left out. Neither the conjuncts nor the digest move.
        /// Each is over <c>SOUNDEX</c>, which the service cannot spell: a call it spells is a computed place (h10), and
        /// compared, no opaque term.
        /// </summary>
        [Fact]
        public void AnOpaqueConjunctIsReadAsAValueBesideItsTerm()
        {
            var soundex = Call(SqlStdOperatorTable.EQUALS, Call(SqlLibraryOperators.SOUNDEX, JsonValue("label")), Str("X"));
            var compared = Call(SqlStdOperatorTable.NOT_EQUALS, soundex, JsonValue("flag", SqlTypeName.BOOLEAN));
            var unread = Call(SqlStdOperatorTable.EQUALS, Call(SqlLibraryOperators.SOUNDEX, JsonValue("label")), _rex.makeNullLiteral(Sql(SqlTypeName.VARCHAR)));
            var statement = Load(_rex.makeCall(SqlStdOperatorTable.AND, soundex, compared, unread));

            statement.Conjuncts.Should().Equal(Opaque(0), Opaque(1), Opaque(2));
            Load(Call(SqlStdOperatorTable.EQUALS, Call(SqlStdOperatorTable.UPPER, JsonValue("label")), Str("X"))).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Compare>();
            var read = new SqlValue.Call("=", [new SqlValue.Call("SOUNDEX", [Text(Label)]), Lit("X")]);
            Enumerable.Range(0, 3).Select(i => statement.Opaques[i]).Should().Equal(read, new SqlValue.Call("<>", [read, Typed(P("flag"), "BOOLEAN")]),
                new SqlValue.Call("=", [new SqlValue.Call("SOUNDEX", [Text(Label)])]));
            statement.Digest.Should().Be(new PredicateStatement(statement.Conjuncts).Digest);
        }

        /// <summary>
        /// The shapes the facts graph reads from h07: a boolean literal, the truth tests, <c>COALESCE(p, FALSE)</c>, the null
        /// test of a predicate and a boolean <c>CASE</c> as cases of what they define (<c>Calcite.TruthTestsAreCases</c>,
        /// <c>Calcite.CoalesceIsACase</c>), <c>LIKE</c> of text against a literal pattern, the type tests of the reading as it stands,
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
            One(Call(CosmosOperators.IsString, JsonValue("label"))).Should().Be(new SqlPredicate.TypeTest("IS_STRING", Text(Label)));
            One(Call(CosmosOperators.IsArray, Variant)).Should().Be(new SqlPredicate.TypeTest("IS_ARRAY", new SqlValue.Raw(Category) { Column = "VARIANT" }));
            One(Call(SqlStdOperatorTable.EQUALS, JsonValue("tags[0]"), Str("x"))).Should().Be(Eq(Text(P("tags").Element(0)), "x"));

            var chosen = Call(SqlStdOperatorTable.EQUALS, Call(SqlStdOperatorTable.CASE, x, JsonValue("label"), JsonValue("kind")), Str("z"));
            var statement = Load(chosen);
            statement.Conjuncts.Should().Equal(Opaque(0));
            statement.Opaques[0].Should().Be(new SqlValue.Call("=", [new SqlValue.Case([px], [Text(Label), Text(Kind)]), Lit("z")]));
        }

        /// <summary>
        /// <c>LIKE</c> is read as two operands over a place against a literal pattern: an <c>ESCAPE</c> and a computed
        /// pattern stay opaque. A bracket is a unit of the pattern like any, which each side's rows read (G8), a subject a
        /// call the service spells a computed place's (h10), and a type test is of the reading as it stands, over a call too.
        /// </summary>
        [Fact]
        public void EachRefusedLikeStaysOpaque()
        {
            Load(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a[b]%"))).Conjuncts.Should().ContainSingle()
                .Which.Should().Be(new SqlPredicate.Affix(PredicateAtom.LikeTest, Text(Label), "a[b]%"));
            Load(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"), Str("!"))).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.Opaque>();
            Load(Call(CosmosOperators.IsString, JsonValue("label"))).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.TypeTest>().Which.Value.Should().Be(Text(Label));
            Load(Call(CosmosOperators.IsString, Variant)).Conjuncts.Should().ContainSingle().Which.Should().BeOfType<SqlPredicate.TypeTest>()
                .Which.Value.Should().Be(new SqlValue.Raw(Category) { Column = "VARIANT" });

            One(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), Str("a%"), Str("!"))).Should().Be(Opaque(0));
            One(Call(SqlStdOperatorTable.LIKE, JsonValue("label"), JsonValue("kind"))).Should().Be(Opaque(0));
            One(Call(SqlStdOperatorTable.LIKE, Call(SqlStdOperatorTable.UPPER, JsonValue("label")), Str("A%"))).Should().BeOfType<SqlPredicate.Affix>()
                .Which.Value.Should().BeOfType<SqlValue.Call>().Which.Spelled.Should().Be("UPPER(" + SqlValue.Spell(Label) + ")");
            One(Call(CosmosOperators.IsString, Call(SqlStdOperatorTable.UPPER, JsonValue("label")))).Should().BeOfType<SqlPredicate.TypeTest>()
                .Which.Value.Should().BeOfType<SqlValue.Call>();
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
            {
                if (read == "opaque")
                {
                    conjunct.Should().BeOfType<SqlPredicate.Opaque>($"no decimal holds the {type} {written} Calcite evaluates");
                    continue;
                }

                // Boxed for a VARIANT, the literal is what its box holds (G8).
                var right = conjunct.Should().BeOfType<SqlPredicate.Compare>().Subject.Right;
                var held = (right is SqlValue.Box box ? box.Value : right).Should().BeOfType<SqlValue.Literal>().Subject.Value;
                if (read == "exact")
                    JsonValues.NumberOf(held).ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(evaluated.stripTrailingZeros().toPlainString(), $"{conjunct} holds the {type} {written} Calcite evaluates");
                else
                    JsonValues.ApproximateOf(held).Should().Be(value.doubleValue(), $"{conjunct} holds the double no decimal holds, approximately");
            }
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
