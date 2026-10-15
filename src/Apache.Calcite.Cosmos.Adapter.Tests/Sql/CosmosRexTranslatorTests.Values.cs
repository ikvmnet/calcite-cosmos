using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// Where a value is read rather than a truth, and where what an operator reads is not what it appears to.
    /// </summary>
    public partial class CosmosRexTranslatorTests
    {

        static java.util.List Of(params RexNode[] nodes)
        {
            var list = new java.util.ArrayList();
            foreach (var node in nodes)
                list.add(node);
            return list;
        }

        RexNode Nullable(int index, SqlTypeName type) =>
            _rex.makeInputRef(_types.createTypeWithNullability(_types.createSqlType(type), true), index);

        RexNode Text(string path, RexNode document) => _rex.makeCall(
            _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true),
            SqlStdOperatorTable.JSON_VALUE,
            Of(document, Str(path)));

        RexNode Variant(RexNode node) => _rex.makeCall(
            _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARIANT), node.getType().isNullable()),
            SqlStdOperatorTable.CAST,
            Of(node));

        RexNode Cast(RexNode node, SqlTypeName type, int precision) => _rex.makeCall(
            _types.createTypeWithNullability(_types.createSqlType(type, precision), node.getType().isNullable()),
            SqlStdOperatorTable.CAST,
            Of(node));

        /// <remarks>
        /// <para>
        /// Every form a comparison is written in is false somewhere SQL's is unknown — the service's own equality over a
        /// JSON null, and the guard a <c>WHERE</c> puts on the inequality — and a <c>WHERE</c> cannot tell the two
        /// apart. A column and an enclosing comparison can. Measured against the emulator over products, whose
        /// category is null in one document and absent in another: <c>(category &lt;&gt; 'bikes') = FALSE</c> kept
        /// both, and <c>category = 'bikes'</c> projected was false for the null where SQL answers null.
        /// </para>
        /// <para>
        /// So a comparison read as a value is declined where Calcite types it nullable — projected, and as the operand
        /// of another comparison in a <c>WHERE</c>. Typed <c>NOT NULL</c> it is two-valued at both ends and is written
        /// as before, and a <c>CASE</c> condition selects an arm by its truth whatever the <c>CASE</c> is read as.
        /// </para>
        /// </remarks>
        [Fact]
        public void AComparisonReadAsAValueIsDeclinedWhereSqlCanAnswerUnknown()
        {
            var equality = Call(SqlStdOperatorTable.EQUALS, Nullable(0, SqlTypeName.VARCHAR), Str("bikes"));
            var inequality = Call(SqlStdOperatorTable.NOT_EQUALS, Nullable(0, SqlTypeName.VARCHAR), Str("bikes"));

            Translator().TryTranslateValue(equality, out _).Should().BeFalse();
            Translator().TryTranslateValue(Call(SqlStdOperatorTable.NOT, equality), out _).Should().BeFalse();
            Translator().TryTranslateProjection(inequality, out _, out _).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.EQUALS, inequality, _rex.makeLiteral(false))).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.EQUALS, equality, Call(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("3")))).Should().BeFalse();

            Translator().TranslateValue(Call(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("3")))
                .Should().Be("(c.name = @p0)", "typed NOT NULL, SQL cannot answer it unknown");

            Translator().TranslateValue(Call(SqlStdOperatorTable.CASE, inequality, Str("x"), Str("y")))
                .Should().Be("((IS_DEFINED(c.name) AND NOT IS_NULL(c.name) AND (c.name != @p0)) ? @p1 : @p2)", "a condition is read for its truth");

            Translate(inequality).Should().Be("(IS_DEFINED(c.name) AND NOT IS_NULL(c.name) AND (c.name != @p0))", "and a WHERE reads truth alone");
        }

        /// <remarks>
        /// A width is a conversion on top of the rendering: <c>VARCHAR(3)</c> truncates and <c>CHAR(8)</c> pads.
        /// Taken as an identity it was dropped from the equality, so <c>CAST(label AS VARCHAR(3)) = 'bik'</c> pushed
        /// as <c>c.label = 'bik'</c> and lost the stored <c>bikes</c>, and the partition-key extractor routed the same
        /// statement over the category to a partition named <c>bik</c>. Only the undecorated cast is the rendering.
        /// </remarks>
        [Fact]
        public void ACastToTextWithAWidthIsNotTheRendering()
        {
            var label = Text("$.label", Ref(2, SqlTypeName.VARCHAR));

            CosmosRexTranslator.TryTextCastValue(Cast(label, SqlTypeName.VARCHAR, 3)).Should().BeNull();
            CosmosRexTranslator.TryTextCastValue(Cast(label, SqlTypeName.CHAR, 8)).Should().BeNull();
            CosmosRexTranslator.TryTextCastValue(_rex.makeCall(_types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true), SqlStdOperatorTable.CAST, Of(label)))
                .Should().BeSameAs(label);

            CanTranslate(Translator(), Call(SqlStdOperatorTable.EQUALS, Cast(label, SqlTypeName.VARCHAR, 3), Str("bik"))).Should().BeFalse();
        }

        /// <remarks>
        /// <para>
        /// Calcite boxes the other side of a comparison with a <c>VARIANT</c>, and the box carries the value unchanged,
        /// so a rendering inside one is still a rendering. Asked of the box, the equality saw none: measured over the
        /// typed container, <c>label = CAST('30' AS VARIANT)</c> pushed as <c>c.label = '30'</c> and lost the stored
        /// number 30, which Calcite renders and keeps. Against unambiguous text it is exact, box or no box.
        /// </para>
        /// <para>
        /// The box hid the reading from <c>IS NULL</c> the same way, which tested the path's null set alone and lost
        /// the array and the object.
        /// </para>
        /// </remarks>
        [Fact]
        public void AVariantBoxIsSeenThroughToTheRendering()
        {
            var label = Text("$.label", Ref(2, SqlTypeName.VARCHAR));

            CanTranslate(Translator(), Call(SqlStdOperatorTable.EQUALS, Variant(label), Variant(Str("30")))).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.LESS_THAN, Variant(label), Variant(Str("x")))).Should().BeFalse();

            Translate(Call(SqlStdOperatorTable.EQUALS, Variant(label), Variant(Str("bikes"))))
                .Should().Be("(c.label = @p0)");

            Translate(Call(SqlStdOperatorTable.IS_NULL, Variant(label)))
                .Should().Be("(NOT IS_DEFINED(c.label) OR IS_NULL(c.label) OR NOT IS_PRIMITIVE(c.label))");
        }

        /// <remarks>
        /// The document an accessor addresses through has to be a document. A text accessor's value is the rendering,
        /// which Calcite parses as JSON before reading the path out of it; the service has the value. Measured over
        /// products, <c>JSON_VALUE(JSON_VALUE(DOC, '$.name'), '$') IS NOT NULL</c> resolved to <c>c.name</c> and kept
        /// every document, where Calcite cannot parse <c>Trail Blazer</c> and answers null. A <c>JSON_QUERY</c> is the
        /// fragment's text, which parses back to the fragment, and still addresses through.
        /// </remarks>
        [Fact]
        public void AnAccessorDoesNotAddressThroughARendering()
        {
            var nested = Text("$", Text("$.name", Ref(2, SqlTypeName.VARCHAR)));

            Translator().TryResolvePath(nested, out _).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.IS_NOT_NULL, nested)).Should().BeFalse();

            TranslatorOverARendering().TryResolvePath(Text("$.v", Ref(0, SqlTypeName.VARCHAR)), out _)
                .Should().BeFalse("and nor through a field a projection bound to one");

            var fragment = _rex.makeCall(
                _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true),
                SqlStdOperatorTable.JSON_QUERY,
                Of(Ref(2, SqlTypeName.VARCHAR), Str("$.metadata")));

            Translator().TryResolvePath(Text("$.sku", fragment), out var path).Should().BeTrue();
            path!.ToString().Should().Be("c.metadata.sku");
        }

        /// <remarks>
        /// Calcite computes a sum, difference, product or remainder it types a decimal fraction exactly, and the
        /// service in doubles. Measured over products, <c>price * 1.1 = 374</c> lost the document priced 340, whose
        /// product the service makes <c>374.00000000000006</c>. A scale of zero is an integer, exact in doubles, and an
        /// approximate result is the service's arithmetic outright.
        /// </remarks>
        [Fact]
        public void ArithmeticInADecimalFractionIsDeclined()
        {
            var tenth = _rex.makeExactLiteral(new java.math.BigDecimal("1.1"));

            CanTranslate(Translator(), Call(SqlStdOperatorTable.MULTIPLY, Ref(1, SqlTypeName.INTEGER), tenth)).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.PLUS, Ref(1, SqlTypeName.INTEGER), tenth)).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.MINUS, Ref(1, SqlTypeName.INTEGER), tenth)).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.MOD, Ref(1, SqlTypeName.INTEGER), tenth)).Should().BeFalse();

            Translate(Call(SqlStdOperatorTable.MULTIPLY, Ref(1, SqlTypeName.INTEGER), Num(2))).Should().Be("(c.price * @p0)");
            Translate(Call(SqlStdOperatorTable.PLUS, Ref(1, SqlTypeName.DOUBLE), _rex.makeApproxLiteral(new java.math.BigDecimal("1.1"))))
                .Should().Be("(c.price + @p0)");
        }

        /// <remarks>
        /// <para>
        /// A field a projection bound to <c>JSON_VALUE(… RETURNING INTEGER)</c> is read as an integer, and is null
        /// wherever the accessor is: for an object, an array, and every value that is not a number. Its reading said
        /// only that it was typed, so <c>IS NOT NULL</c> over it kept an object, and the guard under a <c>NOT</c> kept
        /// the object too. The field's type is the accessor's, and the test is the one the accessor gets.
        /// </para>
        /// <para>
        /// Reached only where a filter stays above the projection: a host that transposes it through writes the
        /// accessor itself, and that was always tested.
        /// </para>
        /// </remarks>
        [Fact]
        public void AFieldBoundToATypedAccessorIsTestedForItsType()
        {
            var price = Nullable(1, SqlTypeName.INTEGER);

            TranslatorOverARendering().Translate(Call(SqlStdOperatorTable.IS_NOT_NULL, price))
                .Should().Be("(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price))");

            TranslatorOverARendering().Translate(Call(SqlStdOperatorTable.NOT, Call(SqlStdOperatorTable.EQUALS, price, Num(5))))
                .Should().Be("(NOT ((c.price = @p0) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))");

            TranslatorOverARendering().Translate(Call(SqlStdOperatorTable.IS_NOT_NULL, Nullable(1, SqlTypeName.BOOLEAN)))
                .Should().Be("(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_BOOL(c.price))");

            Translate(Call(SqlStdOperatorTable.IS_NOT_NULL, price))
                .Should().Be("(IS_DEFINED(c.price) AND NOT IS_NULL(c.price))", "a field nothing says was bound to an accessor is the raw value");
        }

        RexNode Typed(string path, RexNode document, SqlTypeName type, int precision, int scale) => _rex.makeCall(
            _types.createTypeWithNullability(_types.createSqlType(type, precision, scale), true),
            SqlStdOperatorTable.JSON_VALUE,
            Of(document, Str(path)));

        /// <remarks>
        /// <para>
        /// <c>RETURNING DECIMAL</c> parses what the path holds, a stored <c>"2"</c> as 2, where every other numeric
        /// <c>RETURNING</c> asserts. Written as the path, <c>= 2</c> pushed as <c>c.price = 2</c> and lost the string,
        /// and <c>IS NOT NULL</c> and the guard under a <c>NOT</c> took <c>IS_NUMBER</c> as its null set and lost it
        /// too. So it addresses no path, in any clause, and is left to the engine.
        /// </para>
        /// <para>
        /// What a comparison through it still implies is the window a cast to a number puts a stored number in, and
        /// the value it converts is the accessor read as text — the operand a cast would have had, written out because
        /// the clause adds none.
        /// </para>
        /// </remarks>
        [Fact]
        public void ADecimalReturningAddressesNoPathAndIsReadAsACast()
        {
            var price = Typed("$.price", Ref(2, SqlTypeName.VARCHAR), SqlTypeName.DECIMAL, 19, 0);

            CosmosRexTranslator.IsParsingJsonValue(price).Should().BeTrue();
            Translator().TryResolvePath(price, out _).Should().BeFalse("a parse is not the value at the path");
            CanTranslate(Translator(), Call(SqlStdOperatorTable.EQUALS, price, Num(2))).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.NOT, Call(SqlStdOperatorTable.EQUALS, price, Num(2)))).Should().BeFalse();
            CanTranslate(Translator(), Call(SqlStdOperatorTable.IS_NOT_NULL, price)).Should().BeFalse();
            Translator().TryTranslateProjection(price, out _, out _).Should().BeFalse();

            CosmosRexTranslator.TryNumericCastOperand(price, _rex).Should().Match<RexNode>(
                rendering => CosmosRexTranslator.IsTextJsonValue(rendering), "the value it converts is the rendering");
            Translator().TryResolvePath(CosmosRexTranslator.TryNumericCastOperand(price, _rex)!, out var path).Should().BeTrue();
            path!.ToString().Should().Be("c.price");

            CosmosRexTranslator.IsParsingJsonValue(Typed("$.price", Ref(2, SqlTypeName.VARCHAR), SqlTypeName.DECIMAL, 10, 2)).Should().BeTrue("at any scale");

            // The control: a RETURNING that asserts is the value where Calcite answers one, and is written as the path.
            var asserted = _rex.makeCall(_types.createTypeWithNullability(_types.createSqlType(SqlTypeName.INTEGER), true), SqlStdOperatorTable.JSON_VALUE, Of(Ref(2, SqlTypeName.VARCHAR), Str("$.price")));

            CosmosRexTranslator.IsParsingJsonValue(asserted).Should().BeFalse();
            Translate(Call(SqlStdOperatorTable.EQUALS, asserted, Num(2))).Should().Be("(c.price = @p0)");
            CosmosRexTranslator.TryNumericCastOperand(asserted, _rex).Should().BeNull("an asserted type is no conversion");
        }

    }

}
