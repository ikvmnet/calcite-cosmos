using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingRexBuilderTests;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The wrong answers the 3A audit found waiting for h15 (audit plan §1, items 1 to 6), each through S's own judgement,
    /// answered right by its group's form: CONCAT's own name and rows (G6), REPLACE's search and replacement and a window's
    /// length keyed by operand (F2), the case mapping's rows (F5), the emulator's stored escapes as its rows (F2), and a
    /// literal a cast truncated, read as the casts' terms, which <c>RETURNING DECIMAL</c>'s row alone takes (G8). S proves
    /// no form over what the loader reads that keeps another row than Calcite in process on a document, or IrEval where the
    /// reading is Calcite's, the service's answer CosmosEval's or, where CosmosEval does not model the operand, the one
    /// measured; and each case is declined under every set asked (<c>DESIGN.md</c>, <i>The defects answered before h15</i>).
    /// </summary>
    public sealed class RenderingDefectTests
    {

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias, places: true);

        static readonly JsonDocumentPath S = JsonDocumentPath.Root.Property("s"), T = JsonDocumentPath.Root.Property("t");

        static readonly RenderingSet[] AllSets = [RenderingSet.Production, RenderingSet.Emulator, RenderingSet.Portable];

        static readonly PathValues Strings = PathValues.OfKinds(ValueSet.Of(StringType));

        /// <summary>One defect: the predicate S reads, how its paths are imported, the sets asked, the documents judged, and the service where CosmosEval does not say.</summary>
        sealed record Defect(SqlPredicate Read, Func<J.Obj, Truths?> Calcite, Func<JsonDocumentPath, (PathValues Values, JsonCharacterBand Chars)> Import,
            RenderingSet[] Sets, J.Obj[] Documents, Func<J.Obj, ServiceOutcomes?>? Measured = null);

        static Defect FromRex(RexNode rex, Func<JsonDocumentPath, (PathValues, JsonCharacterBand)> import, RenderingSet[] sets, J.Obj[] documents, Func<J.Obj, ServiceOutcomes?>? measured = null) =>
            new(Loading.Read(rex), CalciteInProcess.Compile(rex, Fields, Readings) ?? throw new InvalidOperationException($"{rex} does not compile alone"), import, sets, documents, measured);

        /// <summary>
        /// A place written as Spell would write it without its literal refusal (G1), which gives a literal holding U+0000 or
        /// U+FDD0 no place yet: compared with a string, Calcite's truth IrEval's.
        /// </summary>
        static Defect Written(string function, SqlValue[] operands, string compared, RenderingSet[] sets, J.Obj[] documents, Func<J.Obj, ServiceOutcomes?> measured)
        {
            var spelled = function == "REPLACE" ? "REPLACE" : "CONCAT";
            var call = new SqlValue.Call(function, operands) with { Type = "VARCHAR", Spelled = $"{spelled}({string.Join(", ", operands.Select(o => o is SqlValue.Text t ? SqlValue.Spell(t.Path) : o is SqlValue.Literal l ? l.Value : throw new ArgumentException(o.ToString())))})" };
            var read = new SqlPredicate.Compare("=", call, SqlValue.Of(compared));
            return new(read, d => RenderingSoundnessTests.Tally.Calcite(read, d, new World(0)) is [var truth, ..] ? truth : null, _ => (Strings, JsonCharacterBand.None), sets, documents, measured);
        }

        static RexNode Lit(string value) => Rex.makeLiteral(value);

        static RexNode Unicode(string value) => Rex.makeLiteral(new org.apache.calcite.util.NlsString(value, "UTF-16LE", SqlCollation.IMPLICIT),
            RenderingRexBuilderTests.Types.createTypeWithCharsetAndCollation(RenderingRexBuilderTests.Types.createSqlType(SqlTypeName.VARCHAR, value.Length), java.nio.charset.Charset.forName("UTF-16LE"), SqlCollation.IMPLICIT), false);

        static RexNode Int(int value) => Rex.makeExactLiteral(new java.math.BigDecimal(value));

        static RexNode Eq(RexNode left, RexNode right) => Rex.makeCall(SqlStdOperatorTable.EQUALS, left, right);

        static J.Obj Doc(params (string Name, J Value)[] properties) =>
            new(new SortedDictionary<string, J>(properties.ToDictionary(p => p.Name, p => p.Value), StringComparer.Ordinal));

        static readonly IReadOnlyDictionary<string, Func<Defect>> Defects = new Dictionary<string, Func<Defect>>(StringComparer.Ordinal)
        {
            // Item 1. A window of a negative length, which Calcite raises over and the service answers "" for: the length is
            // keyed below 0, where the emulator's rows part and Azure's are unmeasured, from 1 and from before the string.
            ["SubstringOfANegativeLength"] = () => FromRex(
                Rex.makeCall(SqlStdOperatorTable.OR,
                    Eq(Rex.makeCall(SqlStdOperatorTable.SUBSTRING, JsonValue("s", SqlTypeName.VARCHAR), Int(1), Int(-1)), Lit("")),
                    Eq(Rex.makeCall(SqlStdOperatorTable.SUBSTRING, JsonValue("s", SqlTypeName.VARCHAR), Int(0), Int(-1)), Lit(""))),
                _ => (Strings, JsonCharacterBand.None), AllSets,
                [Doc(("s", new J.Str("abc"))), Doc(("s", new J.Str("")))]),

            // Item 2. JSON_VALUE(... RETURNING DOUBLE) = CAST(CAST(2.5 AS DECIMAL(19, 0)) AS DOUBLE): the loader read the
            // literal Calcite truncates to 2 as 2.5, so 2.0 was dropped and 2.5 kept. The casts are terms now, and what they
            // denote a moved 2.5, which only RETURNING DECIMAL's row takes (G8): RETURNING DOUBLE's comparison is no atom.
            ["ReturningDoubleReadsATruncatedLiteral"] = () => FromRex(
                Eq(JsonValue("n", SqlTypeName.DOUBLE), Rex.makeCast(RenderingRexBuilderTests.Types.createSqlType(SqlTypeName.DOUBLE),
                    Rex.makeCast(RenderingRexBuilderTests.Types.createSqlType(SqlTypeName.DECIMAL, 19, 0), Rex.makeExactLiteral(new java.math.BigDecimal("2.5"))))),
                _ => (PathValues.OfKinds(ValueSet.Of(IntegerType, FractionType)), JsonCharacterBand.None), AllSets,
                [Doc(("n", new J.Num(2.0m))), Doc(("n", new J.Num(2.5m))), Doc(("n", new J.Num(3.0m)))]),

            // Item 3. The PostgreSQL, SQL Server and Oracle CONCAT, which read a null as empty, were loaded as CONCAT, whose
            // rows answer Same over a null: CONCAT(s, '!') = '!' dropped the absent and null s Calcite keeps. Their own rows
            // say Undefined.
            ["ConcatThatReadsNullAsEmptyIsReadAsConcat"] = () => FromRex(
                Rex.makeCall(SqlStdOperatorTable.OR,
                    Eq(Rex.makeCall(SqlLibraryOperators.CONCAT_FUNCTION_WITH_NULL, JsonValue("s", SqlTypeName.VARCHAR), Lit("!")), Lit("!")),
                    Eq(Rex.makeCall(SqlLibraryOperators.CONCAT2, JsonValue("s", SqlTypeName.VARCHAR), Lit("?")), Lit("?"))),
                _ => (PathValues.OfKinds(ValueSet.Of(StringType, NullType, PathValues.AbsentKind)), JsonCharacterBand.None), AllSets,
                [Doc(), Doc(("s", new J.Null())), Doc(("s", new J.Str("a")))]),

            // Item 4. REPLACE keyed on its subject alone: a search holding U+0000 replaces every unit on both targets
            // (Service.NulEndsWhatReplaceAndContainsRead on Azure; measured on EN20261019 and EN20260907). The search is keyed.
            ["ReplaceOfASearchHoldingNul"] = () => Written("REPLACE", [new SqlValue.Text(S), SqlValue.Of("\0"), SqlValue.Of("x")], "abc",
                [RenderingSet.Production, RenderingSet.Emulator], [Doc(("s", new J.Str("abc")))],
                d => IrEval.At(d, S) is J.Str s ? new string('x', s.Value.Length) == "abc" ? ServiceOutcomes.True : ServiceOutcomes.False : null),

            // Found beside item 6. REPLACE keyed on its subject alone: a replacement that is a number is undefined at the
            // service (measured on EN20261019 and EN20260907), where Calcite replaces with the number's text.
            ["ReplaceOfAReplacementThatIsNoString"] = () => FromRex(
                Eq(Rex.makeCall(SqlStdOperatorTable.REPLACE, JsonValue("s", SqlTypeName.VARCHAR), Lit("a"), JsonValue("t", SqlTypeName.VARCHAR)), Lit("5")),
                path => path == S ? (Strings, JsonCharacterBand.None) : (PathValues.OfKinds(ValueSet.Of(IntegerType)), JsonCharacterBand.None), [RenderingSet.Emulator],
                [Doc(("s", new J.Str("a")), ("t", new J.Num(5)))],
                d => IrEval.At(d, T) is J.Num ? ServiceOutcomes.Undefined : null),

            // Item 5. A case mapping over a surrogate, and LOWER over the high band, whose rows said Same where no witness held
            // a letter the mapping changes: Java's maps U+10428 and U+10400, and U+FF21 to U+FF41. The rows part (F5).
            ["CaseMappingOverASurrogate"] = () => FromRex(
                Rex.makeCall(SqlStdOperatorTable.OR,
                    Eq(Rex.makeCall(SqlStdOperatorTable.UPPER, JsonValue("s", SqlTypeName.VARCHAR)), Unicode("\U00010400")),
                    Eq(Rex.makeCall(SqlStdOperatorTable.LOWER, JsonValue("s", SqlTypeName.VARCHAR)), Unicode("\U00010428"))),
                _ => (Strings, JsonCharacterBand.Surrogates), AllSets,
                [Doc(("s", new J.Str("\U00010428"))), Doc(("s", new J.Str("\U00010400")))]),
            ["LowerOverTheHighBand"] = () => FromRex(
                Eq(Rex.makeCall(SqlStdOperatorTable.LOWER, JsonValue("s", SqlTypeName.VARCHAR)), Unicode("ａ")),
                _ => (Strings, JsonCharacterBand.HighBmp), AllSets,
                [Doc(("s", new J.Str("Ａ"))), Doc(("s", new J.Str("ａ")))]),

            // Item 6. What the emulator makes of a literal it stores escaped: a U+0000 made there fails the statement (Postgres
            // 22P05), and a U+FDD0 before a 0 is read back as U+0000. The emulator's rows say so.
            ["ConcatOfANulTheEmulatorMakes"] = () => Written("CONCAT", [new SqlValue.Text(S), SqlValue.Of("\0")], "a\0",
                [RenderingSet.Emulator], [Doc(("s", new J.Str("a")))],
                d => IrEval.At(d, S) is J.Str ? ServiceOutcomes.Fails : null),
            ["ReplacementByAnEscapeTheEmulatorMisreads"] = () => Written("REPLACE", [new SqlValue.Text(S), SqlValue.Of("a"), SqlValue.Of("﷐")], "﷐0",
                [RenderingSet.Emulator], [Doc(("s", new J.Str("a0")))],
                d => IrEval.At(d, S) is J.Str { Value: "a0" } ? ServiceOutcomes.False : null),
        };

        public static TheoryData<string> Names => [.. Defects.Keys];

        /// <summary>
        /// Each defect answers as Calcite: no proof whose form keeps another row than Calcite on a document, under any set
        /// asked, through both theories; and none at all, each declined where its rows part, are unmeasured, or the loader
        /// reads no constant.
        /// </summary>
        [Theory]
        [MemberData(nameof(Names))]
        public void EachDefectAnswersAsCalcite(string name)
        {
            var (found, proven) = Judge(Defects[name]());
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, found.Concat(proven)));
            found.Should().BeEmpty();
            proven.Should().BeEmpty($"{name} is declined under every set asked");
        }

        /// <summary>Every row S's proven form keeps otherwise than Calcite, on every target each set serves, and every form proven.</summary>
        static (List<string> Found, List<string> Proven) Judge(Defect defect)
        {
            var (found, proven) = (new List<string>(), new List<string>());
            var places = defect.Read.Computed.Where(c => c.Spelled is not null).DistinctBy(c => c.Spelled).ToDictionary(c => c.Spelled!, StringComparer.Ordinal);
            foreach (var theory in new[] { ProbeTheory, SwitchedTheory })
                foreach (var set in defect.Sets)
                {
                    using var bundle = theory.Open(set);
                    var term = bundle.Write(defect.Read);
                    foreach (var path in defect.Read.Places)
                        bundle.Import(Source, path, defect.Import(path).Values, ClassSet(defect.Import(path).Chars));
                    foreach (var call in bundle.ComputedCalls)
                        bundle.ImportComputed(call, ClassSet(defect.Import(defect.Read.Places.First()).Chars));

                    bundle.Generate(term);
                    bundle.RunGenerate(RenderingBundle.WidthOf([defect.Read]));
                    bundle.Failure.Should().BeNull();
                    if (bundle.ReadBest(term) is not { Kind: RenderingVerdictKind.Proven, Best: { } best })
                        continue;

                    proven.Add($"{set}: {best.Text}");
                    foreach (var document in defect.Documents)
                        foreach (var target in Measured.Serves[set])
                        {
                            var calcite = defect.Calcite(document);
                            var service = defect.Measured?.Invoke(document) ?? CosmosEval.On(best, document, target, places);
                            if (calcite is Truths.True or Truths.False or Truths.Unknown && service is { } answer
                                && (answer == ServiceOutcomes.Fails || (calcite == Truths.True) != (answer == ServiceOutcomes.True)))
                                found.Add($"{set}, {target}: {best.Text} on {Documents.Json(document)}: Calcite {calcite}, the service {answer}");
                        }
                }

            GC.KeepAlive(ProbeTheory);
            GC.KeepAlive(SwitchedTheory);
            return (found, proven);
        }

    }

}
