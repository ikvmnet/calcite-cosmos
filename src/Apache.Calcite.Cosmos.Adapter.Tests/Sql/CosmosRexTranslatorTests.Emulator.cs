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

            /// <summary>A translator over a container declaring strings of these classes at field 0's path, the facts S imports.</summary>
            CosmosRexTranslator Declared(CosmosServiceTarget target, JsonCharacterBand characters)
            {
                _t._parameters = new CosmosParameterList();
                var container = TestReasoning.Bind(new CosmosContainerMetadata("products")
                    .WithFacts(StringsOf(characters).Select(claim => JsonFactRule.Unconditional(new JsonFact(JsonDocumentPath.Root.Property("name"), claim))))).WithServiceTarget(target);
                return new CosmosRexTranslator(_t._rex, _t._fields, _t._parameters, container.Facts.Declared, null, container);
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
            /// A prefix, a case-insensitive match and a <c>POSITION</c> pattern are compared positions too, the last over a
            /// subject proved to hold neither escape (D10); <c>REPLACE</c>'s replacement and a generic <c>LIKE</c> pattern are
            /// not, so a value holding U+0000 there stays in process.
            /// </summary>
            [Fact]
            public void APatternIsBoundStoredAndAReplacementIsNot()
            {
                Translator(CosmosServiceTarget.Emulator).Translate(_t.Call(SqlStdOperatorTable.LIKE, Name, _t.Str("\0%"))).Should().Be("STARTSWITH(c.name, @p0)");
                _t._parameters.Parameters[0].Value.Should().Be("﷐0");

                Declared(CosmosServiceTarget.Emulator, JsonCharacterBand.None).TranslateValue(_t.Call(SqlStdOperatorTable.POSITION, _t.Str("a\0"), Name)).Should().Be("(INDEX_OF(c.name, @p0) + 1)");
                _t._parameters.Parameters[0].Value.Should().Be("a﷐0");
                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(_t.Call(SqlStdOperatorTable.POSITION, _t.Str("a\0"), Name), out _).Should().BeFalse("the emulator counts a subject's stored units (D10)");

                Translator(CosmosServiceTarget.Emulator).TryTranslate(_t.Call(SqlStdOperatorTable.LIKE, Name, _t.Str("a_\0")), out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Azure).TryTranslate(_t.Call(SqlStdOperatorTable.LIKE, Name, _t.Str("a_\0")), out _).Should().BeTrue();
            }

            /// <summary>
            /// Anywhere but a compared position, a projected column or a <c>CASE</c> arm among them, a literal holding either
            /// character, and a parameter that may hold text, are computed in process (owner, 2026-11-01: "Cut it"). A number
            /// is not text, and a constant holding neither is sent as it is; under Azure nothing changes.
            /// </summary>
            [Fact]
            public void AConstantNotComparedIsRefusedWhereItMayHoldAnEscape()
            {
                var condition = _t.Call(SqlStdOperatorTable.GREATER_THAN, _t.Ref(1, SqlTypeName.INTEGER), _t.Num(1));
                var arm = _t.Call(SqlStdOperatorTable.CASE, condition, _t.Str("\0"), Name);
                var parameterArm = _t.Call(SqlStdOperatorTable.CASE, condition, Param, Name);

                Translator(CosmosServiceTarget.Emulator).TryTranslateProjection(arm, out _, out _).Should().BeFalse("a returned constant is not spelled");
                Translator(CosmosServiceTarget.Azure).TryTranslateProjection(arm, out _, out _).Should().BeTrue();
                Translator(CosmosServiceTarget.Emulator).TryTranslateProjection(parameterArm, out _, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Azure).TryTranslateProjection(parameterArm, out _, out _).Should().BeTrue();
                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(arm, out _).Should().BeFalse("a sort or group key, say");

                Translator(CosmosServiceTarget.Emulator).TryTranslateProjection(_t.Call(SqlStdOperatorTable.CASE, _t.Call(SqlStdOperatorTable.EQUALS, _t.Str("\0"), Name), _t.Str("y"), Name), out _, out _)
                    .Should().BeTrue("a condition's literal is compared");
                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(_t.Call(SqlStdOperatorTable.CASE, condition, _t.Str("x"), Name), out _)
                    .Should().BeTrue("a constant holding neither character is sent as it is");
                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(_t.Call(SqlStdOperatorTable.PLUS, _t.Ref(1, SqlTypeName.INTEGER), _t._rex.makeDynamicParam(_t._types.createSqlType(SqlTypeName.INTEGER), 0)), out _)
                    .Should().BeTrue("a parameter of a number is no text");
            }

            /// <summary>
            /// A parameter that is a list is not spelled when it is bound, so outside a compared position it is refused:
            /// <c>MEMBER OF ?</c> is <c>ARRAY_CONTAINS(@p, ...)</c>, whose elements the service compares with the stored path.
            /// A service-only function given a text parameter fails the statement there, having no body in process.
            /// </summary>
            [Fact]
            public void AListParameterAndAServiceOnlyFunctionsTextParameterAreRefused()
            {
                var list = _t._rex.makeDynamicParam(_t._types.createMultisetType(_t._types.createSqlType(SqlTypeName.VARCHAR), -1), 0);
                var member = _t.Call(SqlStdOperatorTable.MEMBER_OF, Name, list);
                var regex = _t.Call(CosmosOperators.RegexMatch, Name, Param);

                Translator(CosmosServiceTarget.Emulator).TryTranslate(member, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Azure).TryTranslate(member, out _).Should().BeTrue();
                Translator(CosmosServiceTarget.Emulator).TryTranslate(regex, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Azure).TryTranslate(regex, out _).Should().BeTrue();
            }

            /// <summary>
            /// The emulator stores a member's name escaped too, so a path naming one that holds U+0000 or U+FDD0, written as
            /// spelled, reads nothing: no path there, and an accessor naming one is computed in process over the document. A
            /// literal cannot hold U+FDD0, so it reaches a statement as a column's name, which binds no path there.
            /// </summary>
            [Fact]
            public void APathNamingAnEscapeIsNoPath()
            {
                var item = _t.Call(SqlStdOperatorTable.ITEM, _t.Ref(2, SqlTypeName.ANY), _t.Str("m\0"));

                Translator(CosmosServiceTarget.Emulator).Paths.TryResolvePath(item, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Emulator).TryTranslateValue(item, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Azure).TryTranslateValue(item, out _).Should().BeTrue();
                Translator(CosmosServiceTarget.Emulator).Paths.TryResolvePath(_t.Call(SqlStdOperatorTable.ITEM, _t.Ref(2, SqlTypeName.ANY), _t.Str("m")), out _).Should().BeTrue();

                var row = _t._types.builder().add("id", SqlTypeName.VARCHAR).add("m\uFDD0", SqlTypeName.VARCHAR).add("m\0", SqlTypeName.VARCHAR).build();
                CosmosImplementor.BindFields(row, "c", spelled: true).Select(p => p?.ToString()).Should().Equal("c.id", null, null);
                CosmosImplementor.BindFields(row, "c").Should().NotContainNulls();
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
            /// emulator refuses it, the parameter able to hold one (owner, 2026-11-01: "Cut it"). Two such paths
            /// compare as on Azure; a path that may hold U+0000 against another is refused, nothing recovering the rows the
            /// service drops, where an equality of the two is sent as on Azure, exact in the stored spelling.
            /// </summary>
            [Fact]
            public void AnOrderingOverTextThatHoldsNoNulIsRefusedAgainstAParameter()
            {
                var below = _t.Call(SqlStdOperatorTable.LESS_THAN, Name, Param);
                var paths = _t.Call(SqlStdOperatorTable.LESS_THAN, Name, Name);

                Translator(CosmosServiceTarget.Azure, Strings(JsonCharacterBand.None)).TranslateConjunct(below).Should().Be("(c.name < @p0)");
                _t._parameters.Comparisons.Should().BeEmpty("proved when the plan is made");

                Translator(CosmosServiceTarget.Emulator, Strings(JsonCharacterBand.None)).TryTranslateConjunct(below, out _).Should().BeFalse("nothing weaker is sent (owner, 2026-11-01: \"Cut it\")");

                Translator(CosmosServiceTarget.Emulator, Strings(JsonCharacterBand.None)).TranslateConjunct(paths).Should().Be("(c.name < c.name)");
                Translator(CosmosServiceTarget.Azure, Strings(JsonCharacterBand.Nul)).TranslateConjunct(paths).Should().Be("(c.name < c.name)");
                Translator(CosmosServiceTarget.Emulator, Strings(JsonCharacterBand.Nul)).TryTranslateConjunct(paths, out _).Should().BeFalse();
                Translator(CosmosServiceTarget.Emulator, Strings(JsonCharacterBand.Nul)).TranslateConjunct(Eq(Name, Name)).Should().Be("(c.name = c.name)");
            }

        }

    }

}
