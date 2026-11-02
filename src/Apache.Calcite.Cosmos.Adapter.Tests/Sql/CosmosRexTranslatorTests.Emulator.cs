using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using System.Linq;

using FluentAssertions;

using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    public partial class CosmosRexTranslatorTests
    {

        /// <summary>
        /// Under the emulator a value is sent as it is stored where the service compares it: the emulator stores U+0000 as
        /// U+FDD0 <c>0</c> and U+FDD0 as U+FDD0 <c>1</c>, and compares what it is sent, unescaped, with that
        /// (<c>Service.EmulatorComparesTheSpellingItIsSent</c>). Elsewhere a value holding either is computed in process, and
        /// under Azure nothing changes.
        /// </summary>
        public sealed class UnderTheEmulator
        {

            readonly CosmosRexTranslatorTests _t = new();

            static CosmosContainerMetadata Container(CosmosServiceTarget target) =>
                TestReasoning.Bind(new CosmosContainerMetadata("products")).WithServiceTarget(target);

            CosmosRexTranslator Translator(CosmosServiceTarget target)
            {
                _t._parameters = new CosmosParameterList();
                return new CosmosRexTranslator(_t._rex, _t._fields, _t._parameters, JsonFactSet.Empty, null, Container(target));
            }

            CosmosRexTranslator Translator(CosmosServiceTarget target, JsonFactSet facts)
            {
                _t._parameters = new CosmosParameterList();
                return new CosmosRexTranslator(_t._rex, _t._fields, _t._parameters, facts, null, Container(target));
            }

            RexNode Name => _t.Ref(0, SqlTypeName.VARCHAR);

            /// <summary>Strings at field 0's path, holding characters of these classes; any, U+0000 among them, for <c>null</c>.</summary>
            static JsonFactSet Strings(JsonCharacterBand? characters = null) => characters is { } bands
                ? Declaring(new JsonClaim.OfType(JsonType.String), new JsonClaim.Characters(bands))
                : Declaring(new JsonClaim.OfType(JsonType.String));

            RexNode Param => _t._rex.makeDynamicParam(_t._types.createSqlType(SqlTypeName.VARCHAR), 0);

            RexNode Eq(RexNode a, RexNode b) => _t.Call(SqlStdOperatorTable.EQUALS, a, b);

            /// <summary>A literal compared with a path is bound stored; under Azure as written.</summary>
            [Fact]
            public void ALiteralComparedWithAPathIsBoundInTheStoredSpelling()
            {
                Translator(CosmosServiceTarget.Emulator).Translate(Eq(Name, _t.Str("a\0"))).Should().Be("(c.name = @p0)");
                _t._parameters.Parameters[0].Value.Should().Be("a﷐0");

                Translator(CosmosServiceTarget.Azure).Translate(Eq(Name, _t.Str("a\0")));
                _t._parameters.Parameters[0].Value.Should().Be("a\0");

                Translator(CosmosServiceTarget.Emulator).Translate(Eq(Name, _t.Str("bikes")));
                _t._parameters.Parameters[0].Value.Should().Be("bikes", "a value holding neither is its own spelling");
            }

            /// <summary>A parameter compared with a path is named stored, for the binding to spell; under Azure it is not.</summary>
            [Fact]
            public void AParameterComparedWithAPathIsNamedStored()
            {
                Translator(CosmosServiceTarget.Emulator).Translate(Eq(Name, Param)).Should().Be("(NOT IS_NULL(@p1) AND (c.name = @p0))");
                _t._parameters.Stored.Should().Equal(["@p0", "@p1"], "the guard's null is the value's, in either spelling");

                Translator(CosmosServiceTarget.Azure).Translate(Eq(Name, Param));
                _t._parameters.Stored.Should().BeEmpty();
            }

            /// <summary>
            /// A prefix, a case-insensitive match and a <c>POSITION</c> pattern are compared positions too; <c>REPLACE</c>'s
            /// replacement and a generic <c>LIKE</c> pattern are not, so a value holding U+0000 there stays in process.
            /// </summary>
            [Fact]
            public void APatternIsBoundStoredAndAReplacementIsNot()
            {
                Translator(CosmosServiceTarget.Emulator).Translate(_t.Call(SqlStdOperatorTable.LIKE, Name, _t.Str("\0%"))).Should().Be("STARTSWITH(c.name, @p0)");
                _t._parameters.Parameters[0].Value.Should().Be("﷐0");

                Translator(CosmosServiceTarget.Emulator).TranslateValue(_t.Call(SqlStdOperatorTable.POSITION, _t.Str("a\0"), Name)).Should().Be("(INDEX_OF(c.name, @p0) + 1)");
                _t._parameters.Parameters[0].Value.Should().Be("a﷐0");

                Translator(CosmosServiceTarget.Emulator).TryTranslate(_t.Call(SqlStdOperatorTable.LIKE, Name, _t.Str("a_\0")), out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Azure).TryTranslate(_t.Call(SqlStdOperatorTable.LIKE, Name, _t.Str("a_\0")), out _).Should().BeTrue();
            }

            /// <summary>
            /// A constant the service returns, the column or a <c>CASE</c> arm, is a 500 sent raw where it holds U+0000, and
            /// comes back exactly as written sent stored (<c>Service.EmulatorDecodesWhatItReturns</c>): so a projected one is
            /// bound stored, and a parameter there named stored. Under Azure each is as written.
            /// </summary>
            [Fact]
            public void AConstantTheServiceReturnsIsBoundInTheStoredSpelling()
            {
                var condition = _t.Call(SqlStdOperatorTable.GREATER_THAN, _t.Ref(1, SqlTypeName.INTEGER), _t.Num(1));

                Translator(CosmosServiceTarget.Emulator).TranslateProjection(_t.Call(SqlStdOperatorTable.CASE, condition, _t.Str("a\0"), Name), out _)
                    .Should().Be("((c.price > @p0) ? @p1 : c.name)");
                _t._parameters.Parameters[1].Value.Should().Be("a﷐0");

                Translator(CosmosServiceTarget.Azure).TranslateProjection(_t.Call(SqlStdOperatorTable.CASE, condition, _t.Str("a\0"), Name), out _);
                _t._parameters.Parameters[1].Value.Should().Be("a\0");

                Translator(CosmosServiceTarget.Emulator).TranslateProjection(_t.Call(SqlStdOperatorTable.CASE, condition, Param, Name), out _);
                _t._parameters.Stored.Should().Equal(["@p1"]);

                Translator(CosmosServiceTarget.Azure).TranslateProjection(_t.Call(SqlStdOperatorTable.CASE, condition, Param, Name), out _);
                _t._parameters.Stored.Should().BeEmpty();
            }

            /// <summary>
            /// Anywhere else, a <c>CASE</c> in a filter, a condition, or an operand a function goes on to work on, nothing was
            /// measured in the stored spelling: a literal holding either character, and a parameter that may be text, are
            /// computed in process there. A number is not text, and a constant holding neither is sent as it is.
            /// </summary>
            [Fact]
            public void AConstantNeitherComparedNorReturnedIsRefusedWhereItMayHoldAnEscape()
            {
                var condition = _t.Call(SqlStdOperatorTable.GREATER_THAN, _t.Ref(1, SqlTypeName.INTEGER), _t.Num(1));
                var arm = _t.Call(SqlStdOperatorTable.CASE, condition, _t.Str("\0"), Name);
                var parameterArm = _t.Call(SqlStdOperatorTable.CASE, condition, Param, Name);

                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(arm, out _).Should().BeFalse("not returned: a sort or group key, say");
                Translator(CosmosServiceTarget.Azure).TryTranslateValue(arm, out _).Should().BeTrue();
                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(parameterArm, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Azure).TryTranslateValue(parameterArm, out _).Should().BeTrue();

                Translator(CosmosServiceTarget.Emulator).TryTranslateProjection(_t.Call(SqlStdOperatorTable.UPPER, arm), out _, out _)
                    .Should().BeFalse("an arm a function goes on to work on is not returned");
                Translator(CosmosServiceTarget.Emulator).TryTranslateProjection(_t.Call(SqlStdOperatorTable.CASE, _t.Call(SqlStdOperatorTable.EQUALS, _t.Str("\0"), Name), _t.Str("y"), Name), out _, out _)
                    .Should().BeTrue("a condition's literal is compared, not returned");
                Translator(CosmosServiceTarget.Emulator).TryTranslateProjection(_t.Call(SqlStdOperatorTable.CASE, condition, _t._rex.makeDynamicParam(_t._types.createSqlType(SqlTypeName.ANY), 0), Name), out _, out _)
                    .Should().BeFalse("a parameter that may be text but arrives as an object is not spelled when it is bound");

                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(_t.Call(SqlStdOperatorTable.CASE, condition, _t.Str("x"), Name), out _)
                    .Should().BeTrue("a constant holding neither character is sent as it is");
                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(_t.Call(SqlStdOperatorTable.PLUS, _t.Ref(1, SqlTypeName.INTEGER), _t._rex.makeDynamicParam(_t._types.createSqlType(SqlTypeName.INTEGER), 0)), out _)
                    .Should().BeTrue("a parameter of a number is no text");
            }

            /// <summary>
            /// An ordering against a parameter, over a path that may hold U+0000: refused under the emulator whatever the
            /// value, nothing weaker being sent for it (owner, 2026-11-01: "Cut it"), so the filter above stays in process.
            /// Under Azure it is anchored on the value, as before.
            /// </summary>
            [Fact]
            public void AnOrderingOverTextThatMayHoldANulIsRefusedAgainstAParameter()
            {
                var below = _t.Call(SqlStdOperatorTable.LESS_THAN, Name, Param);
                var above = _t.Call(SqlStdOperatorTable.GREATER_THAN, Name, Param);

                Translator(CosmosServiceTarget.Emulator, Strings()).TryTranslateConjunct(below, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Emulator, Strings()).TryTranslateConjunct(above, out _).Should().BeFalse();
                _t._parameters.Comparisons.Should().BeEmpty();

                Translator(CosmosServiceTarget.Azure, Strings()).TranslateConjunct(below).Should().Be("(c.name < @p0)");
                _t._parameters.Comparisons.Single().Should().Match<CosmosOrderComparison>(c => c.Anchor == CosmosOrderAnchor.Text && c.Weakened == "(c.name < @p1)");
                _t._parameters.Parameters.Should().HaveCount(2);
            }

            /// <summary>
            /// Over a path that holds no U+0000, ASCII here, Azure proves the ordering against a parameter outright; the
            /// emulator anchors it on a value holding no U+0000, whose bound stops there. Two such paths
            /// compare as on Azure; a path that may hold U+0000 against another is refused, nothing recovering the rows the
            /// service drops, where an equality of the two is sent as on Azure, exact in the stored spelling.
            /// </summary>
            [Fact]
            public void AnOrderingOverTextThatHoldsNoNulIsAnchoredOnAValueHoldingNone()
            {
                var below = _t.Call(SqlStdOperatorTable.LESS_THAN, Name, Param);
                var paths = _t.Call(SqlStdOperatorTable.LESS_THAN, Name, Name);

                Translator(CosmosServiceTarget.Azure, Strings(JsonCharacterBand.None)).TranslateConjunct(below).Should().Be("(c.name < @p0)");
                _t._parameters.Comparisons.Should().BeEmpty("proved when the plan is made");

                Translator(CosmosServiceTarget.Emulator, Strings(JsonCharacterBand.None)).TranslateConjunct(below).Should().Be("(c.name < @p0)");
                _t._parameters.Comparisons.Single().Should().Match<CosmosOrderComparison>(c => c.Anchor == CosmosOrderAnchor.TextWithoutNul && c.Weakened == "(c.name < @p1)");

                Translator(CosmosServiceTarget.Emulator, Strings(JsonCharacterBand.None)).TranslateConjunct(paths).Should().Be("(c.name < c.name)");
                Translator(CosmosServiceTarget.Azure, Strings(JsonCharacterBand.Nul)).TranslateConjunct(paths).Should().Be("(c.name < c.name)");
                Translator(CosmosServiceTarget.Emulator, Strings(JsonCharacterBand.Nul)).TryTranslateConjunct(paths, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Emulator, Strings(JsonCharacterBand.Nul)).TranslateConjunct(Eq(Name, Name)).Should().Be("(c.name = c.name)");
            }

        }

    }

}
