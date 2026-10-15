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

    }

}
