using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingRexBuilderTests;
using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// A comparison of two readings (h11): the loader reads one outside the legacy reading, S reads it as an atom of the
    /// two places' pair, whose import is the product of theirs parted by order, and proposes the comparison of the two
    /// paths, which the printer writes with a path on each side. What the service answers of two paths is the literal
    /// comparison's rows, mirrored where only that way round is measured (an assumption Azure confirms at h14b).
    /// </summary>
    public sealed class RenderingPairTests
    {

        static readonly JsonDocumentPath NamePath = JsonDocumentPath.Root.Property("name"), OtherPath = JsonDocumentPath.Root.Property("other");

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias, places: true);

        static readonly CosmosPredicateLoader.Loading Legacy = new(Fields, Readings, CosmosImplementor.DefaultRootAlias, legacy: true);

        static readonly PathValues Strings = PathValues.OfKinds(ValueSet.Of(StringType));

        static readonly ValueSet<string> Ascii = ClassSet(JsonCharacterBand.None);

        static readonly string[] Comparisons = [PredicateAtom.Equal, PredicateAtom.NotEqual, PredicateAtom.Less, PredicateAtom.LessOrEqual, PredicateAtom.Greater, PredicateAtom.GreaterOrEqual];

        static SqlPredicate Pair(string op, JsonDocumentPath left, JsonDocumentPath right) => new SqlPredicate.Compare(op, new SqlValue.Text(left), new SqlValue.Text(right));

        static string Spelled(JsonDocumentPath path) => SqlValue.Spell(path);

        /// <summary>
        /// What S generates for a predicate under a set, each path imported as these values and characters; the rows the run
        /// left, and the most cells a partition holds.
        /// </summary>
        static (RenderingAnswer Answer, RenderingRun Run, long Rows, int Cells) Generate(SqlPredicate root, RenderingSet set, PathValues values, ValueSet<string> chars)
        {
            using var bundle = ProbeTheory.Open(set);
            var term = bundle.Write(root);
            foreach (var path in root.Places)
                bundle.Import(Source, path, values, chars);

            bundle.Generate(term);
            var run = bundle.RunGenerate(RenderingBundle.WidthOf([root]));
            bundle.Failure.Should().BeNull();
            var cells = bundle.Scope.Rows(ProbeTheory.Partition).Select(row => ((CellPartition)row.Output!).Cells.Count).DefaultIfEmpty().Max();
            return (bundle.ReadBest(term), run, bundle.Rows, cells);
        }

        /// <summary>
        /// Two readings of one kind compared are read as such outside the legacy reading; the legacy reading S reads until
        /// h15 keeps them opaque, and two raw readings are no pair. A pair's atom is assumed by the rule, and S owns none.
        /// </summary>
        [Fact]
        public void TheLoaderReadsTwoReadingsOutsideTheLegacyReading()
        {
            var rex = Rex.makeCall(SqlStdOperatorTable.LESS_THAN, JsonValue("name", SqlTypeName.VARCHAR), JsonValue("other", SqlTypeName.VARCHAR));
            Loading.Read(rex).Should().Be(Pair(PredicateAtom.Less, NamePath, OtherPath));
            Loading.Read(rex).Places.Should().Equal(NamePath, OtherPath);
            Legacy.Read(rex).Should().BeOfType<SqlPredicate.Opaque>();
            Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, JsonValue("name", SqlTypeName.ANY), JsonValue("other", SqlTypeName.ANY))).Should().BeOfType<SqlPredicate.Opaque>();
            RenderingBundle.IsComputed(Pair(PredicateAtom.Equal, NamePath, OtherPath)).Should().BeTrue("a pair's atom is assumed by the rule");
            RenderingFamily.Owns(Pair(PredicateAtom.Equal, NamePath, OtherPath)).Should().BeFalse("S owns no pair until the switch");
        }

        /// <summary>
        /// Text equal to text over ASCII strings is Proven under the emulator, whose rows say two paths compare as a path
        /// and a literal (<c>Service.TwoPathsCompareAsAPathAndALiteral</c>), as the two paths' comparison, which the printer
        /// writes with no literal bound; under <c>Production</c>, Azure having measured no two paths until run 1, it is
        /// not. The pair's atom is assumed by the rule, as the product, and its spelling is both places'.
        /// </summary>
        [Fact]
        public void TextEqualToTextIsProvenAsTheTwoPathsComparison()
        {
            var equal = Pair(PredicateAtom.Equal, NamePath, OtherPath);
            var form = ServiceForm.ComparePathsOf(CosmosServiceTest.Equal, Spelled(NamePath), Spelled(OtherPath));
            var (answer, run, rows, _) = Generate(equal, RenderingSet.Emulator, Strings, Ascii);
            (answer.Kind, run.StopReason).Should().Be((RenderingVerdictKind.Proven, RunStopReason.Completed), answer.Reason);
            answer.Best.Should().Be(form);
            rows.Should().BeLessThan(RenderingBundle.RowLimit);
            Generate(equal, RenderingSet.Production, Strings, Ascii).Answer.Kind.Should().Be(RenderingVerdictKind.Unproven, "no Azure row measures two paths");

            var parameters = new CosmosParameterList();
            RenderingPrinter.Print(form, equal.Places.ToDictionary(Spelled), "c", parameters).Should().Be("(c.name = c.other)");
            RenderingPrinter.Print(ServiceForm.NotOf(ServiceForm.ComparePathsOf(CosmosServiceTest.Less, Spelled(NamePath), Spelled(OtherPath))), equal.Places.ToDictionary(Spelled), "c", parameters)
                .Should().Be("(NOT (c.name < c.other))");
            parameters.Count.Should().Be(0, "no literal is bound");

            using var bundle = ProbeTheory.Open(RenderingSet.Emulator);
            var term = bundle.Write(equal);
            foreach (var path in equal.Places)
                bundle.Import(Source, path, Strings, Ascii);
            bundle.Generate(term);
            bundle.RunGenerate(RenderingBundle.WidthOf([equal])).StopReason.Should().Be(RunStopReason.Completed);
            bundle.Scope.TryGet(ProbeTheory.Assumed.Of(term), out PathValues assumed).Should().BeTrue();
            assumed.Pair.Should().NotBeNull();
            var pair = ProbeTheory.Paired.Of(JsonFactEncoding.Path(NamePath).ToTerm(ProbeTheory.Paths), JsonFactEncoding.Path(OtherPath).ToTerm(ProbeTheory.Paths));
            bundle.Scope.Get(ProbeTheory.Spelling.Of(pair)).Should().Be(PlaceSpellings.Of(Spelled(NamePath), Spelled(OtherPath)));
        }

        /// <summary>
        /// An ordering of ASCII strings is Proven, both orders agreeing below U+D800; over every band it is not, where Calcite's
        /// order by UTF-16 unit and the service's by code point part a surrogate pair from a character above U+DFFF. The
        /// rows each run leaves are recorded against <see cref="RenderingBundle.RowLimit"/> (line 39).
        /// </summary>
        [Fact]
        public void AnOrderingIsProvenOverAsciiAndNotAcrossThePartingBands()
        {
            foreach (var op in new[] { PredicateAtom.Less, PredicateAtom.GreaterOrEqual })
            {
                var ordered = Pair(op, NamePath, OtherPath);
                var (answer, _, rows, cells) = Generate(ordered, RenderingSet.Emulator, Strings, Ascii);
                answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{op}: {answer.Reason}");
                answer.Best!.Kind.Should().Be(ServiceFormKind.ComparePaths);
                rows.Should().BeLessThan(RenderingBundle.RowLimit);

                var (across, run, most, product) = Generate(ordered, RenderingSet.Emulator, Strings, AllBands);
                (across.Kind, run.StopReason).Should().Be((RenderingVerdictKind.Unproven, RunStopReason.Completed), op);
                most.Should().BeLessThan(RenderingBundle.RowLimit);
                TestContext.Current.TestOutputHelper?.WriteLine($"{op}: rows a bundle {rows} over ASCII ({cells} cells), {most} over every band ({product} cells), of {RenderingBundle.RowLimit}");
            }
        }

        /// <summary>
        /// Two numbers, and two paths that may hold anything, are not proven: S parts no pair of numbers by order, and a value
        /// of one kind against another's is not the literal's row.
        /// </summary>
        [Fact]
        public void TwoNumbersAndTwoAnythingsAreNotProven()
        {
            var numbers = new SqlPredicate.Compare(PredicateAtom.Less, new SqlValue.Typed(NamePath, "INTEGER"), new SqlValue.Typed(OtherPath, "INTEGER"));
            Generate(numbers, RenderingSet.Emulator, PathValues.OfKinds(ValueSet.Of(IntegerType)), Ascii).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);
            Generate(Pair(PredicateAtom.Equal, NamePath, OtherPath), RenderingSet.Emulator, PathValues.Anything, AllBands).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// A path against itself is the diagonal: <c>name &lt; name</c> is FALSE wherever it is a string and <c>name = name</c>
        /// TRUE, the service's equal, so both are Proven under the emulator over ASCII strings; over the bands its stored
        /// order reads apart (U+0000, the high band) the order row leaves anything, which the emulator's rule keeps.
        /// </summary>
        [Fact]
        public void APathAgainstItselfIsTheDiagonal()
        {
            foreach (var op in new[] { PredicateAtom.Less, PredicateAtom.Equal })
            {
                var (answer, _, _, _) = Generate(Pair(op, NamePath, NamePath), RenderingSet.Emulator, Strings, Ascii);
                answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{op}: {answer.Reason}");
            }
        }

        /// <summary>
        /// What S proves of a pair under the emulator, each Proven form judged on documents by the two oracles: Calcite's
        /// truth on each is TRUE exactly where the service keeps it. Over ASCII every comparison is Proven; over every band,
        /// whatever is Proven is held to strings holding U+0000, a surrogate pair, a character above it and one between.
        /// Under <c>Production</c> nothing is, until Azure measures two paths.
        /// </summary>
        [Fact]
        public void EveryProvenPairKeepsWhatCalciteKeeps()
        {
            string[] ascii = ["", "a", "ab", "b", "B", "~", "a b"], wide = [.. ascii, "\0", "a\0", "é", "😀", "～", "￿", "a😀"];
            foreach (var op in Comparisons)
            {
                var ir = Pair(op, NamePath, OtherPath);
                var (answer, _, _, _) = Generate(ir, RenderingSet.Emulator, Strings, Ascii);
                answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{op}: {answer.Reason}");
                Agrees(ir, answer.Best!, RenderingSet.Emulator, ascii);

                if (Generate(ir, RenderingSet.Emulator, Strings, AllBands).Answer is { Kind: RenderingVerdictKind.Proven, Best: { } best })
                    Agrees(ir, best, RenderingSet.Emulator, wide);
                Generate(ir, RenderingSet.Production, Strings, AllBands).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, op);
            }

            static void Agrees(SqlPredicate ir, ServiceForm form, RenderingSet set, string[] values)
            {
                var target = set == RenderingSet.Emulator ? RenderingTarget.Emulator : RenderingTarget.Azure;
                foreach (var left in values)
                    foreach (var right in values)
                    {
                        var document = new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal) { ["name"] = new J.Str(left), ["other"] = new J.Str(right) });
                        var kept = IrEval.Truth(ir, document, new World(0)) == Truths.True;
                        (CosmosEval.On(form, document, target) == ServiceOutcomes.True).Should().Be(kept, $"{form} under {set} on {Json(left)}, {Json(right)}");
                    }
            }

            static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
        }

    }

}
