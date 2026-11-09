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
        /// The mappings are written over a path declared to hold strings of ASCII, holding nothing the service trims
        /// beside a space, where every string function computes what Calcite's does; what each asks of its arguments where
        /// they hold more is held to the rule apart, below (<c>CosmosStringFunctions</c>). Each declaration is a
        /// container's, bound to the reasoning the fixtures share, since S keys what the declaration's root says a path
        /// may hold: over a path not declared a string it keys a number and a boolean, which the rows answer undefined.
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

            /// <summary>A translator whose container declares <c>c.s</c> to hold strings of characters of these classes alone.</summary>
            CosmosRexTranslator Translator(JsonCharacterBand characters) => Declaring(new JsonClaim.OfType(JsonType.String), new JsonClaim.Characters(characters));

            /// <summary>A translator whose container declares <c>c.s</c> to hold strings, of characters nothing is known of.</summary>
            CosmosRexTranslator Strings() => Declaring(new JsonClaim.OfType(JsonType.String));

            /// <summary>A translator whose facts say nothing of what <c>c.s</c> holds.</summary>
            CosmosRexTranslator Undeclared() => new(_rex, _fields, new CosmosParameterList(), JsonFactSet.Empty);

            /// <summary>A translator whose container declares these claims of <c>c.s</c>, outright, as S imports them.</summary>
            CosmosRexTranslator Declaring(params JsonClaim[] claims) => Declaring(CosmosServiceTarget.Azure, claims);

            /// <summary>A translator whose container, on this target, declares these claims of <c>c.s</c>.</summary>
            CosmosRexTranslator Declaring(CosmosServiceTarget target, params JsonClaim[] claims)
            {
                var container = TestReasoning.Bind(new CosmosContainerMetadata("functions")
                    .WithFacts(claims.Select(claim => JsonFactRule.Unconditional(new JsonFact(JsonDocumentPath.Root.Property("s"), claim))))).WithServiceTarget(target);
                return new(_rex, _fields, new CosmosParameterList(), container.Facts.Declared, null, container);
            }

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
                    Pushes(Strings(), op, operands).Should().BeFalse(op.getName() + " over a string nothing is known of");
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
            /// operands are numbers and are asked nothing. Two literals concatenate to a <c>CHAR</c>, a reading no row was
            /// measured through, so S has no image of a count over them; a literal alone is counted by its own characters.
            /// A concatenation's literal is keyed apart from a stored operand (<c>FunctionKey.Literal</c>), its rows measured
            /// as a string of ASCII alone, so one holding <c>ß</c> beside a path stays in process.
            /// </remarks>
            [Fact]
            public void ACountAsksEveryInputOfAComputedArgument()
            {
                var concatenated = _rex.makeCall(SqlStdOperatorTable.CONCAT, Str(), Lit("x"));

                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, concatenated).Should().BeTrue("both inputs hold no surrogate");
                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, _rex.makeCall(SqlStdOperatorTable.UPPER, concatenated)).Should().BeFalse("UPPER asks ASCII of its argument, and the path may hold more");
                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, _rex.makeCall(SqlStdOperatorTable.CONCAT, Str(), Lit("x\u00DF"))).Should().BeFalse("no run sent a concatenation's literal beyond ASCII");
                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, Unicode("a\uD83D\uDE00")).Should().BeFalse("the literal holds a surrogate pair");
                Pushes(Translator(N), SqlStdOperatorTable.CHAR_LENGTH, _rex.makeCall(SqlStdOperatorTable.CONCAT, Str(), Param())).Should().BeFalse("a parameter may hold anything");
                Pushes(Translator(S), SqlStdOperatorTable.CHAR_LENGTH, _rex.makeCall(SqlStdOperatorTable.CONCAT, Lit("a"), Lit("b"))).Should().BeFalse("two literals concatenate to a CHAR");
                Pushes(Translator(S), SqlStdOperatorTable.CHAR_LENGTH, Lit("ab")).Should().BeTrue("no path is counted");
            }

            /// <remarks>
            /// Calcite maps case by Java's full mapping and the service by a simple one, and they agree over ASCII and where
            /// the cased letters measured each band alike (<c>Service.CaseMappingOverCasedLetters</c>): ß is <c>SS</c> to
            /// Calcite and stays at the service, so a character below U+D800 outside ASCII computes the mapping in process.
            /// On Azure (run 2) both map a surrogate as Java does, and LOWER the high band, where UPPER leaves the ligatures
            /// Java writes as two; the emulator maps the scripts cased after Java 8's tables, so it parts over a surrogate.
            /// </remarks>
            [Fact]
            public void ACaseMappingPushesWhereTheCasedLettersAnswerAsCalcites()
            {
                foreach (var op in new[] { SqlStdOperatorTable.UPPER, SqlStdOperatorTable.LOWER })
                {
                    Pushes(Translator(W), op, Str()).Should().BeTrue(op.getName() + " over ASCII");
                    Pushes(Translator(N), op, Str()).Should().BeFalse(op.getName() + " over a string that may hold ß");
                    Pushes(Translator(S), op, Str()).Should().BeTrue(op.getName() + " over a string that may hold a surrogate, on Azure");
                    Pushes(Declaring(CosmosServiceTarget.Emulator, new JsonClaim.OfType(JsonType.String), new JsonClaim.Characters(S)), op, Str()).Should().BeFalse(op.getName() + " over a surrogate, on the emulator");
                    Pushes(Strings(), op, Str()).Should().BeFalse(op.getName() + " over a string nothing is known of");
                }

                Pushes(Translator(H), SqlStdOperatorTable.UPPER, Str()).Should().BeFalse("UPPER leaves U+FB00, which Java writes as two");
                Pushes(Translator(H), SqlStdOperatorTable.LOWER, Str()).Should().BeTrue("the fullwidth capitals map as Java maps them");
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
                    Pushes(Strings(), SqlStdOperatorTable.TRIM, Flag(flag), Lit(" "), Str()).Should().BeFalse(flag + " over a string nothing is known of");
                }
            }

            /// <remarks>
            /// Azure's <c>INDEX_OF</c> counts UTF-16 units, as Calcite's <c>POSITION</c> does, so it is pushed whatever
            /// the string holds; the emulator counts code points, a difference the registry records
            /// (<c>Service.IndexOfCountsUnits</c>). So do the functions that agree everywhere. Over a path not declared a
            /// string none does: a number or a boolean there is undefined to the service's function, and its text Calcite's.
            /// <c>POSITION</c>'s pattern is keyed as the operand it is, measured as a string of ASCII alone that holds
            /// something: a literal, or a path the declaration proves present and every string it may be such a one.
            /// </remarks>
            [Fact]
            public void WhatAgreesEverywherePushesWhateverTheStringHolds()
            {
                Pushes(Strings(), SqlStdOperatorTable.POSITION, Lit("b"), Str()).Should().BeTrue();
                Pushes(Strings(), SqlLibraryOperators.REVERSE, Str()).Should().BeTrue();
                Pushes(Strings(), SqlStdOperatorTable.CONCAT, Str(), Lit("!")).Should().BeTrue();
                Pushes(Strings(), SqlLibraryOperators.CONCAT_FUNCTION, Str(), Lit("!")).Should().BeTrue("MySQL's CONCAT is null over a null, as the service's is");

                Pushes(Undeclared(), SqlStdOperatorTable.POSITION, Lit("b"), Str()).Should().BeFalse("S keys a number and a boolean the path may hold, which the rows answer undefined");

                Pushes(PresentOneOf("b", "cd"), SqlStdOperatorTable.POSITION, Str(), Lit("abc")).Should().BeTrue("a pattern read from a path present and one of strings of no band that hold something keys the cell measured");
                Pushes(Strings(), SqlStdOperatorTable.POSITION, Str(), Lit("abc")).Should().BeFalse("a pattern read from a path may be empty or absent");
            }

            /// <remarks>
            /// PostgreSQL's and SQL Server's <c>CONCAT</c>, and Oracle's two-argument one, read a null as the empty string
            /// where the service's <c>CONCAT</c> is undefined over one, and are spelled alike: the operator decides, not the
            /// name.
            /// </remarks>
            [Fact]
            public void AConcatenationThatReadsANullAsEmptyIsComputedInProcess()
            {
                Pushes(Strings(), SqlLibraryOperators.CONCAT_FUNCTION_WITH_NULL, Str(), Lit("!")).Should().BeFalse();
                Pushes(Strings(), SqlLibraryOperators.CONCAT2, Str(), Lit("!")).Should().BeFalse();
            }

            /// <summary>A translator whose container declares <c>c.s</c> to be one of these strings.</summary>
            CosmosRexTranslator OneOf(params string[] values) => OneOf(CosmosServiceTarget.Azure, values);

            /// <summary>A translator whose container, on this target, declares <c>c.s</c> to be one of these strings.</summary>
            CosmosRexTranslator OneOf(CosmosServiceTarget target, params string[] values) => Declaring(target, new JsonClaim.OneOf(values.Select(v => (JsonScalar)v).ToArray()));

            /// <summary>A translator whose Azure container declares <c>c.s</c> present, and one of these strings.</summary>
            CosmosRexTranslator PresentOneOf(params string[] values) => Declaring(new JsonClaim.Present(), new JsonClaim.OneOf(values.Select(v => (JsonScalar)v).ToArray()));

            /// <remarks>
            /// The service's <c>REPLICATE</c> is undefined for a negative count, where Calcite's <c>REPEAT</c> is the empty
            /// string, and on Azure past 10,000 code points, inclusive, where Calcite repeats on. So S keys the result by its
            /// length against that bound, the longest string the facts allow times a literal count: the rows answer within it
            /// as Calcite does, and past it, or where nothing bounds it, Azure's answer is undefined and the emulator's
            /// unmeasured (G6). A literal subject is keyed apart from a stored one (<c>FunctionKey.Literal</c>), and as every
            /// string is, by no band as well as by each band it holds: run 3 sent one holding U+0000, which Azure repeats as
            /// Calcite does, but no run sent one of no band, so a literal subject stays in process whatever it holds and
            /// whatever its length.
            /// </remarks>
            [Fact]
            public void ARepetitionPushesWhereTheCountKeepsItWithinWhatTheServiceRepeats()
            {
                Pushes(OneOf("ab", "cde"), SqlLibraryOperators.REPEAT, Str(), Int(3333)).Should().BeTrue("3,333 times three is 9,999");
                Pushes(OneOf("ab", "c\U0001F600e"), SqlLibraryOperators.REPEAT, Str(), Int(3333)).Should().BeTrue("a pair is one code point");
                Pushes(OneOf("ab", "cde"), SqlLibraryOperators.REPEAT, Str(), Int(3334)).Should().BeFalse("3,334 times three is 10,002");
                Pushes(OneOf(CosmosServiceTarget.Emulator, "ab", "cde"), SqlLibraryOperators.REPEAT, Str(), Int(3333)).Should().BeTrue("both targets repeat that far");
                Pushes(OneOf(CosmosServiceTarget.Emulator, "ab", "cde"), SqlLibraryOperators.REPEAT, Str(), Int(3334)).Should().BeFalse("no run measured the emulator past it");
                Pushes(OneOf("ab", "cde"), SqlLibraryOperators.REPEAT, Str(), Int(-1)).Should().BeFalse("REPLICATE is undefined for a negative count");
                Pushes(Strings(), SqlLibraryOperators.REPEAT, Str(), Int(0)).Should().BeTrue("a count of 0 is the empty string at both ends");
                Pushes(Strings(), SqlLibraryOperators.REPEAT, Str(), Int(2)).Should().BeFalse("nothing bounds the string's length");
                Pushes(OneOf("ab"), SqlLibraryOperators.REPEAT, Str(), Num()).Should().BeFalse("a count from a column may be anything");
                Pushes(Undeclared(), SqlLibraryOperators.REPEAT, Lit("ab"), Int(5000)).Should().BeFalse("no run sent a literal subject of no band");
                Pushes(Undeclared(), SqlLibraryOperators.REPEAT, Lit("ab"), Int(5001)).Should().BeFalse();
                Pushes(Undeclared(), SqlLibraryOperators.REPEAT, Lit("a\0b"), Int(2)).Should().BeFalse("a literal holding U+0000 is keyed by no band too, which no run sent");
            }

            /// <remarks>
            /// The service's <c>REPLACE</c> of an empty search is undefined, where Calcite's is the subject. So the search is
            /// proved to hold something: a literal that does, or a path whose strings a domain bounds to one unit or more. A
            /// parameter may be empty until it is bound. Azure's searches were measured as run 1's literals are, strings of
            /// no band holding something, and a path keys that cell alone where the declaration proves it present and every
            /// string it may be one of them, so it pushes there; a path that may be absent keys a cell Azure has not
            /// measured, which is computed in process there until a run measures it (TODO.md: run 3 asked one read from a
            /// path and was left unanswered, which measures nothing). Run 3 measured the replacement over every kind and
            /// band, which Azure answers as Calcite does, so a replacement read from any path of strings pushes there. The
            /// emulator's rows answer an absent operand as Calcite does.
            /// </remarks>
            /// <remarks>
            /// A rank clause's keyword is held to S's image of the function computing it, as a column is (G6's check: the
            /// gate had moved off the writer a rank clause writes through, which pushed both of these): a <c>REPLACE</c> of an
            /// empty search and the concatenation that reads a null as empty stay in process, the plain one pushes.
            /// </remarks>
            [Fact]
            public void ARankClausesKeywordIsHeldToTheFunctionRows()
            {
                string Rank(RexNode keyword) => Translator().TranslateRank(_rex.makeCall(CosmosOperators.FullTextScore, Str(), keyword));

                Rank(_rex.makeCall(SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("b"))).Should().Be("FULLTEXTSCORE(c.s, REPLACE(c.s, @p0, @p1))");
                var empty = () => Rank(_rex.makeCall(SqlStdOperatorTable.REPLACE, Str(), Lit(""), Lit("b")));
                empty.Should().Throw<CosmosTranslationException>().WithMessage("REPLACE is computed in process*");
                var concat = () => Rank(_rex.makeCall(SqlLibraryOperators.CONCAT_FUNCTION_WITH_NULL, Str(), Lit("!")));
                concat.Should().Throw<CosmosTranslationException>().WithMessage("CONCAT is computed in process*");
            }

            [Fact]
            public void AReplacementPushesWhereTheSearchHoldsSomething()
            {
                Pushes(Translator(), SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("b")).Should().BeTrue();
                Pushes(Translator(), SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("")).Should().BeTrue("an empty replacement, which Azure answers as Calcite does (run 3)");
                Pushes(Translator(), SqlStdOperatorTable.REPLACE, Str(), Lit(""), Lit("b")).Should().BeFalse("an empty search");
                Pushes(Translator(), SqlStdOperatorTable.REPLACE, Str(), Param(), Lit("b")).Should().BeFalse("a parameter may be empty");
                Pushes(OneOf(CosmosServiceTarget.Emulator, "ab", "c"), SqlStdOperatorTable.REPLACE, Lit("abc"), Str(), Lit("!")).Should().BeTrue("every string the search may be holds something");
                Pushes(OneOf(CosmosServiceTarget.Emulator, "ab", ""), SqlStdOperatorTable.REPLACE, Lit("abc"), Str(), Lit("!")).Should().BeFalse("the search may be empty");
                Pushes(PresentOneOf("ab", "c"), SqlStdOperatorTable.REPLACE, Lit("abc"), Str(), Lit("!")).Should().BeTrue("the search is present and every string it may be is of no band and holds something, the cell Azure measured");
                Pushes(PresentOneOf("x", "yz"), SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeTrue("a replacement read from a path answers as Calcite's");
                Pushes(Strings(), SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeTrue("over every band, and absent, as run 3 measured it on Azure");
                Pushes(Translator(JsonCharacterBand.Nul), SqlStdOperatorTable.REPLACE, Lit("abc"), Lit("b"), Str()).Should().BeTrue("a U+0000 among them");
                Pushes(PresentOneOf("ab", ""), SqlStdOperatorTable.REPLACE, Lit("abc"), Str(), Lit("!")).Should().BeFalse("the search may be empty");
                Pushes(OneOf("ab", "c"), SqlStdOperatorTable.REPLACE, Lit("abc"), Str(), Lit("!")).Should().BeFalse("the search may be absent, which Azure has not measured");
            }

            /// <remarks>
            /// Azure's <c>REPLACE</c> reads its subject only up to its first U+0000 — <c>REPLACE(s, 'b', '!')</c> leaves
            /// <c>a</c>U+0000<c>b</c> as it is — and a search holding one replaces every byte; a replacement holding one is
            /// written as it is. So the subject and the search are asked to hold none. Run 3 sent a replacement holding one, a
            /// literal and a stored string, and Azure answered both as Calcite does, so a replacement holding U+0000 pushes.
            /// </remarks>
            [Fact]
            public void AReplacementPushesWhereNeitherTheSubjectNorTheSearchHoldsNul()
            {
                const JsonCharacterBand Z = JsonCharacterBand.Nul;

                Pushes(Translator(S | H | N | W), SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("b")).Should().BeTrue("the subject holds no U+0000");
                Pushes(Translator(S | H | N | W), SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("\0")).Should().BeTrue("Azure writes a replacement holding U+0000 as Calcite does (run 3)");
                Pushes(Translator(Z), SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("b")).Should().BeFalse("the subject may hold U+0000");
                Pushes(Strings(), SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("b")).Should().BeFalse("nothing is known of the subject's characters");
                Pushes(Translator(JsonCharacterBand.None), SqlStdOperatorTable.REPLACE, Str(), Lit("\0"), Lit("b")).Should().BeFalse("the search holds U+0000");
            }

            /// <remarks>
            /// A lone surrogate cannot be sent: the SDK's UTF-8 encoder refuses it while it writes the statement. Only a
            /// <c>U&amp;</c> literal holds one, inside <c>POSITION</c> or <c>REPLACE</c>, and wherever it stands the
            /// expression is computed in process. A pair is a character and is sent, compared with a path; as
            /// <c>POSITION</c>'s pattern, keyed as the operand it is and measured as a string of ASCII alone, it stays in
            /// process.
            /// </remarks>
            [Fact]
            public void ALiteralHoldingALoneSurrogateIsNeverSent()
            {
                Pushes(Strings(), SqlStdOperatorTable.POSITION, Unicode("\uD83D"), Str()).Should().BeFalse();
                Pushes(Strings(), SqlStdOperatorTable.REPLACE, Str(), Unicode("\uDE00"), Lit("!")).Should().BeFalse();
                Pushes(Strings(), SqlStdOperatorTable.EQUALS, Str(), Unicode("\uD83D\uDE00")).Should().BeTrue("a pair is sent");
                Pushes(Strings(), SqlStdOperatorTable.POSITION, Unicode("\uD83D\uDE00"), Str()).Should().BeFalse("no run sent a pattern beyond ASCII");
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
