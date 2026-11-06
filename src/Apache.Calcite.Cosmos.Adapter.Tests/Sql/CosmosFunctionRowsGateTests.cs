using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

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
    /// The translator's column gate over a string function, the bridge reading the function rows as S's image rule reads
    /// them until G6 asks S's image (<see cref="CosmosFunctionSpellings.Disagreeing"/>, <see cref="CosmosFunctionSpellings.Refusals"/>):
    /// each band a target's rows part over is refused there, every kind and class of value an operand may hold is keyed,
    /// each operand the rows name among them, a search, a replacement and a window's length (F2), and #246's stored escapes
    /// are the emulator's rows as well as the legality the bridge keeps (<c>reports/emu-nul/spec.md</c>, §4.4, D8 to D13).
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

        /// <summary>
        /// A translator for a container of a target that declares <c>c.s</c> holds strings, where typed, of characters of
        /// these classes alone, of any for <c>null</c>, handed its declaration's facts as a statement's root is; and whose S
        /// is defined from these rows, the registry's for <c>null</c>.
        /// </summary>
        CosmosRexTranslator Translator(CosmosServiceTarget target, JsonCharacterBand? characters, CosmosParameterList? parameters = null, RenderingDefinition? rows = null, bool typed = true)
        {
            var declared = new CosmosContainerMetadata("strings").WithFacts(StringsOf(characters, typed));
            var container = (rows is null
                ? Declared.GetOrAdd((characters, typed), _ => TestReasoning.Bind(declared))
                : declared.WithReasoning(new CosmosReasoning(new JsonFactGenerations(CosmosReasoning.Stored), definition: rows))).WithServiceTarget(target);
            return new(_rex, _fields, parameters ?? new CosmosParameterList(), container.Facts.Declared, null, container);
        }

        /// <summary>Each declaration's container, bound to the shared reasoning once, so that each compiles once.</summary>
        static readonly ConcurrentDictionary<(JsonCharacterBand?, bool), CosmosContainerMetadata> Declared = new();

        /// <summary>That <c>c.s</c> holds strings, where typed, of characters of these classes alone, of any for <c>null</c>.</summary>
        static JsonFactRule[] StringsOf(JsonCharacterBand? characters, bool typed) =>
        [
            .. typed ? [JsonFactRule.Unconditional(new JsonFact(S, new JsonClaim.OfType(JsonType.String)))] : new JsonFactRule[0],
            .. characters is { } bands ? [JsonFactRule.Unconditional(new JsonFact(S, new JsonClaim.Characters(bands)))] : new JsonFactRule[0],
        ];

        static readonly JsonDocumentPath S = JsonDocumentPath.Root.Property("s");

        bool Pushes(CosmosServiceTarget target, JsonCharacterBand? characters, SqlOperator op, params RexNode[] operands) =>
            Translator(target, characters).TryTranslate(_rex.makeCall(op, operands), out _);

        RexNode Str() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 0);

        RexNode Lit(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, System.Math.Max(1, value.Length)));

        RexNode Unicode(string value) => _rex.makeLiteral(new org.apache.calcite.util.NlsString(value, "UTF-16LE", SqlCollation.IMPLICIT),
            _types.createTypeWithCharsetAndCollation(_types.createSqlType(SqlTypeName.VARCHAR, value.Length), java.nio.charset.Charset.forName("UTF-16LE"), SqlCollation.IMPLICIT), false);

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

        /// <summary>The calls each string key of <c>Service.StringFunctionAnswersByBand</c> was measured by, with <c>c.s</c> the operand it keys.</summary>
        RexNode[] Measured(string function, long subject) => (function, subject) switch
        {
            ("REPLACE", 1) => [Call(SqlStdOperatorTable.REPLACE, Lit("a0b1c"), Str(), Lit("x"))],
            ("REPLACE", 2) => [Call(SqlStdOperatorTable.REPLACE, Lit("a0b1c"), Lit("b"), Str())],
            (Apache.Calcite.Cosmos.Facts.Saturation.StringCalls.NullAsEmpty, _) => [Call(SqlLibraryOperators.CONCAT_FUNCTION_WITH_NULL, Str(), Lit("z")), Call(SqlLibraryOperators.CONCAT2, Str(), Lit("z"))],
            _ => Measured(function),
        };

        /// <summary>The call each string key of a function's subject was measured by, over <c>c.s</c>.</summary>
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
        /// Every band the emulator was measured to part from Calcite over (<c>Service.StringFunctionAnswersByBand</c>), at
        /// each operand the rows key, is refused there, read off the rows: <c>POSITION</c> over a surrogate among them, which
        /// the emulator's <c>INDEX_OF</c> counts as one code point where Calcite counts two units, pushed before h10, and a
        /// search that may be empty, which the service answers undefined.
        /// </summary>
        [Fact]
        public void EveryBandTheEmulatorPartsOverIsRefusedThere()
        {
            var parted = from o in CosmosAxioms.Get("Service.StringFunctionAnswersByBand").FunctionOutcomes!
                         where o.Operand == Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass.String && o.Answer != Apache.Calcite.Cosmos.Facts.Rendering.CosmosFunctionAnswer.Same
                         select (o.Function, o.Subject, o.Band);
            parted.Should().NotBeEmpty();
            foreach (var (function, subject, band) in parted)
                foreach (var call in Measured(function, subject))
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
        /// subject — <c>POSITION</c>'s second operand — to hold neither escape. On Azure both push whatever characters the
        /// string holds. <c>POSITION</c>'s pattern is keyed as the operand it is, measured on both targets as a literal of no
        /// band and not empty, so a path there, which may be empty or hold anything, stays in process.
        /// </summary>
        [Fact]
        public void AReversalAndAPositionAskTheirSubjectUnderTheEmulator()
        {
            foreach (var (op, operands) in new (SqlOperator, RexNode[])[] { (SqlLibraryOperators.REVERSE, [Str()]), (SqlStdOperatorTable.POSITION, [Lit("b"), Str()]) })
            {
                Pushes(Azure, null, op, operands).Should().BeTrue(op.getName() + " on Azure over any string");
                Pushes(Emulator, N | W, op, operands).Should().BeTrue(op.getName() + " under the emulator over a string holding neither");
                Pushes(Emulator, Z, op, operands).Should().BeFalse(op.getName() + " under the emulator over a string that may hold U+0000");
                Pushes(Emulator, H, op, operands).Should().BeFalse(op.getName() + " under the emulator over a string that may hold U+FDD0");
                Pushes(Emulator, null, op, operands).Should().BeFalse(op.getName() + " under the emulator over a string nothing is known of the characters of");
            }

            foreach (var target in new[] { Azure, Emulator })
                Pushes(target, JsonCharacterBand.None, SqlStdOperatorTable.POSITION, Str(), Lit("abc")).Should().BeFalse($"{target}: the pattern is a path, which may be empty, and no run sent one");
            Pushes(Azure, null, SqlStdOperatorTable.POSITION, Lit("\u00e9"), Str()).Should().BeFalse("no run sent a pattern beyond ASCII");
        }

        /// <summary>
        /// D12, D13: the emulator's <c>REPLACE</c> works on the stored units, a search beginning <c>1</c> meeting U+FDD0's
        /// second unit, and a replacement holding U+0000 fails the statement: so it asks all three operands to hold neither.
        /// Azure asks the subject to hold no U+0000, and the search and the replacement to be what run 1 measured them as, a
        /// string of no band and not empty, until run 2 measures the rest; an empty search is undefined at both.
        /// </summary>
        [Fact]
        public void AReplacementAsksEachOperandUnderTheEmulator()
        {
            Pushes(Azure, H | N | W, SqlStdOperatorTable.REPLACE, Str(), Lit("1"), Lit("b")).Should().BeTrue();
            Pushes(Emulator, H, SqlStdOperatorTable.REPLACE, Str(), Lit("1"), Lit("b")).Should().BeFalse("the subject may hold U+FDD0");
            Pushes(Emulator, N | W, SqlStdOperatorTable.REPLACE, Str(), Lit("1"), Lit("b")).Should().BeTrue("the subject holds neither");
            foreach (var target in new[] { Azure, Emulator })
            {
                Pushes(target, N, SqlStdOperatorTable.REPLACE, Str(), Lit(""), Lit("b")).Should().BeFalse($"{target}: an empty search is undefined at the service");
                Pushes(target, N, SqlStdOperatorTable.REPLACE, Lit("abc"), Str(), Lit("x")).Should().BeFalse($"{target}: a search that may be empty");
            }

            Pushes(Azure, Z, SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeFalse("Azure measured a replacement only as a string of no band and not empty");
            Pushes(Emulator, Z, SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeFalse("a replacement holding U+0000 fails the statement");
            Pushes(Emulator, H, SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeFalse("a replacement holding U+FDD0 mixes its spelling into the stored one");
            Pushes(Emulator, N, SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeTrue();
        }

        /// <summary>
        /// A window's length is keyed by its sign (F2): from 1 alike on both targets; 0 alike on both, Azure's measured as the
        /// window from 0 of length 0 (<c>Service.SubstringOfANegativeStartIsEmpty</c>); below 0, which Calcite raises over,
        /// parting on the emulator and unmeasured on Azure.
        /// </summary>
        [Fact]
        public void AWindowsLengthIsKeyedByItsSign()
        {
            foreach (var target in new[] { Azure, Emulator })
            {
                Pushes(target, N, SqlStdOperatorTable.SUBSTRING, Str(), Int(2), Int(2)).Should().BeTrue($"{target}: a length from 1");
                Pushes(target, N, SqlStdOperatorTable.SUBSTRING, Str(), Int(2), Int(-1)).Should().BeFalse($"{target}: a length below 0");
            }

            Pushes(Emulator, N, SqlStdOperatorTable.SUBSTRING, Str(), Int(2), Int(0)).Should().BeTrue("the emulator answers a length of 0 as Calcite does");
            Pushes(Azure, N, SqlStdOperatorTable.SUBSTRING, Str(), Int(2), Int(0)).Should().BeTrue("Azure answers a length of 0 as Calcite does, measured as the window from 0");
        }

        /// <summary>
        /// D13: a concatenation or a repetition joins what the emulator stores with what it makes, so every operand is
        /// asked to hold neither escape, a path among them (owner, 2026-11-01: "Cut it"). Azure asks nothing of a stored
        /// operand. A literal is keyed apart (<c>FunctionKey.Literal</c>), as the runs sent it: on both targets a string of
        /// no band and not empty, and a count of 0 on Azure alone (<c>Service.ReplicateStopsAtTenThousandCodePoints</c>).
        /// </summary>
        [Fact]
        public void AConcatenationAsksEveryOperandUnderTheEmulator()
        {
            var made = Call(SqlStdOperatorTable.UPPER, Str());

            Pushes(Azure, null, SqlStdOperatorTable.CONCAT, Str(), Lit("!")).Should().BeTrue();
            Pushes(Azure, null, SqlStdOperatorTable.CONCAT, Str(), Lit("\u00e9")).Should().BeFalse("no run sent a literal beyond ASCII");
            Pushes(Azure, null, SqlStdOperatorTable.CONCAT, Str(), Lit("")).Should().BeFalse("no run sent an empty literal");
            Pushes(Emulator, null, SqlStdOperatorTable.CONCAT, Str(), Lit("!")).Should().BeFalse("a path is asked as any operand is");
            Pushes(Emulator, JsonCharacterBand.None, SqlLibraryOperators.CONCAT_FUNCTION, Str(), Str(), Lit("!")).Should().BeTrue();
            Pushes(Emulator, Z, SqlStdOperatorTable.CONCAT, Str(), made).Should().BeFalse("UPPER's value may hold U+0000, made at the service");
            Pushes(Emulator, JsonCharacterBand.None, SqlStdOperatorTable.CONCAT, Str(), made).Should().BeTrue("and holds neither where its argument holds neither");

            Pushes(Azure, Z, SqlLibraryOperators.REPEAT, made, Int(0)).Should().BeTrue("a count of 0, as Azure answered it");
            Pushes(Emulator, Z, SqlLibraryOperators.REPEAT, Str(), Int(0)).Should().BeFalse("REPEAT's subject joins CONCAT's rule");
            Pushes(Emulator, JsonCharacterBand.None, SqlLibraryOperators.REPEAT, Str(), Int(0)).Should().BeFalse("the emulator's runs sent a count of 2 alone");
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
        /// A string function the gate holds and no row names is unmeasured and refused whatever it holds, on every target:
        /// <c>LENGTH</c>, whose row is owed. Azure's verdict declared for it went with run 1 (h14b).
        /// </summary>
        [Fact]
        public void AFunctionNoRowNamesIsRefusedEverywhere()
        {
            CosmosFunctionSpellings.Disagreeing(CosmosRendering.Definition, RenderingSet.Emulator, "LENGTH", ValueSet<PathImport>.Empty).Should().NotBeNull();
            CosmosFunctionSpellings.Disagreeing(CosmosRendering.Definition, RenderingSet.Production, "LENGTH", ValueSet<PathImport>.Empty).Should().NotBeNull();
            CosmosFunctionSpellings.Named("LENGTH").Should().BeTrue("the gate holds it, so it is refused rather than passed unasked");
            var length = SqlLibraryOperators.LENGTH;
            Pushes(Emulator, JsonCharacterBand.None, length, Str()).Should().BeFalse("the emulator never measured LENGTH");
            Pushes(Azure, JsonCharacterBand.None, length, Str()).Should().BeFalse("nor did the probe account");
        }

        /// <summary>
        /// A case mapping asks ASCII under every set: its rows part over a surrogate, and LOWER's over the high band, where
        /// the witnesses held no letter the mapping changes while Java's changes U+10428, U+10400 and U+FF21 (F5). No
        /// emulator refusal is lost (h10).
        /// </summary>
        [Fact]
        public void ACaseMappingAsksAsciiWhereItsRowsAreUnwitnessed()
        {
            Pushes(Emulator, JsonCharacterBand.Surrogates, SqlStdOperatorTable.UPPER, Str()).Should().BeFalse();
            Pushes(Emulator, JsonCharacterBand.Surrogates, SqlStdOperatorTable.LOWER, Str()).Should().BeFalse();
            Pushes(Emulator, H, SqlStdOperatorTable.LOWER, Str()).Should().BeFalse();
            Pushes(Azure, Z, SqlStdOperatorTable.LOWER, Str()).Should().BeTrue();
        }

        /// <summary>
        /// The rows key a trim by its flag as the loader names it, and describe its subject, the loader's operand 1 past the
        /// flag, not its character.
        /// </summary>
        [Fact]
        public void ATrimIsKeyedByItsFlagAndAskedOfItsSubject()
        {
            var trim = (RexCall)Call(SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.LEADING), Lit(" "), Str());
            CosmosFunctionSpellings.KeyOf(trim).Should().Be("TRIM LEADING");
            CosmosRendering.Definition.FunctionSubjects.Where(f => f.Function == "TRIM LEADING").Select(f => f.Subject).Should().Equal(1L);
            Pushes(Emulator, W, SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.LEADING), Lit(" "), Str()).Should().BeFalse();
            Pushes(Emulator, N, SqlStdOperatorTable.TRIM, _rex.makeFlag(SqlTrimFunction.Flag.LEADING), Lit(" "), Str()).Should().BeTrue();
        }

        /// <summary>
        /// The bridge reads every row S's image rule reads (owed, latent item 9), over the definition the container's S is
        /// built from: a row of one class of value disagreeing beside a row of every value refuses; rows of each class alone
        /// that agree push, where no row of every value is; and a row of another operand, of the kind it holds, keys that
        /// operand by its kind, where the bridge once asked it as a string and refused the call S proves
        /// (<see cref="Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingValuedTests.ANumberRowOfAnotherOperandKeysItByItsKind"/>).
        /// </summary>
        [Fact]
        public void TheBridgeReadsEveryRowSsImageRuleReads()
        {
            var registry = CosmosRendering.Definition;
            static bool Lower(FunctionKey k) => k.Function == "LOWER" && k.Kind == CosmosValueClass.String && k.Band == JsonCharacterBand.None;
            var lower = Call(SqlStdOperatorTable.LOWER, Str());
            bool Over(RenderingDefinition rows, CosmosServiceTarget target, RexNode call) => Translator(target, JsonCharacterBand.None, rows: rows).TryTranslate(call, out _);

            Over(registry, Emulator, lower).Should().BeTrue("the registry's rows agree over ASCII");
            var differing = registry with
            {
                Functions = [.. registry.Functions, .. from f in registry.Functions where Lower(f.Key) select (f.Key with { Value = FunctionValueClass.Empty }, f.Target, CosmosFunctionAnswer.Differs)],
            };
            Over(differing, Emulator, lower).Should().BeFalse("the empty string's row disagrees beside the row of every string");
            Over(differing, Azure, lower).Should().BeFalse();

            var split = registry with
            {
                Functions =
                [
                    .. registry.Functions.Where(f => Lower(f.Key) == false),
                    .. from f in registry.Functions where Lower(f.Key) from c in new[] { FunctionValueClass.Empty, FunctionValueClass.NonEmpty } select (f.Key with { Value = c }, f.Target, f.Answer),
                ],
            };
            Over(split, Emulator, lower).Should().BeTrue("the two classes cover both atoms of a string, and agree");
            Over(split, Azure, lower).Should().BeTrue();

            var left = Call(SqlLibraryOperators.LEFT, Str(), Int(2));
            var counted = registry with
            {
                Functions = [.. registry.Functions, .. from target in new[] { RenderingTarget.Emulator, RenderingTarget.Azure } select (FunctionKey.Of("LEFT", 1, CosmosValueClass.Number, JsonCharacterBand.None), target, CosmosFunctionAnswer.Same)],
                FunctionSubjects = [.. registry.FunctionSubjects, ("LEFT", 1L)],
            };
            Over(counted, Emulator, left).Should().BeTrue("the count is keyed as the number it is, and its row agrees");
            Over(counted, Azure, left).Should().BeTrue();
        }

        /// <summary>
        /// Every kind an operand may hold is keyed, and every operand is read through a reader the rows were measured
        /// through, as S keys them (latent item 9): a case mapping over a path that may hold a number, which the service's
        /// LOWER leaves undefined, is refused where the bridge once asked its strings alone; and a window whose start is a
        /// position, read through <c>typed:INTEGER</c>, which no row of <c>SUBSTRING</c> was measured through.
        /// </summary>
        [Fact]
        public void EveryKindAndReaderIsKeyedAsSKeysThem()
        {
            Translator(Emulator, JsonCharacterBand.None, typed: false).TryTranslate(Call(SqlStdOperatorTable.LOWER, Str()), out _).Should().BeFalse("c.s may hold a number");
            Pushes(Emulator, JsonCharacterBand.None, SqlStdOperatorTable.LOWER, Str()).Should().BeTrue("c.s holds strings");

            Pushes(Emulator, JsonCharacterBand.None, SqlStdOperatorTable.SUBSTRING, Str(), Call(SqlStdOperatorTable.POSITION, Lit("x"), Str()), Int(2)).Should().BeFalse();
            Pushes(Emulator, JsonCharacterBand.None, SqlStdOperatorTable.SUBSTRING, Str(), Int(1), Int(2)).Should().BeTrue();
        }

        /// <summary>
        /// A call over a call is asked of the rows of each, whatever the loader makes a place of: a replacement whose
        /// literal holds a surrogate pair, which the loader spells no place of, and six reversals, past the four it nests
        /// places to, are gathered with their images as any nested call is, and push where those were measured alike; a
        /// replacement holding U+FDD0, which the emulator stores escaped, is refused there, and on Azure, which measured a
        /// replacement only as the literal <c>x</c>, a replacement of either band is.
        /// </summary>
        [Fact]
        public void ACallOverACallIsAskedWhateverTheLoaderMakesAPlaceOf()
        {
            RexNode Replace(string replacement) => Call(SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Unicode(replacement));
            var reversed = Str();
            for (var i = 0; i < 6; i++)
                reversed = Call(SqlLibraryOperators.REVERSE, reversed);

            foreach (var target in new[] { Azure, Emulator })
                Translator(target, JsonCharacterBand.None).TryTranslate(reversed, out _).Should().BeTrue($"{target}: six reversals");

            Translator(Emulator, JsonCharacterBand.None).TryTranslate(Call(SqlLibraryOperators.REVERSE, Replace("\U0001F600")), out _).Should().BeTrue("the reversal of a replacement by a pair");
            Translator(Emulator, JsonCharacterBand.None).TryTranslate(Call(SqlLibraryOperators.REVERSE, Replace("\uFDD0")), out _).Should().BeFalse("the emulator stores U+FDD0 escaped");
            Translator(Azure, JsonCharacterBand.None).TryTranslate(Call(SqlLibraryOperators.REVERSE, Replace("\U0001F600")), out _).Should().BeFalse("Azure measured no replacement by a pair");
            Translator(Azure, JsonCharacterBand.None).TryTranslate(Call(SqlLibraryOperators.REVERSE, Replace("\uFDD0")), out _).Should().BeFalse("nor one of the high band");
            Translator(Azure, JsonCharacterBand.None).TryTranslate(Call(SqlLibraryOperators.REVERSE, Replace("b")), out _).Should().BeTrue("a replacement of no band, as measured");
        }

        /// <summary>
        /// A function whose rows name no operand they were measured over has no key S's image rule reads, which joins the
        /// rows' subjects (<c>FunctionSubjects</c>): refused, as S gives its place no image
        /// (<see cref="Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingValuedTests.APlaceWhoseRowsNameNoSubjectHasNoImage"/>),
        /// where the bridge once found no key to ask and passed it.
        /// </summary>
        [Fact]
        public void AFunctionWhoseRowsNameNoSubjectIsRefused()
        {
            var registry = CosmosRendering.Definition;
            var bare = registry with { FunctionSubjects = [.. registry.FunctionSubjects.Where(f => f.Function != "LOWER")] };
            var lower = Call(SqlStdOperatorTable.LOWER, Str());

            Translator(Emulator, JsonCharacterBand.None, rows: registry).TryTranslate(lower, out _).Should().BeTrue();
            Translator(Emulator, JsonCharacterBand.None, rows: bare).TryTranslate(lower, out _).Should().BeFalse("no row of LOWER names its subject");
            Translator(Azure, JsonCharacterBand.None, rows: bare).TryTranslate(lower, out _).Should().BeFalse();
        }

    }

}
