using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using org.apache.calcite.util;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    public partial class CosmosRexTranslatorTests
    {

        /// <summary>
        /// Scalar function translation. The mappings and the two argument adjustments were measured
        /// against the service; see <c>DESIGN.md</c>.
        /// </summary>
        /// <remarks>
        /// The mappings are written over a path declared ASCII, holding nothing the service trims beside a space, where
        /// every string function computes what Calcite's does; what each asks of its arguments where they hold more is
        /// held to the rule apart, below (<c>CosmosStringFunctions</c>).
        /// </remarks>
        public class Functions
        {

            readonly JavaTypeFactoryImpl _types = new();
            readonly RexBuilder _rex;
            readonly List<CosmosPath> _fields;

            public Functions()
            {
                _rex = new RexBuilder(_types);
                _fields = new List<CosmosPath>
                {
                    CosmosPath.Root("c").Property("s"),   // 0
                    CosmosPath.Root("c").Property("n"),   // 1
                    CosmosPath.Root("c").Property("a"),   // 2 — an array
                    CosmosPath.Root("c").Property("m"),   // 3 — a multiset
                    CosmosPath.Root("c").Property("o"),   // 4 — a map
                };
            }

            CosmosRexTranslator Translator() => Translator(JsonCharacterBand.None);

            /// <summary>A translator whose facts say <c>c.s</c>'s strings hold characters of these classes alone.</summary>
            CosmosRexTranslator Translator(JsonCharacterBand characters) => new(_rex, _fields, new CosmosParameterList(),
                new JsonFactTheory(new[] { JsonFactRule.Unconditional(new JsonFact(JsonDocumentPath.Root.Property("s"), new JsonClaim.Characters(characters))) }).Derive(null));

            /// <summary>A translator whose facts say nothing of what <c>c.s</c> holds.</summary>
            CosmosRexTranslator Undeclared() => new(_rex, _fields, new CosmosParameterList(), JsonFactSet.Empty);

            bool Pushes(CosmosRexTranslator translator, SqlOperator op, params RexNode[] operands) => translator.TryTranslate(_rex.makeCall(op, operands), out _);

            RexNode Param() => _rex.makeDynamicParam(_types.createSqlType(SqlTypeName.VARCHAR), 0);

            RexNode Unicode(string value) => _rex.makeLiteral(new NlsString(value, "UTF-16LE", SqlCollation.IMPLICIT), _types.createTypeWithCharsetAndCollation(_types.createSqlType(SqlTypeName.VARCHAR, value.Length), java.nio.charset.Charset.forName("UTF-16LE"), SqlCollation.IMPLICIT), false);

            const JsonCharacterBand S = JsonCharacterBand.Surrogates;
            const JsonCharacterBand H = JsonCharacterBand.HighBmp;
            const JsonCharacterBand N = JsonCharacterBand.NonAscii;
            const JsonCharacterBand W = JsonCharacterBand.Trimmed;

            RexNode Str() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 0);

            RexNode Num() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.DOUBLE), 1);

            RexNode Lit(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

            RexNode Int(int value) => _rex.makeExactLiteral(new java.math.BigDecimal(value));

            RexNode Array() => _rex.makeInputRef(_types.createArrayType(_types.createSqlType(SqlTypeName.VARCHAR), -1), 2);

            RexNode Multiset() => _rex.makeInputRef(_types.createMultisetType(_types.createSqlType(SqlTypeName.VARCHAR), -1), 3);

            RexNode Map() => _rex.makeInputRef(_types.createMapType(_types.createSqlType(SqlTypeName.VARCHAR), _types.createSqlType(SqlTypeName.ANY)), 4);

            RexNode Flag(SqlTrimFunction.Flag flag) => _rex.makeFlag(flag);

            string Translate(SqlOperator op, params RexNode[] operands) => Translator().Translate(_rex.makeCall(op, operands));

            bool CanTranslate(SqlOperator op, params RexNode[] operands) => Translator().TryTranslate(_rex.makeCall(op, operands), out _);

            // ── Direct mappings ───────────────────────────────────────────────────────

            [Fact]
            public void CaseFunctionsMapDirectly()
            {
                Translate(SqlStdOperatorTable.UPPER, Str()).Should().Be("UPPER(c.s)");
                Translate(SqlStdOperatorTable.LOWER, Str()).Should().Be("LOWER(c.s)");
            }

            [Fact]
            public void CharLengthMapsToLength()
            {
                Translate(SqlStdOperatorTable.CHAR_LENGTH, Str()).Should().Be("LENGTH(c.s)");
                Translate(SqlStdOperatorTable.CHARACTER_LENGTH, Str()).Should().Be("LENGTH(c.s)");
            }

            [Fact]
            public void ReplaceMapsDirectly()
            {
                Translate(SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("b")).Should().Be("REPLACE(c.s, @p0, @p1)");
            }

            [Fact]
            public void NumericFunctionsMapDirectly()
            {
                Translate(SqlStdOperatorTable.ABS, Num()).Should().Be("ABS(c.n)");
                Translate(SqlStdOperatorTable.EXP, Num()).Should().Be("EXP(c.n)");
                Translate(SqlStdOperatorTable.SQRT, Num()).Should().Be("SQRT(c.n)");
                Translate(SqlStdOperatorTable.SIGN, Num()).Should().Be("SIGN(c.n)");
                Translate(SqlStdOperatorTable.POWER, Num(), Int(2)).Should().Be("POWER(c.n, @p0)");
            }

            /// <remarks>
            /// Cosmos's natural logarithm is spelled LOG; its LOG10 is separate.
            /// </remarks>
            [Fact]
            public void NaturalLogMapsToLog()
            {
                Translate(SqlStdOperatorTable.LN, Num()).Should().Be("LOG(c.n)");
                Translate(SqlStdOperatorTable.LOG10, Num()).Should().Be("LOG10(c.n)");
            }

            [Fact]
            public void FloorMapsDirectly()
            {
                Translate(SqlStdOperatorTable.FLOOR, Num()).Should().Be("FLOOR(c.n)");
            }

            /// <remarks>
            /// Cosmos rejects CEIL; the function is named CEILING.
            /// </remarks>
            [Fact]
            public void CeilMapsToCeiling()
            {
                Translate(SqlStdOperatorTable.CEIL, Num()).Should().Be("CEILING(c.n)");
            }

            // ── Adjusted mappings ─────────────────────────────────────────────────────

            /// <remarks>
            /// SQL positions are one-based, Cosmos's are zero-based.
            /// </remarks>
            [Fact]
            public void SubstringShiftsTheStartPosition()
            {
                Translate(SqlStdOperatorTable.SUBSTRING, Str(), Int(1), Int(5))
                    .Should().Be("SUBSTRING(c.s, (@p0 - 1), @p1)");
            }

            /// <remarks>
            /// Cosmos requires a length, so taking the rest of the string cannot be expressed.
            /// </remarks>
            [Fact]
            public void SubstringWithoutALengthIsDeclined()
            {
                CanTranslate(SqlStdOperatorTable.SUBSTRING, Str(), Int(2)).Should().BeFalse();
            }

            /// <remarks>
            /// INDEX_OF is zero-based and yields -1 when absent, so adding one reproduces SQL exactly
            /// on both counts. Note the operands swap: SQL is POSITION(needle IN haystack).
            /// </remarks>
            [Fact]
            public void PositionBecomesIndexOfPlusOne()
            {
                Translate(SqlStdOperatorTable.POSITION, Lit("World"), Str())
                    .Should().Be("(INDEX_OF(c.s, @p0) + 1)");
            }

            // ── Trigonometry and the rest of the numeric set ──────────────────────────

            [Fact]
            public void TrigonometricFunctionsMapDirectly()
            {
                Translate(SqlStdOperatorTable.SIN, Num()).Should().Be("SIN(c.n)");
                Translate(SqlStdOperatorTable.COS, Num()).Should().Be("COS(c.n)");
                Translate(SqlStdOperatorTable.TAN, Num()).Should().Be("TAN(c.n)");
                Translate(SqlStdOperatorTable.COT, Num()).Should().Be("COT(c.n)");
                Translate(SqlStdOperatorTable.ASIN, Num()).Should().Be("ASIN(c.n)");
                Translate(SqlStdOperatorTable.ACOS, Num()).Should().Be("ACOS(c.n)");
                Translate(SqlStdOperatorTable.ATAN, Num()).Should().Be("ATAN(c.n)");
                Translate(SqlStdOperatorTable.DEGREES, Num()).Should().Be("DEGREES(c.n)");
                Translate(SqlStdOperatorTable.RADIANS, Num()).Should().Be("RADIANS(c.n)");
            }

            /// <remarks>
            /// The one name in the set that differs: Cosmos spells it ATN2, after T-SQL.
            /// </remarks>
            [Fact]
            public void Atan2IsSpelledAtn2()
            {
                Translate(SqlStdOperatorTable.ATAN2, Num(), Num()).Should().Be("ATN2(c.n, c.n)");
            }

            [Fact]
            public void PiIsWrittenAsANiladicCall()
            {
                Translate(SqlStdOperatorTable.PI).Should().Be("PI()");
            }

            [Fact]
            public void TruncateIsSpelledTrunc()
            {
                Translate(SqlStdOperatorTable.TRUNCATE, Num()).Should().Be("TRUNC(c.n)");
            }

            /// <remarks>
            /// Truncating to a number of decimal places is left out rather than guessed at; the arity of
            /// Cosmos's TRUNC has not been verified against the service.
            /// </remarks>
            [Fact]
            public void TruncateToDecimalPlacesIsDeclined()
            {
                CanTranslate(SqlStdOperatorTable.TRUNCATE, Num(), Int(2)).Should().BeFalse();
            }

            // ── Collections ───────────────────────────────────────────────────────────

            [Fact]
            public void CardinalityOverAnArrayCountsIt()
            {
                Translate(SqlStdOperatorTable.CARDINALITY, Array()).Should().Be("ARRAY_LENGTH(c.a)");
            }

            /// <remarks>
            /// SQL defines CARDINALITY over a map too, and ARRAY_LENGTH counts only an array. Emitting it
            /// for a map would report something meaningless rather than fail.
            /// </remarks>
            [Fact]
            public void CardinalityOverAMapIsDeclined()
            {
                CanTranslate(SqlStdOperatorTable.CARDINALITY, Map()).Should().BeFalse();
            }

            /// <remarks>
            /// The operands swap: SQL is value MEMBER OF multiset, and ARRAY_CONTAINS takes the array first.
            /// </remarks>
            [Fact]
            public void MemberOfBecomesArrayContainsWithTheOperandsSwapped()
            {
                Translate(SqlStdOperatorTable.MEMBER_OF, Lit("x"), Multiset()).Should().Be("ARRAY_CONTAINS(c.m, @p0)");
            }

            // ── TRIM ──────────────────────────────────────────────────────────────────

            [Fact]
            public void TrimSpecificationPicksTheCosmosFunction()
            {
                Translate(SqlStdOperatorTable.TRIM, Flag(SqlTrimFunction.Flag.BOTH), Lit(" "), Str()).Should().Be("TRIM(c.s)");
                Translate(SqlStdOperatorTable.TRIM, Flag(SqlTrimFunction.Flag.LEADING), Lit(" "), Str()).Should().Be("LTRIM(c.s)");
                Translate(SqlStdOperatorTable.TRIM, Flag(SqlTrimFunction.Flag.TRAILING), Lit(" "), Str()).Should().Be("RTRIM(c.s)");
            }

            /// <remarks>
            /// Cosmos's two-argument TRIM forms have not been verified, and emitting the one-argument form
            /// here would trim spaces where the query asked for something else.
            /// </remarks>
            [Fact]
            public void TrimOfANonSpaceCharacterIsDeclined()
            {
                CanTranslate(SqlStdOperatorTable.TRIM, Flag(SqlTrimFunction.Flag.BOTH), Lit("x"), Str()).Should().BeFalse();
            }

            // ── What each string function asks of its arguments (CosmosStringFunctions) ──

            /// <remarks>
            /// <c>LENGTH</c> counts code points at the service and Calcite counts UTF-16 units; Azure's <c>SUBSTRING</c>,
            /// <c>LEFT</c> and <c>RIGHT</c> count units and send a split pair as U+FFFD, the emulator's count code points.
            /// Without a surrogate a unit is a code point: a string holding a high unit, a character outside ASCII and one
            /// the service trims is counted alike, and one that may hold a surrogate is not, nor one nothing is known of.
            /// </remarks>
            [Fact]
            public void ACountPushesWhereNoCountedStringHoldsASurrogate()
            {
                foreach (var (op, operands) in Counts())
                {
                    Pushes(Translator(H | N | W), op, operands).Should().BeTrue(op.getName() + " over a string holding no surrogate");
                    Pushes(Translator(S), op, operands).Should().BeFalse(op.getName() + " over a string that may hold a surrogate");
                    Pushes(Undeclared(), op, operands).Should().BeFalse(op.getName() + " over a string nothing is known of");
                }
            }

            IEnumerable<(SqlOperator, RexNode[])> Counts() =>
            [
                (SqlStdOperatorTable.CHAR_LENGTH, [Str()]),
                (SqlStdOperatorTable.CHARACTER_LENGTH, [Str()]),
                (SqlStdOperatorTable.SUBSTRING, [Str(), Int(2), Int(1)]),
                (SqlLibraryOperators.LEFT, [Str(), Int(2)]),
                (SqlLibraryOperators.RIGHT, [Str(), Int(2)]),
            ];

            /// <remarks>
            /// A computed argument is asked through every input its characters come from: both sides of a concatenation,
            /// a literal's own characters, and a parameter, which may hold anything until it is bound. A count's other
            /// operands are numbers and are asked nothing.
            /// </remarks>
            [Fact]
            public void ACountAsksEveryInputOfAComputedArgument()
            {
                var concatenated = _rex.makeCall(SqlStdOperatorTable.CONCAT, Str(), Lit("x\u00DF"));

                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, concatenated).Should().BeTrue("both inputs hold no surrogate");
                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, _rex.makeCall(SqlStdOperatorTable.UPPER, concatenated)).Should().BeFalse("UPPER asks ASCII of its argument, and ß is not");
                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, Unicode("a\uD83D\uDE00")).Should().BeFalse("the literal holds a surrogate pair");
                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, _rex.makeCall(SqlStdOperatorTable.CONCAT, Str(), Param())).Should().BeFalse("a parameter may hold anything");
                Pushes(Translator(S), SqlStdOperatorTable.CHAR_LENGTH, _rex.makeCall(SqlStdOperatorTable.CONCAT, Lit("a"), Lit("b"))).Should().BeTrue("no path is counted");
            }

            /// <remarks>
            /// Calcite maps case by Java's full mapping and the service by a simple one, and they agree over ASCII alone:
            /// ß is <c>SS</c> to Calcite and stays at the service. A tab is ASCII, and a high unit, a surrogate and a
            /// character below U+D800 outside ASCII are each reason enough to compute the mapping in process.
            /// </remarks>
            [Fact]
            public void ACaseMappingPushesOverAsciiAlone()
            {
                foreach (var op in new[] { SqlStdOperatorTable.UPPER, SqlStdOperatorTable.LOWER })
                {
                    Pushes(Translator(W), op, Str()).Should().BeTrue(op.getName() + " over ASCII");
                    Pushes(Translator(N), op, Str()).Should().BeFalse(op.getName() + " over a string that may hold ß");
                    Pushes(Translator(H), op, Str()).Should().BeFalse(op.getName() + " over a string that may hold a high unit");
                    Pushes(Translator(S), op, Str()).Should().BeFalse(op.getName() + " over a string that may hold a surrogate");
                    Pushes(Undeclared(), op, Str()).Should().BeFalse(op.getName() + " over a string nothing is known of");
                }
            }

            /// <remarks>
            /// The service's trims remove white space, Calcite's U+0020 alone: a tab, U+00A0 or U+3000 at either end is
            /// trimmed at the service and kept by Calcite. A string holding none of them trims alike whatever else it
            /// holds.
            /// </remarks>
            [Fact]
            public void ATrimPushesWhereNothingTheServiceTrimsIsHeld()
            {
                foreach (var flag in new[] { SqlTrimFunction.Flag.BOTH, SqlTrimFunction.Flag.LEADING, SqlTrimFunction.Flag.TRAILING })
                {
                    Pushes(Translator(S | H | N), SqlStdOperatorTable.TRIM, Flag(flag), Lit(" "), Str()).Should().BeTrue(flag + " over a string holding nothing the service trims");
                    Pushes(Translator(W), SqlStdOperatorTable.TRIM, Flag(flag), Lit(" "), Str()).Should().BeFalse(flag + " over a string that may hold a tab");
                    Pushes(Undeclared(), SqlStdOperatorTable.TRIM, Flag(flag), Lit(" "), Str()).Should().BeFalse(flag + " over a string nothing is known of");
                }
            }

            /// <remarks>
            /// Azure's <c>INDEX_OF</c> counts UTF-16 units, as Calcite's <c>POSITION</c> does, so it is pushed whatever
            /// the string holds; the emulator counts code points, a difference the registry records
            /// (<c>Service.IndexOfCountsUnits</c>). So do the functions that agree everywhere.
            /// </remarks>
            [Fact]
            public void WhatAgreesEverywherePushesWhateverTheStringHolds()
            {
                Pushes(Undeclared(), SqlStdOperatorTable.POSITION, Lit("b"), Str()).Should().BeTrue();
                Pushes(Undeclared(), SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("b")).Should().BeTrue();
                Pushes(Undeclared(), SqlLibraryOperators.REVERSE, Str()).Should().BeTrue();
                Pushes(Undeclared(), SqlLibraryOperators.REPEAT, Str(), Int(2)).Should().BeTrue();
                Pushes(Undeclared(), SqlStdOperatorTable.CONCAT, Str(), Lit("!")).Should().BeTrue();
            }

            /// <remarks>
            /// A lone surrogate cannot be sent: the SDK's UTF-8 encoder refuses it while it writes the statement. Only a
            /// <c>U&amp;</c> literal holds one, inside <c>POSITION</c> or <c>REPLACE</c>, and wherever it stands the
            /// expression is computed in process. A pair is a character and is sent.
            /// </remarks>
            [Fact]
            public void ALiteralHoldingALoneSurrogateIsNeverSent()
            {
                Pushes(Undeclared(), SqlStdOperatorTable.POSITION, Unicode("\uD83D"), Str()).Should().BeFalse();
                Pushes(Undeclared(), SqlStdOperatorTable.REPLACE, Str(), Unicode("\uDE00"), Lit("!")).Should().BeFalse();
                Pushes(Undeclared(), SqlStdOperatorTable.POSITION, Unicode("\uD83D\uDE00"), Str()).Should().BeTrue("a pair is sent");
            }

            // ── Declined ──────────────────────────────────────────────────────────────

            [Fact]
            public void UnmappedFunctionIsDeclined()
            {
                CanTranslate(SqlStdOperatorTable.INITCAP, Str()).Should().BeFalse();
            }

            [Fact]
            public void WrongArityIsDeclined()
            {
                CanTranslate(SqlStdOperatorTable.REPLACE, Str(), Lit("a")).Should().BeFalse();
            }

        }

    }

}
