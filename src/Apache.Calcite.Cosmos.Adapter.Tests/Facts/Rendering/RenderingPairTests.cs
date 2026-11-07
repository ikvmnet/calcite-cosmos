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
        static (RenderingAnswer Answer, RenderingRun Run, long Rows, int Cells) Generate(SqlPredicate root, RenderingSet set, PathValues values, ValueSet<string> chars, RenderingTheory? theory = null)
        {
            var defined = theory ?? ProbeTheory;
            using var bundle = defined.Open(set);
            var term = bundle.Write(root);
            foreach (var path in root.Places)
                bundle.Import(Source, path, values, chars);

            bundle.Generate(term);
            var run = bundle.RunGenerate(RenderingBundle.WidthOf([root]));
            bundle.Failure.Should().BeNull();
            var cells = bundle.Scope.Rows(defined.Partition).Select(row => ((CellPartition)row.Output!).Cells.Count).DefaultIfEmpty().Max();
            return (bundle.ReadBest(term), run, bundle.Rows, cells);
        }

        /// <summary>The atoms S found of a predicate under the emulator, each path imported as these values over ASCII.</summary>
        static int Atoms(SqlPredicate root, PathValues values)
        {
            using var bundle = ProbeTheory.Open(RenderingSet.Emulator);
            var term = bundle.Write(root);
            foreach (var path in root.Places)
                bundle.Import(Source, path, values, Ascii);

            bundle.Generate(term);
            bundle.RunGenerate(RenderingBundle.WidthOf([root]));
            bundle.Failure.Should().BeNull();
            return bundle.Scope.Rows(ProbeTheory.Ir.Atom).Count();
        }

        /// <summary>
        /// Two readings compared are read as such outside the legacy reading; the legacy reading S reads until h15 keeps them
        /// opaque. Whether two readings are a pair is S's, two raw readings among them (<see cref="APairIsReadByItsReadingsKindsAndOrders"/>).
        /// A pair's atom is assumed by the rule, and S owns none.
        /// </summary>
        [Fact]
        public void TheLoaderReadsTwoReadingsOutsideTheLegacyReading()
        {
            var rex = Rex.makeCall(SqlStdOperatorTable.LESS_THAN, JsonValue("name", SqlTypeName.VARCHAR), JsonValue("other", SqlTypeName.VARCHAR));
            Loading.Read(rex).Should().Be(Pair(PredicateAtom.Less, NamePath, OtherPath));
            Loading.Read(rex).Places.Should().Equal(NamePath, OtherPath);
            Legacy.Read(rex).Should().BeOfType<SqlPredicate.Opaque>();
            Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, JsonValue("name", SqlTypeName.ANY), JsonValue("other", SqlTypeName.ANY))).Should().BeOfType<SqlPredicate.Compare>()
                .Which.Places.Should().Equal(NamePath, OtherPath);
            RenderingBundle.IsComputed(Pair(PredicateAtom.Equal, NamePath, OtherPath)).Should().BeTrue("a pair's atom is assumed by the rule");
            RenderingFamily.Owns(Pair(PredicateAtom.Equal, NamePath, OtherPath)).Should().BeFalse("S owns no pair until the switch");
        }

        /// <summary>
        /// Text equal to text over ASCII strings is Proven under the emulator, whose rows say two paths compare as a path
        /// and a literal (<c>Service.TwoPathsCompareAsAPathAndALiteral</c>), as the two paths' comparison, which the printer
        /// writes with no literal bound; and under <c>Production</c>, the probe account having measured the row (run 1). The
        /// pair's atom is assumed by the rule, as the product, and its spelling is both places'.
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
            Generate(equal, RenderingSet.Production, Strings, Ascii).Answer.Best.Should().Be(form, "the probe account measured two paths in run 1");

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
        /// order by UTF-16 unit and the service's by code point part a surrogate pair from a character above U+DFFF. Over the
        /// high band without U+0000 it is Proven on both targets, the emulator's stored spelling keeping code-point order there,
        /// and over U+0000 the emulator's is not. The
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

                // The high band without U+0000 (planted_free's U+FDD0): the emulator's stored spelling keeps code-point order.
                var high = ClassSet(JsonCharacterBand.NonAscii | JsonCharacterBand.HighBmp);
                foreach (var set in new[] { RenderingSet.Emulator, RenderingSet.Production })
                    Generate(ordered, set, Strings, high).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{op} over the high band under {set}");
                Generate(ordered, RenderingSet.Emulator, Strings, ClassSet(JsonCharacterBand.Nul)).Answer.Kind.Should().Be(RenderingVerdictKind.Unproven, $"{op}: U+0000 stored above U+0001");
                TestContext.Current.TestOutputHelper?.WriteLine($"{op}: rows a bundle {rows} over ASCII ({cells} cells), {most} over every band ({product} cells), of {RenderingBundle.RowLimit}");
            }
        }

        /// <summary>
        /// What makes two readings a pair is S's (G3): each reading's kind, its type's family in Calcite, and what its order
        /// holds. A raw reading has no kind and a number read against text is two kinds, so S makes no atom of either and
        /// neither is proven; a text place against another is an atom of their pair. A cast of the text to <c>DOUBLE</c>
        /// reads <c>'NaN'</c> as a NaN, which compares as nothing alike, itself included (<c>Calcite.ANaNComparesAsNothingAlike</c>),
        /// so not even its diagonal is placed, and over strings no comparison of it with itself is proven where the text's
        /// own is; were its order to hold equal values read alike equal, the diagonal would be proven, and wrong on
        /// <c>'NaN'</c>.
        /// </summary>
        [Fact]
        public void APairIsReadByItsReadingsKindsAndOrders()
        {
            var raw = new SqlPredicate.Compare(PredicateAtom.Equal, new SqlValue.Raw(NamePath), new SqlValue.Raw(OtherPath));
            var across = new SqlPredicate.Compare(PredicateAtom.Less, new SqlValue.Typed(NamePath, "INTEGER"), new SqlValue.Text(OtherPath));
            foreach (var none in new[] { raw, across })
            {
                Atoms(none, PathValues.Anything).Should().Be(0, $"{none} is no pair");
                Generate(none, RenderingSet.Emulator, PathValues.Anything, Ascii).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, none.ToString());
            }

            Atoms(Pair(PredicateAtom.Less, NamePath, OtherPath), Strings).Should().Be(1);

            var cast = new SqlValue.Cast("DOUBLE", new SqlValue.Text(NamePath));
            var reflexive = RenderingTheory.Define(Measured with { ReaderOrders = [.. Measured.ReaderOrders, (PredicateAtom.CastReader("DOUBLE", PredicateAtom.TextReader), PredicateAtom.Reflexive)] });
            foreach (var op in new[] { PredicateAtom.Equal, PredicateAtom.LessOrEqual })
            {
                var diagonal = new SqlPredicate.Compare(op, cast, cast);
                Atoms(diagonal, Strings).Should().Be(1);
                Generate(diagonal, RenderingSet.Emulator, Strings, Ascii).Answer.Kind.Should().Be(RenderingVerdictKind.Unproven, $"{op}: 'NaN' read as a double is not itself");
                Generate(diagonal, RenderingSet.Emulator, Strings, Ascii, reflexive).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{op}, the order planted reflexive");
                Generate(Pair(op, NamePath, NamePath), RenderingSet.Emulator, Strings, Ascii).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, op);
            }
        }

        /// <summary>
        /// A pair's key reads the union of what each way of reading it the set settled answers (G3): the left against a
        /// literal of the right's value, the right against the left's with the comparator mirrored, and a side absent. The
        /// union is what the row choice was, the direct row where measured and the mirrored otherwise, since wherever a set
        /// settled both ways round the shipped rows answer alike; were one to answer otherwise, the pair would read both and
        /// prove less, never more.
        /// </summary>
        [Fact]
        public void BothWaysRoundAnswerAlikeWhereverASetSettledThem()
        {
            var kinds = Enum.GetValues<CosmosValueClass>();
            var both = 0;
            foreach (var set in new[] { RenderingSet.Emulator, RenderingSet.Production, RenderingSet.Portable })
                foreach (var (test, _, reads) in Measured.PairReadings.Where(r => r.Swapped == false))
                    foreach (var (_, _, mirrored) in Measured.PairReadings.Where(r => r.Swapped && r.Test == test))
                        foreach (var left in kinds)
                            foreach (var right in kinds)
                                if (ProbeTheory.Theory.TryGet(ProbeTheory.Final.Of(MeasuredKey.Of(reads, right, left), set), out ValueSet<CosmosAnswer> direct)
                                    && ProbeTheory.Theory.TryGet(ProbeTheory.Final.Of(MeasuredKey.Of(mirrored, left, right), set), out ValueSet<CosmosAnswer> other))
                                {
                                    both++;
                                    direct.AsEnumerable().Should().Equal(other.AsEnumerable(), $"{test} of {left} and {right} under {set}");
                                }

            both.Should().BeGreaterThan(0);
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
        /// order reads apart (U+0000) the order row leaves anything, which the emulator's rule keeps.
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
        /// A side absent is undefined and a right side null reads as the literal null (the two-path row's own outcomes, on
        /// both targets). Over strings that may be absent every comparison of two paths is Proven under both sets; over
        /// strings that may be null, two nulls are equal to the service and unknown to Calcite, so = is not proved there and
        /// &lt; is; a typed integer against itself is the diagonal, null or absent alike. Each Proven form keeps on
        /// documents exactly what Calcite keeps.
        /// </summary>
        [Fact]
        public void APairReadsAnAbsentSideAndANullFromTheRow()
        {
            var absent = PathValues.OfKinds(ValueSet.Of(StringType, PathValues.AbsentKind));
            var nullable = PathValues.OfKinds(ValueSet.Of(StringType, NullType, PathValues.AbsentKind));
            J?[] values = [null, new J.Null(), new J.Str(""), new J.Str("a"), new J.Str("b")];
            foreach (var set in new[] { RenderingSet.Emulator, RenderingSet.Production })
            {
                foreach (var op in Comparisons)
                {
                    var ir = Pair(op, NamePath, OtherPath);
                    var (answer, run, _, _) = Generate(ir, set, absent, Ascii);
                    (answer.Kind, run.StopReason).Should().Be((RenderingVerdictKind.Proven, RunStopReason.Completed), $"{op} under {set}: {answer.Reason}");
                    Agrees(ir, answer.Best!, set, values.Where(v => v is not J.Null));
                    if (Generate(ir, set, nullable, Ascii).Answer is { Kind: RenderingVerdictKind.Proven, Best: { } best })
                        Agrees(ir, best, set, values);
                }

                Generate(Pair(PredicateAtom.Equal, NamePath, OtherPath), set, nullable, Ascii).Answer.Kind.Should().Be(RenderingVerdictKind.Unproven, "two nulls are equal at the service alone");
                Generate(Pair(PredicateAtom.Less, NamePath, OtherPath), set, nullable, Ascii).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, "two nulls are less at neither");
                var diagonal = new SqlPredicate.Compare(PredicateAtom.Less, new SqlValue.Typed(NamePath, "INTEGER"), new SqlValue.Typed(NamePath, "INTEGER"));
                Generate(diagonal, set, PathValues.OfKinds(ValueSet.Of(IntegerType, NullType, PathValues.AbsentKind)), Ascii).Answer.Kind
                    .Should().Be(RenderingVerdictKind.Proven, $"an integer against itself under {set}");
            }

            static void Agrees(SqlPredicate ir, ServiceForm form, RenderingSet set, IEnumerable<J?> values)
            {
                var target = set == RenderingSet.Emulator ? RenderingTarget.Emulator : RenderingTarget.Azure;
                foreach (var left in values)
                    foreach (var right in values)
                    {
                        var fields = new SortedDictionary<string, J>(StringComparer.Ordinal);
                        if (left is not null)
                            fields["name"] = left;
                        if (right is not null)
                            fields["other"] = right;
                        var kept = IrEval.Truth(ir, new J.Obj(fields), new World(0)) == Truths.True;
                        (CosmosEval.On(form, new J.Obj(fields), target) == ServiceOutcomes.True).Should().Be(kept, $"{form} under {set} on {left?.ToString() ?? "absent"}, {right?.ToString() ?? "absent"}");
                    }
            }
        }

        /// <summary>
        /// What S proves of a pair under the emulator, each Proven form judged on documents by the two oracles: Calcite's
        /// truth on each is TRUE exactly where the service keeps it. Over ASCII every comparison is Proven; over every band,
        /// whatever is Proven is held to strings holding U+0000, a surrogate pair, a character above it and one between.
        /// Under <c>Production</c> whatever is Proven over every band is held to the same strings on Azure's model.
        /// </summary>
        [Fact]
        public void EveryProvenPairKeepsWhatCalciteKeeps()
        {
            string[] ascii = ["", "a", "ab", "b", "B", "~", "a b"], wide = [.. ascii, "\0", "a\0", "é", "😀", "～", "￿", "a😀"];
            string[] high = [.. ascii, "é", "～", "￿", "﷐", "a﷐", "﷐0", "﷐1", "﷐~", "﷏", "﷑"];
            foreach (var op in Comparisons)
            {
                var ir = Pair(op, NamePath, OtherPath);
                var (answer, _, _, _) = Generate(ir, RenderingSet.Emulator, Strings, Ascii);
                answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{op}: {answer.Reason}");
                Agrees(ir, answer.Best!, RenderingSet.Emulator, ascii);

                var (stored, _, _, _) = Generate(ir, RenderingSet.Emulator, Strings, ClassSet(JsonCharacterBand.NonAscii | JsonCharacterBand.HighBmp));
                stored.Kind.Should().Be(RenderingVerdictKind.Proven, $"{op} over the high band: {stored.Reason}");
                Agrees(ir, stored.Best!, RenderingSet.Emulator, high);

                if (Generate(ir, RenderingSet.Emulator, Strings, AllBands).Answer is { Kind: RenderingVerdictKind.Proven, Best: { } best })
                    Agrees(ir, best, RenderingSet.Emulator, wide);
                if (Generate(ir, RenderingSet.Production, Strings, AllBands).Answer is { Kind: RenderingVerdictKind.Proven, Best: { } azure })
                    Agrees(ir, azure, RenderingSet.Production, wide);
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
