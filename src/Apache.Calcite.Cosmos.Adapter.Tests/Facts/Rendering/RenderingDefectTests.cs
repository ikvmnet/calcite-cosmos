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
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingRexBuilderTests;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The wrong answers the 3A audit found waiting for h15 (audit plan §1), each shown through S's own judgement: S proves a
    /// form over what the loader reads, and on a document Calcite in process, or IrEval where the reading is Calcite's,
    /// keeps a row the form does not, or drops one it keeps, the service's answer CosmosEval's or, where CosmosEval does not
    /// model the operand, the one measured. A held defect must still be found, so that its proper fix shows here as a name
    /// to strike from <see cref="Held"/>; struck, its case asserts nothing is found (<c>DESIGN.md</c>, <i>The defects held
    /// before h15</i>).
    /// </summary>
    public sealed class RenderingDefectTests
    {

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias, places: true);

        static readonly JsonDocumentPath S = JsonDocumentPath.Root.Property("s"), T = JsonDocumentPath.Root.Property("t"), N = JsonDocumentPath.Root.Property("n");

        static readonly RenderingSet[] AllSets = [RenderingSet.Production, RenderingSet.Emulator, RenderingSet.Portable];

        static readonly PathValues Strings = PathValues.OfKinds(ValueSet.Of(StringType));

        /// <summary>The defects held to still fail, each until the proper fix the audit names for it lands.</summary>
        static readonly IReadOnlySet<string> Held = new HashSet<string>(StringComparer.Ordinal)
        {
            "ReturningDoubleReadsATruncatedLiteral",
            "ConcatThatReadsNullAsEmptyIsReadAsConcat",
            "ReplaceOfASearchHoldingNul",
            "ReplaceOfAReplacementThatIsNoString",
        };

        /// <summary>One defect: the predicate S reads, how its paths are imported, the sets asked, the documents judged, and the service where CosmosEval does not say.</summary>
        sealed record Defect(SqlPredicate Read, Func<J.Obj, Truths?> Calcite, Func<JsonDocumentPath, (PathValues Values, JsonCharacterBand Chars)> Import,
            RenderingSet[] Sets, J.Obj[] Documents, Func<J.Obj, ServiceOutcomes?>? Measured = null);

        static Defect FromRex(RexNode rex, Func<JsonDocumentPath, (PathValues, JsonCharacterBand)> import, RenderingSet[] sets, J.Obj[] documents, Func<J.Obj, ServiceOutcomes?>? measured = null) =>
            new(Loading.Read(rex), CalciteInProcess.Compile(rex, Fields, Readings) ?? throw new InvalidOperationException($"{rex} does not compile alone"), import, sets, documents, measured);

        static RexNode Lit(string value) => Rex.makeLiteral(value);

        static RexNode Eq(RexNode left, RexNode right) => Rex.makeCall(SqlStdOperatorTable.EQUALS, left, right);

        static J.Obj Doc(params (string Name, J Value)[] properties) =>
            new(new SortedDictionary<string, J>(properties.ToDictionary(p => p.Name, p => p.Value), StringComparer.Ordinal));

        static readonly IReadOnlyDictionary<string, Func<Defect>> Defects = new Dictionary<string, Func<Defect>>(StringComparer.Ordinal)
        {
            // Item 2. JSON_VALUE(... RETURNING DOUBLE) = CAST(CAST(2.5 AS DECIMAL(19, 0)) AS DOUBLE): the loader reads the
            // literal Calcite truncates to 2 as 2.5 (`truncates: reading is SqlValue.Typed`), so 2.0 is dropped and 2.5 kept.
            ["ReturningDoubleReadsATruncatedLiteral"] = () => FromRex(
                Eq(JsonValue("n", SqlTypeName.DOUBLE), Rex.makeCast(RenderingRexBuilderTests.Types.createSqlType(SqlTypeName.DOUBLE),
                    Rex.makeCast(RenderingRexBuilderTests.Types.createSqlType(SqlTypeName.DECIMAL, 19, 0), Rex.makeExactLiteral(new java.math.BigDecimal("2.5"))))),
                _ => (PathValues.OfKinds(ValueSet.Of(IntegerType, FractionType)), JsonCharacterBand.None), AllSets,
                [Doc(("n", new J.Num(2.0m))), Doc(("n", new J.Num(2.5m))), Doc(("n", new J.Num(3.0m)))]),

            // Item 3. The PostgreSQL and Oracle CONCAT, which read a null as empty, are loaded as CONCAT, whose rows answer
            // Same over a null: CONCAT(s, '!') = '!' drops the absent and null s Calcite keeps.
            ["ConcatThatReadsNullAsEmptyIsReadAsConcat"] = () => FromRex(
                Rex.makeCall(SqlStdOperatorTable.OR,
                    Eq(Rex.makeCall(SqlLibraryOperators.CONCAT_FUNCTION_WITH_NULL, JsonValue("s", SqlTypeName.VARCHAR), Lit("!")), Lit("!")),
                    Eq(Rex.makeCall(SqlLibraryOperators.CONCAT2, JsonValue("s", SqlTypeName.VARCHAR), Lit("?")), Lit("?"))),
                _ => (PathValues.OfKinds(ValueSet.Of(StringType, NullType, PathValues.AbsentKind)), JsonCharacterBand.None), AllSets,
                [Doc(), Doc(("s", new J.Null())), Doc(("s", new J.Str("a")))]),

            // Item 4. REPLACE keyed on its subject alone: a search holding U+0000 replaces every unit on both targets
            // (Service.NulEndsWhatReplaceAndContainsRead on Azure; measured here on EN20261019 and EN20260907). The loader
            // gives the call no place while Spell refuses a literal holding U+0000, so the place is written as Spell
            // would write it without that refusal, which G1 moves into S.
            ["ReplaceOfASearchHoldingNul"] = () =>
            {
                var call = new SqlValue.Call("REPLACE", [new SqlValue.Text(S), SqlValue.Of("\0"), SqlValue.Of("x")])
                    with { Type = "VARCHAR", Spelled = $"REPLACE({SqlValue.Spell(S)}, {Of("\0")}, {Of("x")})" };
                var read = new SqlPredicate.Compare("=", call, SqlValue.Of("abc"));
                return new(read, d => RenderingSoundnessTests.Tally.Calcite(read, d, new World(0)) is [var truth, ..] ? truth : null,
                    _ => (Strings, JsonCharacterBand.None), [RenderingSet.Production, RenderingSet.Emulator], [Doc(("s", new J.Str("abc")))],
                    d => IrEval.At(d, S) is J.Str s ? new string('x', s.Value.Length) == "abc" ? ServiceOutcomes.True : ServiceOutcomes.False : null);
            },

            // Found beside item 6. REPLACE keyed on its subject alone: a replacement that is a number is undefined at the
            // service (measured on EN20261019 and EN20260907), where Calcite replaces with the number's text.
            ["ReplaceOfAReplacementThatIsNoString"] = () => FromRex(
                Eq(Rex.makeCall(SqlStdOperatorTable.REPLACE, JsonValue("s", SqlTypeName.VARCHAR), Lit("a"), JsonValue("t", SqlTypeName.VARCHAR)), Lit("5")),
                path => path == S ? (Strings, JsonCharacterBand.None) : (PathValues.OfKinds(ValueSet.Of(IntegerType)), JsonCharacterBand.None), [RenderingSet.Emulator],
                [Doc(("s", new J.Str("a")), ("t", new J.Num(5)))],
                d => IrEval.At(d, T) is J.Num ? ServiceOutcomes.Undefined : null),
        };

        public static TheoryData<string> Names => [.. Defects.Keys];

        /// <summary>
        /// Each defect S proves wrong while it is held, and nothing wrong once its fix lands: a proof whose form keeps
        /// another row than Calcite on a document, under any set asked, through both theories.
        /// </summary>
        [Theory]
        [MemberData(nameof(Names))]
        public void AHeldDefectStillAnswersWrong(string name)
        {
            var found = Wrong(Defects[name]());
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, found));
            if (Held.Contains(name))
                found.Should().NotBeEmpty($"{name} is held to still answer wrong until its fix lands, which strikes it from Held");
            else
                found.Should().BeEmpty();
        }

        /// <summary>Every row S's proven form keeps otherwise than Calcite, on every target each set serves.</summary>
        static List<string> Wrong(Defect defect)
        {
            var found = new List<string>();
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
            return found;
        }

    }

}
