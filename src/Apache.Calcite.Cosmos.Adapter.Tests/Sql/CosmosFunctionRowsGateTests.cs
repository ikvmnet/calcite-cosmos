using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// The translator's column gate over a string function, the bridge reading the function rows S reads until h37
    /// (<see cref="CosmosFunctionSpellings.Parting"/>, <see cref="CosmosFunctionSpellings.Refusals"/>): under the emulator
    /// each band its rows part over is refused, and what the rows do not carry (#246's stored escapes, a case mapping
    /// outside ASCII) is legality; under Azure, which has no rows until run 1, each asks what it asked before
    /// (<c>reports/emu-nul/spec.md</c>, §4.4, D8 to D13).
    /// </summary>
    public class CosmosFunctionRowsGateTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath> _fields;

        public CosmosFunctionRowsGateTests()
        {
            _rex = new RexBuilder(_types);
            _fields =
            [
                CosmosPath.Root("c").Property("s"),   // 0
            ];
        }

        const CosmosServiceTarget Azure = CosmosServiceTarget.Azure;
        const CosmosServiceTarget Emulator = CosmosServiceTarget.Emulator;

        const JsonCharacterBand Z = JsonCharacterBand.Nul;
        const JsonCharacterBand H = JsonCharacterBand.HighBmp;
        const JsonCharacterBand N = JsonCharacterBand.NonAscii;
        const JsonCharacterBand W = JsonCharacterBand.Trimmed;

        /// <summary>A translator for a container of a target, whose facts say <c>c.s</c>'s strings hold characters of these classes alone; anything for <c>null</c>.</summary>
        CosmosRexTranslator Translator(CosmosServiceTarget target, JsonCharacterBand? characters, CosmosParameterList? parameters = null) =>
            new(_rex, _fields, parameters ?? new CosmosParameterList(), characters is { } bands
                ? new JsonFactTheory([JsonFactRule.Unconditional(new JsonFact(JsonDocumentPath.Root.Property("s"), new JsonClaim.Characters(bands)))]).Derive(null)
                : JsonFactSet.Empty, null, TestReasoning.Bind(new CosmosContainerMetadata("strings")).WithServiceTarget(target));

        bool Pushes(CosmosServiceTarget target, JsonCharacterBand? characters, SqlOperator op, params RexNode[] operands) =>
            Translator(target, characters).TryTranslate(_rex.makeCall(op, operands), out _);

        RexNode Str() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 0);

        RexNode Lit(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, System.Math.Max(1, value.Length)));

        RexNode Int(int value) => _rex.makeExactLiteral(new java.math.BigDecimal(value));

        RexNode Call(SqlOperator op, params RexNode[] operands) => _rex.makeCall(op, operands);

        IEnumerable<(SqlOperator, RexNode[])> Counts() =>
        [
            (SqlStdOperatorTable.CHAR_LENGTH, [Str()]),
            (SqlStdOperatorTable.CHARACTER_LENGTH, [Str()]),
            (SqlStdOperatorTable.SUBSTRING, [Str(), Int(2), Int(1)]),
            (SqlLibraryOperators.LEFT, [Str(), Int(2)]),
            (SqlLibraryOperators.RIGHT, [Str(), Int(2)]),
        ];

        /// <summary>The call each string key of <c>Service.StringFunctionAnswersByBand</c> was measured by, over <c>c.s</c>.</summary>
        RexNode[] Measured(string function) => function switch
        {
            "CHAR_LENGTH" => [Call(SqlStdOperatorTable.CHAR_LENGTH, Str())],
            "CHARACTER_LENGTH" => [Call(SqlStdOperatorTable.CHARACTER_LENGTH, Str())],
            "SUBSTRING" => [Call(SqlStdOperatorTable.SUBSTRING, Str(), Int(2), Int(2))],
            "LEFT" => [Call(SqlLibraryOperators.LEFT, Str(), Int(2))],
            "RIGHT" => [Call(SqlLibraryOperators.RIGHT, Str(), Int(2))],
            "UPPER" => [Call(SqlStdOperatorTable.UPPER, Str())],
            "LOWER" => [Call(SqlStdOperatorTable.LOWER, Str())],
            "REPLACE" => [Call(SqlStdOperatorTable.REPLACE, Str(), Lit("0"), Lit("x")), Call(SqlStdOperatorTable.REPLACE, Str(), Lit("1"), Lit("x"))],
            "POSITION" => [Call(SqlStdOperatorTable.POSITION, Lit("0"), Str()), Call(SqlStdOperatorTable.POSITION, Lit("1"), Str())],
            "REVERSE" => [Call(SqlLibraryOperators.REVERSE, Str())],
            "||" => [Call(SqlStdOperatorTable.CONCAT, Str(), Lit("z"))],
            "CONCAT" => [Call(SqlLibraryOperators.CONCAT_FUNCTION, Str(), Lit("z"))],
            "REPEAT" => [Call(SqlLibraryOperators.REPEAT, Str(), Int(2))],
            "TRIM BOTH" => [Call(SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.BOTH), Lit(" "), Str())],
            "TRIM LEADING" => [Call(SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.LEADING), Lit(" "), Str())],
            "TRIM TRAILING" => [Call(SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.TRAILING), Lit(" "), Str())],
            _ => throw new System.ArgumentException(function),
        };

        /// <summary>
        /// Every band the emulator was measured to part from Calcite over (<c>Service.StringFunctionAnswersByBand</c>) is
        /// refused there, read off the rows: <c>POSITION</c> over a surrogate among them, which the emulator's
        /// <c>INDEX_OF</c> counts as one code point where Calcite counts two units, pushed before h10.
        /// </summary>
        [Fact]
        public void EveryBandTheEmulatorPartsOverIsRefusedThere()
        {
            var parted = from o in CosmosAxioms.Get("Service.StringFunctionAnswersByBand").FunctionOutcomes!
                         where o.Operand == Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass.String && o.Answer != Apache.Calcite.Cosmos.Facts.Rendering.CosmosFunctionAnswer.Same
                         select (o.Function, o.Band);
            parted.Should().NotBeEmpty();
            foreach (var (function, band) in parted)
                foreach (var call in Measured(function))
                {
                    var pushed = Translator(Emulator, band).TryTranslate(call, out _);
                    pushed.Should().BeFalse($"{function} over {band} under the emulator");
                }
        }

        /// <summary>
        /// D8: the emulator counts and cuts the units it stores, two for U+0000 and two for U+FDD0, so a count asks its
        /// subject to hold neither as well as no surrogate; Azure's asks the surrogates alone, as before.
        /// </summary>
        [Fact]
        public void ACountAsksItsSubjectToHoldNeitherEscapeUnderTheEmulator()
        {
            foreach (var (op, operands) in Counts())
            {
                Pushes(Azure, Z | H | N | W, op, operands).Should().BeTrue(op.getName() + " on Azure over a string holding no surrogate");
                Pushes(Emulator, N | W, op, operands).Should().BeTrue(op.getName() + " under the emulator over a string holding neither");
                Pushes(Emulator, Z, op, operands).Should().BeFalse(op.getName() + " under the emulator over a string that may hold U+0000");
                Pushes(Emulator, H, op, operands).Should().BeFalse(op.getName() + " under the emulator over a string that may hold U+FDD0");
            }
        }

        /// <summary>
        /// D9 and D10: the emulator reverses the units it stores, and its <c>INDEX_OF</c> counts them, so both ask their
        /// subject — <c>POSITION</c>'s second operand, not its pattern, which is sent stored — to hold neither escape. On
        /// Azure both push whatever the string holds.
        /// </summary>
        [Fact]
        public void AReversalAndAPositionAskTheirSubjectUnderTheEmulator()
        {
            foreach (var (op, operands) in new (SqlOperator, RexNode[])[] { (SqlLibraryOperators.REVERSE, [Str()]), (SqlStdOperatorTable.POSITION, [Lit("b"), Str()]) })
            {
                Pushes(Azure, null, op, operands).Should().BeTrue(op.getName() + " on Azure over anything");
                Pushes(Emulator, N | W, op, operands).Should().BeTrue(op.getName() + " under the emulator over a string holding neither");
                Pushes(Emulator, Z, op, operands).Should().BeFalse(op.getName() + " under the emulator over a string that may hold U+0000");
                Pushes(Emulator, H, op, operands).Should().BeFalse(op.getName() + " under the emulator over a string that may hold U+FDD0");
                Pushes(Emulator, null, op, operands).Should().BeFalse(op.getName() + " under the emulator over a string nothing is known of");
            }

            Pushes(Emulator, null, SqlStdOperatorTable.POSITION, Str(), Lit("abc")).Should().BeTrue("the subject is the literal; the path is the pattern, matched stored");
        }

        /// <summary>
        /// D12, D13: the emulator's <c>REPLACE</c> works on the stored units, a search beginning <c>1</c> meeting U+FDD0's
        /// second unit, and a replacement holding U+0000 fails the statement: so it asks all three operands to hold neither.
        /// Azure asks the subject and the search to hold no U+0000, and the replacement nothing.
        /// </summary>
        [Fact]
        public void AReplacementAsksEachOperandUnderTheEmulator()
        {
            Pushes(Azure, H | N | W, SqlStdOperatorTable.REPLACE, Str(), Lit("1"), Lit("b")).Should().BeTrue();
            Pushes(Emulator, H, SqlStdOperatorTable.REPLACE, Str(), Lit("1"), Lit("b")).Should().BeFalse("the subject may hold U+FDD0");
            Pushes(Emulator, N | W, SqlStdOperatorTable.REPLACE, Str(), Lit("1"), Lit("b")).Should().BeTrue("the subject holds neither");

            Pushes(Azure, Z, SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeTrue("Azure writes the replacement as it is");
            Pushes(Emulator, Z, SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeFalse("a replacement holding U+0000 fails the statement");
            Pushes(Emulator, H, SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeFalse("a replacement holding U+FDD0 mixes its spelling into the stored one");
            Pushes(Emulator, N, SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeTrue();
        }

        /// <summary>
        /// D13: a concatenation or a repetition joins what the emulator stores with what it makes, so every operand is
        /// asked to hold neither escape, a path among them (owner, 2026-11-01: "Cut it"). Azure asks nothing.
        /// </summary>
        [Fact]
        public void AConcatenationAsksEveryOperandUnderTheEmulator()
        {
            var made = Call(SqlStdOperatorTable.UPPER, Str());

            Pushes(Azure, null, SqlStdOperatorTable.CONCAT, Str(), Lit("!")).Should().BeTrue();
            Pushes(Emulator, null, SqlStdOperatorTable.CONCAT, Str(), Lit("!")).Should().BeFalse("a path is asked as any operand is");
            Pushes(Emulator, JsonCharacterBand.None, SqlLibraryOperators.CONCAT_FUNCTION, Str(), Str(), Lit("!")).Should().BeTrue();
            Pushes(Emulator, Z, SqlStdOperatorTable.CONCAT, Str(), made).Should().BeFalse("UPPER's value may hold U+0000, made at the service");
            Pushes(Emulator, JsonCharacterBand.None, SqlStdOperatorTable.CONCAT, Str(), made).Should().BeTrue("and holds neither where its argument holds neither");

            Pushes(Azure, Z, SqlLibraryOperators.REPEAT, made, Int(0)).Should().BeTrue();
            Pushes(Emulator, Z, SqlLibraryOperators.REPEAT, Str(), Int(0)).Should().BeFalse("REPEAT's subject joins CONCAT's rule");
            Pushes(Emulator, JsonCharacterBand.None, SqlLibraryOperators.REPEAT, Str(), Int(0)).Should().BeTrue();
        }

        /// <summary>
        /// D11, D12: a pattern holding <c>_</c> matches one stored unit, and one holding a <c>0</c> or a <c>1</c> can meet an
        /// escape's second unit, so under the emulator such a <c>LIKE</c> asks its subject to hold neither escape: the
        /// generic <c>LIKE</c> and the fold's <c>CONTAINS</c> and <c>ENDSWITH</c> alike. A prefix starts where the subject
        /// does and is exact, and a pattern with none of them cannot start or end inside a pair. On Azure each is as before.
        /// </summary>
        [Fact]
        public void ALikeWhosePatternMeetsAnEscapeAsksItsSubjectUnderTheEmulator()
        {
            foreach (var pattern in new[] { "_", "a_b", "%0", "a1b" })
            {
                var like = Call(SqlStdOperatorTable.LIKE, Str(), Lit(pattern));
                Translator(Azure, Z | H).TryTranslate(like, out _).Should().BeTrue($"'{pattern}' on Azure");
                Translator(Emulator, Z).TryTranslate(like, out _).Should().BeFalse($"'{pattern}' under the emulator over a string that may hold U+0000");
                Translator(Emulator, H).TryTranslate(like, out _).Should().BeFalse($"'{pattern}' under the emulator over a string that may hold U+FDD0");
                Translator(Emulator, N | W).TryTranslate(like, out _).Should().BeTrue($"'{pattern}' under the emulator over a string holding neither");
            }

            foreach (var pattern in new[] { "a%", "0%", "a1%", "%b", "a%b", "%b%" })
                Translator(Emulator, H).TryTranslate(Call(SqlStdOperatorTable.LIKE, Str(), Lit(pattern)), out _).Should().BeTrue($"'{pattern}' meets no escape's unit");

            Translator(Emulator, Z).TryTranslate(Call(SqlStdOperatorTable.LIKE, Str(), Lit("0%")), out var prefix).Should().BeTrue("a prefix is exact");
            prefix.Should().Be("STARTSWITH(c.s, @p0)");
            Translator(Emulator, Z | H).TryTranslate(Call(SqlStdOperatorTable.LIKE, Str(), Lit("a\0%")), out _).Should().BeTrue("a prefix holding U+0000 is sent stored, and E is a prefix code");
            Translator(Emulator, H).TryTranslate(Call(SqlStdOperatorTable.LIKE, Str(), Lit("%a\0")), out _).Should().BeFalse("a pattern holding U+0000 elsewhere asks its subject");

            var fold = Call(SqlStdOperatorTable.LIKE, Call(SqlStdOperatorTable.UPPER, Str()), Lit("%0"));
            Translator(Azure, Z).TryTranslate(fold, out var azure).Should().BeTrue();
            azure.Should().Be("ENDSWITH(c.s, @p0, true)");
            Translator(Emulator, Z).TryTranslate(fold, out _).Should().BeFalse("ENDSWITH(c.s, '0') keeps U+0000, stored U+FDD0 0");
            Translator(Emulator, JsonCharacterBand.None).TryTranslate(fold, out var exact).Should().BeTrue();
            exact.Should().Be("ENDSWITH(c.s, @p0, true)");
            Translator(Emulator, Z).TryTranslate(Call(SqlStdOperatorTable.LIKE, Call(SqlStdOperatorTable.UPPER, Str()), Lit("%1%")), out _).Should().BeFalse("CONTAINS likewise");
        }

        /// <summary>The functions measured right over both escapes gain nothing: a case mapping over ASCII, U+0000 being ASCII, and a trim of spaces.</summary>
        [Fact]
        public void WhatWasMeasuredRightOverBothEscapesAsksNothingMore()
        {
            Pushes(Emulator, Z, SqlStdOperatorTable.UPPER, Str()).Should().BeTrue();
            Pushes(Emulator, Z, SqlStdOperatorTable.LOWER, Str()).Should().BeTrue();
            Pushes(Emulator, Z | H | N, SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.BOTH), Lit(" "), Str()).Should().BeTrue();
        }

        /// <summary>
        /// A function some target's rows name and the emulator's do not is unmeasured there and refused whatever it holds:
        /// <c>LENGTH</c>, whose emulator row is owed. Azure, with no rows until run 1, is held to its verdict (h10).
        /// </summary>
        [Fact]
        public void AFunctionTheEmulatorsRowsDoNotNameIsRefusedThere()
        {
            CosmosFunctionSpellings.Parting("LENGTH", Apache.Calcite.Cosmos.Facts.Rendering.RenderingTarget.Emulator).Should().BeNull();
            CosmosFunctionSpellings.Parting("LENGTH", Apache.Calcite.Cosmos.Facts.Rendering.RenderingTarget.Azure).Should().Be(JsonCharacterBand.Surrogates);
            var length = SqlLibraryOperators.LENGTH;
            Pushes(Emulator, JsonCharacterBand.None, length, Str()).Should().BeFalse("the emulator never measured LENGTH");
            Pushes(Azure, JsonCharacterBand.None, length, Str()).Should().BeTrue();
            Pushes(Azure, JsonCharacterBand.Surrogates, length, Str()).Should().BeFalse();
        }

        /// <summary>
        /// A case mapping asks ASCII under every set though the emulator's rows say it agrees over a surrogate, and LOWER over
        /// the high band: their witnesses hold no cased letter there, and Java maps U+10428 and U+FF21. No emulator refusal
        /// is lost (h10).
        /// </summary>
        [Fact]
        public void ACaseMappingAsksAsciiWhereItsRowsAreUnwitnessed()
        {
            Pushes(Emulator, JsonCharacterBand.Surrogates, SqlStdOperatorTable.UPPER, Str()).Should().BeFalse();
            Pushes(Emulator, JsonCharacterBand.Surrogates, SqlStdOperatorTable.LOWER, Str()).Should().BeFalse();
            Pushes(Emulator, H, SqlStdOperatorTable.LOWER, Str()).Should().BeFalse();
            Pushes(Azure, Z, SqlStdOperatorTable.LOWER, Str()).Should().BeTrue();
        }

        /// <summary>The rows key a trim by its flag as the loader names it, and describe its subject, not its character.</summary>
        [Fact]
        public void ATrimIsKeyedByItsFlagAndAskedOfItsSubject()
        {
            var trim = (RexCall)Call(SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.LEADING), Lit(" "), Str());
            CosmosFunctionSpellings.KeyOf(trim).Should().Be("TRIM LEADING");
            CosmosFunctionSpellings.SubjectsOf("TRIM LEADING", trim).Should().Equal(2);
            Pushes(Emulator, W, SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.LEADING), Lit(" "), Str()).Should().BeFalse();
        }

    }

}
