using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
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
    /// S's affix form (spec §4.2 h07): <c>STARTSWITH</c>, <c>ENDSWITH</c>, <c>CONTAINS</c> and <c>LIKE</c> as one form kind,
    /// a cell cut by the pattern it matches, read by the loader over the text, a cast of a raw value, a <c>VARCHAR</c>
    /// column and a computed text place, and printed as the translator writes it. Its <c>Emulator</c> half rests on the
    /// emulator's rows; its <c>Production</c> half waits on run 1's N5-N7, so under <c>Production</c> it proves nothing.
    /// </summary>
    public sealed class RenderingAffixTests
    {

        static readonly JsonDocumentPath TPath = JsonDocumentPath.Root.Property("t"), NamePath = JsonDocumentPath.Root.Property("name"), RPath = JsonDocumentPath.Root.Property("r");

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias);

        static RexNode Name => JsonValue("name", SqlTypeName.VARCHAR);

        /// <summary>The view's raw string column, <c>r</c>, typed <c>VARCHAR</c>.</summary>
        static RexNode Column => Rex.makeInputRef(Nullable(SqlTypeName.VARCHAR), 4);

        static RexNode Like(RexNode subject, string pattern) => Rex.makeCall(SqlStdOperatorTable.LIKE, subject, Rex.makeLiteral(pattern));

        static readonly PathValues Strings = PathValues.OfKinds(ValueSet.Of(StringType));

        /// <summary>
        /// A set's view as S tabulates it, its <c>LIKE</c> units among it: Azure's under <c>Production</c> and the emulator's
        /// under <c>Emulator</c>. <c>Portable</c> reads a unit only where both read it alike, which since rows2 <c>_</c>, a
        /// lone <c>]</c> and a backslash are not, the emulator's being measured over more (<c>CosmosLikeUnit.On</c>).
        /// </summary>
        static ServiceTable On(RenderingSet set) => ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(set)), ReaderClasses,
            ProbeTheory.Theory.Get(ProbeTheory.PairTable.Of(set)), ProbeTheory.Theory.Get(ProbeTheory.LikesOf.Of(set)));

        static RenderingAnswer Generate(SqlPredicate root, RenderingSet set, PathValues values, JsonCharacterBand chars = JsonCharacterBand.None)
        {
            using var bundle = ProbeTheory.Open(set);
            var term = bundle.Write(root);
            foreach (var path in root.Places)
                bundle.Import(Source, path, values, ClassSet(chars));
            foreach (var call in bundle.ComputedCalls)
                bundle.ImportComputed(call, ClassSet(chars));

            bundle.Generate(term);
            bundle.RunGenerate(RenderingBundle.WidthOf([root])).StopReason.Should().Be(RunStopReason.Completed);
            bundle.Failure.Should().BeNull();
            return bundle.ReadBest(term);
        }

        /// <summary>
        /// An affix asks its argument's units each as itself, between runs; a <c>LIKE</c> its own, as a side's rows read each
        /// unit (G7): Calcite's <c>_</c> one code point and a backslash and a bracket themselves, and each target's bracket a
        /// range and its backslash and lone <c>]</c> themselves (rows2 and run 2). Each is written as the narrowest test that
        /// says it, one of literal units alone the equality of its text.
        /// </summary>
        [Fact]
        public void APatternIsWrittenAsTheNarrowestTest()
        {
            var (calcite, service, emulator) = (ReaderRows.Units, On(RenderingSet.Production).LikeUnits, On(RenderingSet.Emulator).LikeUnits);
            AffixPattern.Of(CosmosServiceTest.StartsWith, "ab", calcite)!.Spelled.Should().Be("ab%");
            AffixPattern.Of(CosmosServiceTest.EndsWith, "ab", calcite)!.Spelled.Should().Be("%ab");
            AffixPattern.Of(CosmosServiceTest.Contains, "", calcite)!.Spelled.Should().Be("%");
            AffixPattern.Of(CosmosServiceTest.Like, "a%%b%%", calcite)!.Spelled.Should().Be("a%b%");
            AffixPattern.Of(CosmosServiceTest.StartsWith, "a%", calcite)!.Spelled.Should().Be("a\\%%", "an affix's argument is literal");
            AffixPattern.Of(CosmosServiceTest.Like, "a\\%", calcite)!.Spelled.Should().Be("a\\\\%", "Calcite's backslash escapes nothing");
            AffixPattern.Of(CosmosServiceTest.Like, "[a]%", calcite)!.Spelled.Should().Be("[a]%", "nor is its bracket a range");
            AffixPattern.Of(CosmosServiceTest.Like, "a_b", calcite)!.HoldsOne.Should().BeTrue();
            AffixPattern.Of(CosmosServiceTest.Like, "a_b%", service)!.Spelled.Should().Be("a_b%");
            foreach (var target in new[] { service, emulator })
            {
                AffixPattern.Of(CosmosServiceTest.Like, "a]%", target)!.Spelled.Should().Be("a]%", "each target reads a lone ] as itself");
                AffixPattern.Of(CosmosServiceTest.Like, "a\\%", target).Should().NotBeNull("and a backslash as itself");
                AffixPattern.Of(CosmosServiceTest.Like, "[a]%", target).Should().BeNull("and a bracket as a range");
            }

            string? Written(string pattern) => AffixPattern.Written(LikePattern.Read(pattern, calcite)!) is var (test, argument) ? test + " " + argument : null;
            Written("ab").Should().BeNull("literal units alone are an equality");
            AffixPattern.Equality(LikePattern.Read("a\\b", calcite)!).Should().Be("a\\b");
            Written("%").Should().Be("StartsWith ");
            Written("ab%").Should().Be("StartsWith ab");
            Written("%ab").Should().Be("EndsWith ab");
            Written("%ab%").Should().Be("Contains ab");
            Written("%a\\b%").Should().Be("Contains a\\b");
            Written("%a[b%").Should().Be("Contains a[b");
            Written("a%b").Should().Be("Like a%b");
            Written("a_b%%").Should().Be("Like a_b%");
            Written("%_%").Should().Be("Like %_%");

            AffixPattern.StoredAlike("0%").Should().BeTrue("a string's first unit is never an escape's second");
            AffixPattern.StoredAlike("%0").Should().BeFalse();
            AffixPattern.StoredAlike("%a\0").Should().BeFalse();
            AffixPattern.StoredAlike("%ab%").Should().BeTrue();
            AffixPattern.StoredAlike("%a_%").Should().BeFalse("a _ may read one unit of an escape");
        }

        /// <summary>
        /// A unit is read by exactly one row of a side (G7): the one naming it, or the one spelled empty, whose row says as
        /// data which units it reads not; none reads a unit no row covers, and two rows reading one unit fail loudly, an
        /// empty row not told its side's other units among them. Each side's rows as the registry gives them read every
        /// unit once: Calcite's and each target's.
        /// </summary>
        [Fact]
        public void AUnitIsReadByExactlyOneRowOfItsSide()
        {
            var run = new LikeUnit("%", LikeUnit.Run, "", null, ValueSet<string>.Empty);
            var rest = new LikeUnit("", LikeUnit.Itself, "", null, ValueSet.Of("%"));
            LikeUnit.Of(ValueSet.Of(run, rest), "%").Should().Be(run);
            LikeUnit.Of(ValueSet.Of(run, rest), "a").Should().Be(rest);
            LikeUnit.Of(ValueSet.Of(run), "a").Should().BeNull();
            FluentActions.Invoking(() => LikeUnit.Of(ValueSet.Of(run, run with { Reads = LikeUnit.Itself }), "%")).Should().Throw<System.InvalidOperationException>();
            FluentActions.Invoking(() => LikeUnit.Of(ValueSet.Of(run, rest with { Besides = ValueSet<string>.Empty }), "%")).Should().Throw<System.InvalidOperationException>();

            foreach (var units in new[] { ReaderRows.Units, On(RenderingSet.Production).LikeUnits, On(RenderingSet.Emulator).LikeUnits })
                foreach (var unit in new[] { "%", "_", "\\", "[", "]", "a", "\u00e9", "\ud83d\ude00" })
                    LikeUnit.Of(units, unit).Should().NotBeNull(unit);
        }

        /// <summary>
        /// A side's <c>_</c> reads a code point as its row says (G7): Calcite's any but a line terminator, and each target's
        /// any of the classes it was measured matching one of, line terminators among them, and unknown over another, so a
        /// string holding one only a reading of it might match is neither matched nor missed: on both an unpaired surrogate,
        /// which neither stores as one (run 2). The emulator's reads the string as its matcher does, as it stores it, U+0000
        /// and U+FDD0 each U+FDD0 and a digit, so <c>a_b</c> misses both and <c>a__b</c> matches both (rows2, h15b), and a
        /// range's cut as written says nothing of either. A run, measured over every class, decides either way.
        /// </summary>
        [Fact]
        public void ASidesOneIsReadOverTheClassesItsRowMeasured()
        {
            var calcite = LikePattern.Read("a_b", ReaderRows.Units)!;
            var service = LikePattern.Read("a_b", On(RenderingSet.Production).LikeUnits)!;
            var emulator = LikePattern.Read("a_b", On(RenderingSet.Emulator).LikeUnits)!;
            calcite.Matches("axb").Should().BeTrue();
            calcite.Matches("a\nb").Should().BeFalse("Calcite's _ is no line terminator");
            calcite.Matches("ab").Should().BeFalse();
            service.Matches("axb").Should().BeTrue();
            service.Matches("a\u00e9b").Should().BeTrue();
            service.Matches("a\ud83d\ude00b").Should().BeTrue();
            service.Matches("a\nb").Should().BeTrue("Azure's _ matches a line terminator");
            service.Matches("a\tb").Should().BeTrue("and a character TRIM removes");
            service.Matches("a\0b").Should().BeTrue("and U+0000, as written");
            service.Matches("ab").Should().BeFalse("no reading of _ matches no code point");
            service.Matches("a\nxb").Should().BeFalse("nor two");
            LikePattern.Read("a%b", On(RenderingSet.Production).LikeUnits)!.Matches("a\nb").Should().BeTrue();
            emulator.Matches("a\nb").Should().BeTrue("the emulator's _ matches a line terminator");
            emulator.Matches("a\u3000b").Should().BeTrue();
            emulator.Matches("a\r\nb").Should().BeFalse("two code points");
            emulator.OnLike(ValueSet.Of(TrimmedClass, LineTerminatorClass)).Should().BeTrue();
            emulator.OnLike(ValueSet.Of(NulClass)).Should().BeNull("U+0000 is stored as two");
            var twice = LikePattern.Read("a__b", On(RenderingSet.Emulator).LikeUnits)!;
            foreach (var escape in new[] { "a\0b", "a\uFDD0b" })
            {
                ServiceLeaf.Matches(CosmosAnswer.StoredCodePoint, escape, emulator).Should().BeFalse("the emulator stores each escape as two code points");
                ServiceLeaf.Matches(CosmosAnswer.StoredCodePoint, escape, twice).Should().BeTrue("each _ one of the stored spelling's");
            }
            LikePattern.Read("a_b", On(RenderingSet.Portable).LikeUnits).Should().BeNull("Azure's _ and the emulator's are read apart");

            service.OnLike(ValueSet<string>.Empty).Should().BeTrue();
            service.OnLike(ValueSet.Of(NonAsciiBand)).Should().BeTrue();
            service.OnLike(ValueSet.Of(TrimmedClass, LineTerminatorClass)).Should().BeTrue();
            service.OnLike(ValueSet.Of(NulClass, EscapeClass)).Should().BeTrue();
            service.OnLike(ValueSet.Of(UnpairedClass)).Should().BeNull("no target stores an unpaired surrogate as one");
            service.OnNotLike(ValueSet.Of(SurrogateBand)).Should().BeFalse();
            service.OnNotLike(ValueSet.Of(TrimmedClass, LineTerminatorClass)).Should().BeFalse();
            calcite.OnLike(ValueSet.Of(TrimmedClass, LineTerminatorClass)).Should().BeNull("a line terminator the spelling's _ reads Calcite's misses");
            calcite.OnNotLike(ValueSet.Of(TrimmedClass, LineTerminatorClass)).Should().BeFalse("Calcite's _ matches no more than the spelling's");
        }

        /// <summary>
        /// A <c>LIKE</c> holding <c>_</c> is decided cell by cell under Azure's rows (G7): over a range its spelling cut, by
        /// the classes the range may hold, matched where they are all ones the service's <c>_</c> was measured over, line
        /// terminators among them (run 2), and either where one is not, an unpaired surrogate; and a pattern holding a unit
        /// the service's rows leave unread answers anything.
        /// </summary>
        [Fact]
        public void TheServicesOneIsDecidedOnEachCell()
        {
            var path = SqlValue.Spell(TPath);
            var production = ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(RenderingSet.Production)), ProbeRows.ReaderClasses, ValueSet<MeasuredEntry>.Empty,
                ProbeTheory.Theory.Get(ProbeTheory.LikesOf.Of(RenderingSet.Production)));
            PathCell.Strings Range(params string[] classes) => new(null, null, ValueSet<string>.Empty, ValueSet.From(classes), Like: ValueSet.Of("a_b"));
            ServiceOutcomes On(string pattern, PathCell cell) => ServiceLeaf.On(ServiceForm.AffixOf(CosmosServiceTest.Like, path, pattern), new CellPartition(path, ValueSet.Of(cell)), production, LeafReads.None).Outcomes.Single().Outcomes;

            On("a_b", Range()).Should().Be(ServiceOutcomes.True);
            On("a_b", Range(NonAsciiBand, HighBand)).Should().Be(ServiceOutcomes.True);
            On("a_b", Range(TrimmedClass, LineTerminatorClass)).Should().Be(ServiceOutcomes.True);
            On("a_b", Range(UnpairedClass)).Should().Be(ServiceOutcomes.True | ServiceOutcomes.False);
            On("a_b", new PathCell.Point(Of("a\u00e9b"))).Should().Be(ServiceOutcomes.True);
            On("a_b", new PathCell.Point(Of("a\nb"))).Should().Be(ServiceOutcomes.True);
            On("[a]_b", new PathCell.Point(Of("a\u00e9b"))).Should().Be(ServiceOutcomes.Widest);
        }

        /// <summary>
        /// The classes each target's <c>_</c> is read over are those <c>Service.LikeOneOverEachClass</c>'s witnesses hold
        /// where <c>a_b</c> kept them, no more and no fewer (G7): the emulator's from its answers (rows2) over the stored
        /// spelling its matcher reads, U+0000 and U+FDD0 each U+FDD0 and a digit, which <c>a__b</c> kept, and Azure's from the
        /// probe account's record (run 2), which kept both as written. The row says what was sent.
        /// </summary>
        [Fact]
        public void TheServicesOneIsReadOverTheClassesItsWitnessesHeld()
        {
            static ValueSet<string> Matched(IEnumerable<(string Witness, string Answer)> answers) =>
                ValueSet.From(answers.Where(p => p.Answer == "true/T").SelectMany(p => BandsOfText(System.Text.Json.JsonSerializer.Deserialize<string>(p.Witness)!).AsEnumerable()));

            var witnesses = Measurements.CosmosStringFunctionMeasurementTests.ClassWitnesses;
            var stored = witnesses.Where((_, i) => i is 11 or 12).Select(w => EmulatorStoredText.Of(System.Text.Json.JsonSerializer.Deserialize<string>(w)!)[1..^1]);
            var twice = Measurements.CosmosStringFunctionMeasurementTests.StoredEscapeAnswers.Split(' ')[1] == "true/T";
            var emulator = Matched(witnesses.Where((_, i) => i is not (11 or 12)).Zip(Measurements.CosmosStringFunctionMeasurementTests.LikeOneAnswers[0].Split(' ')))
                .Union(twice ? ValueSet.From(stored.SelectMany(s => BandsOfText(s).AsEnumerable())) : ValueSet<string>.Empty);
            var record = AzureLines("Service.LikeOneOverEachClass", "c.v LIKE " + '"' + "a_b" + '"');
            var azure = Matched(witnesses.Select(w => (w, record[w])));

            var ones = CosmosAxiomRows.ReadServiceLikeUnits(CosmosAxioms.All).Where(u => u.Unit.Unit == "_").ToList();
            ones.Select(u => u.Target).Should().BeEquivalentTo([RenderingTarget.Azure, RenderingTarget.Emulator]);
            ones.Should().ContainSingle(u => u.Target == RenderingTarget.Azure && u.Row == "Service.LikeOneOverEachClass" && u.Unit.Except.Length == 0
                && u.Unit.Over != null && u.Unit.Over.Equals(azure));
            ones.Should().ContainSingle(u => u.Target == RenderingTarget.Emulator && u.Row == "Service.LikeOneOverEachClass" && u.Unit.Except.Length == 0
                && u.Unit.Over != null && u.Unit.Over.Equals(emulator));
        }

        /// <summary>A row's answers to one expression in the probe accounts' records, by witness.</summary>
        static Dictionary<string, string> AzureLines(string row, string expression)
        {
            var assembly = typeof(RenderingAffixTests).Assembly;
            var lines = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Runs/", System.StringComparison.Ordinal)))
            {
                using var reader = new System.IO.StreamReader(assembly.GetManifestResourceStream(name)!);
                foreach (var f in reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r').Split('\t')).Where(f => f.Length == 5 && f[1] == row && f[2] == expression))
                    lines[f[3]] = f[4];
            }

            return lines;
        }

        /// <summary>
        /// The loader reads <c>LIKE</c> over the text, a cast of a raw value, a <c>VARCHAR</c> column's raw value and a
        /// computed text place, one reading for both graphs, and S owns each (h15).
        /// </summary>
        [Fact]
        public void TheLoaderReadsAnAffixOfEveryReading()
        {
            Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), "a%")).Should().Be(new SqlPredicate.Affix(PredicateAtom.LikeTest, new SqlValue.Text(TPath), "a%"));
            Loading.Read(Like(Column, "a%")).Should().Be(new SqlPredicate.Affix(PredicateAtom.LikeTest, new SqlValue.Raw(RPath) { Column = "VARCHAR" }, "a%"));

            var upper = Loading.Read(Like(Rex.makeCall(SqlStdOperatorTable.UPPER, Name), "%B%"));
            upper.Should().BeOfType<SqlPredicate.Affix>().Which.Value.Should().BeOfType<SqlValue.Call>().Which.Spelled.Should().Be("UPPER(" + SqlValue.Spell(NamePath) + ")");
            RenderingFamily.Owns(upper).Should().BeTrue("S owns every affix since the switch");
        }

        /// <summary>
        /// A bracket is a unit of a pattern like any where S reads (G8), the loader refusing none: Calcite's rows read it as
        /// itself, so a pattern an affix says is written as that affix, whose argument the service reads unit by unit as
        /// itself, and one only the service's <c>LIKE</c> says is unread there, its rows reading the bracket as a range.
        /// </summary>
        [Fact]
        public void ABracketIsReadAsItsRowsSay()
        {
            var t = JsonValue("t", SqlTypeName.VARCHAR);
            var prefix = Loading.Read(Like(t, "[a]%"));
            prefix.Should().Be(new SqlPredicate.Affix(PredicateAtom.LikeTest, new SqlValue.Text(TPath), "[a]%"));

            var answer = Generate(prefix, RenderingSet.Emulator, Strings);
            answer.Kind.Should().Be(RenderingVerdictKind.Proven, answer.Reason);
            answer.Best!.Subforms.Should().Contain(ServiceForm.AffixOf(CosmosServiceTest.StartsWith, SqlValue.Spell(TPath), "[a]"));
            Generate(Loading.Read(Like(t, "[a]_b%")), RenderingSet.Emulator, Strings).Kind.Should().Be(RenderingVerdictKind.Unproven);
        }

        /// <summary>
        /// An affix cuts a cell of strings into those its pattern matches and those it misses, each with a witness; <c>%</c>
        /// cuts nothing, and a pattern holding a unit the classes forbid leaves the missing half alone.
        /// </summary>
        [Fact]
        public void AnAffixCutsACellByWhatItMatches()
        {
            var path = SqlValue.Spell(TPath);
            ValueSet<CutPoint> Cut(string pattern) => ValueSet.Of(new CutPoint(Of(pattern), CutKind.Affix));
            var cells = PathCell.Split(path, Strings, ClassSet(JsonCharacterBand.None), Cut("a%b")).Cells;
            cells.Should().HaveCount(2);
            cells.Select(c => StringOf(c.Witness()!)).Select(w => AffixPattern.Matches(w, "a%b")).Should().BeEquivalentTo([true, false]);
            cells.Should().OnlyContain(c => c.Contains(c.Witness()));

            PathCell.Split(path, Strings, ClassSet(JsonCharacterBand.None), Cut("%")).Cells.Should().ContainSingle().Which.Should().BeOfType<PathCell.Strings>().Which.Affixed.Should().BeFalse();
            PathCell.Split(path, Strings, ClassSet(JsonCharacterBand.None), Cut("%é%")).Cells.Should().ContainSingle()
                .Which.Should().BeOfType<PathCell.Strings>().Which.NotLike.Should().Equal(ValueSet.Of("%é%"));

            // Two patterns: every combination a witness lies in.
            var two = PathCell.Split(path, Strings, ClassSet(JsonCharacterBand.None), Cut("a%").Union(Cut("%b"))).Cells;
            two.Should().HaveCount(4);
            two.Should().OnlyContain(c => c.Contains(c.Witness()));

            // A literal and an affix: the point stands beside the cut intervals.
            var mixed = PathCell.Split(path, Strings, ClassSet(JsonCharacterBand.None), Cut("a%").Union(ValueSet.Of(new CutPoint(Of("m"), CutKind.Literal)))).Cells;
            mixed.Should().Contain(new PathCell.Point(Of("m")));
            mixed.Should().OnlyContain(c => c.Contains(c.Witness()));
        }

        /// <summary>
        /// The leaf reads a string by the matcher the test's entry names under <c>Emulator</c>, a stored escape in the stored
        /// spelling, and anything else by its kind; <c>LIKE</c>'s there is its affixes', each <c>_</c> one code point of that
        /// spelling (h15b).
        /// </summary>
        [Fact]
        public void TheLeafReadsTheMatcherItsEntryNames()
        {
            var path = SqlValue.Spell(TPath);
            var emulator = ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(RenderingSet.Emulator)), ProbeRows.ReaderClasses, ValueSet<MeasuredEntry>.Empty,
                ProbeTheory.Theory.Get(ProbeTheory.LikesOf.Of(RenderingSet.Emulator)));
            var production = ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(RenderingSet.Production)), ProbeRows.ReaderClasses, ValueSet<MeasuredEntry>.Empty,
                ProbeTheory.Theory.Get(ProbeTheory.LikesOf.Of(RenderingSet.Production)));
            var cells = new CellPartition(path, ValueSet.Of<PathCell>(new PathCell.Point(Of("ab")), new PathCell.Point(Of("ba")), new PathCell.Point(Of("a\0")), new PathCell.Point(Of(1m)), new PathCell.Null(), new PathCell.Absent()));
            // Each cell's outcome in the order written above, whatever order the partition keeps, under what S's rules hand the leaf.
            ServiceOutcomes[] On(ServiceForm leaf, ServiceTable table, LeafReads? reads = null) => ServiceLeaf.On(leaf, cells, table, reads ?? LeafReads.None) is var vector
                ? [.. new PathCell[] { new PathCell.Point(Of("ab")), new PathCell.Point(Of("ba")), new PathCell.Point(Of("a\0")), new PathCell.Point(Of(1m)), new PathCell.Null(), new PathCell.Absent() }
                    .Select(cell => vector.Outcomes[cells.Cells.ToList().IndexOf(cell)].Outcomes)]
                : [];

            On(ServiceForm.AffixOf(CosmosServiceTest.StartsWith, path, "a"), emulator).Should().Equal(
                ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.True, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
            // The stored spelling splits this pattern, so S hands the leaf what it rewrites as uncut, which a range reads and a
            // point, matched as it is stored, does not (Service.EmulatorComparesTheSpellingItIsSent, G1).
            On(ServiceForm.AffixOf(CosmosServiceTest.EndsWith, path, "\0"), emulator, LeafReads.Matches(ValueSet<string>.Empty, ValueSet.Of(NulClass, EscapeClass), ValueSet<string>.Empty)).Should().Equal(
                ServiceOutcomes.False, ServiceOutcomes.False, ServiceOutcomes.True, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);

            On(ServiceForm.AffixOf(CosmosServiceTest.Like, path, "a%b"), emulator).Should().Equal(
                ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.False, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
            On(ServiceForm.AffixOf(CosmosServiceTest.Like, path, "a_"), emulator).Should().Equal(
                ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.False, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
            On(ServiceForm.AffixOf(CosmosServiceTest.Like, path, "a__"), emulator).Should().Equal(
                ServiceOutcomes.False, ServiceOutcomes.False, ServiceOutcomes.True, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
            ServiceLeaf.On(ServiceForm.AffixOf(CosmosServiceTest.Like, path, "a%b"), cells, emulator, LeafReads.None).Rows.Should().Contain("Service.LikeMatchesByCodePoint")
                .And.NotContain("missing:Like/String/None");

            // Azure's by code point, read past U+0000 by STARTSWITH and to it by CONTAINS (run 1): the truncates row S reads hands
            // a CONTAINS U+0000 unread (Service.NulEndsWhatReplaceAndContainsRead, G1), and the leaf names no band itself.
            On(ServiceForm.AffixOf(CosmosServiceTest.StartsWith, path, "a"), production).Should().Equal(
                ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.True, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
            On(ServiceForm.AffixOf(CosmosServiceTest.Contains, path, "a"), production, LeafReads.Matches(ValueSet.Of(NulClass), ValueSet<string>.Empty, ValueSet<string>.Empty)).Should().Equal(
                ServiceOutcomes.True, ServiceOutcomes.True, ServiceOutcomes.True | ServiceOutcomes.False, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
            On(ServiceForm.AffixOf(CosmosServiceTest.Contains, path, "a"), production)[2].Should().Be(ServiceOutcomes.True, "unhanded the read, the point is matched as it is");
        }

        /// <summary>
        /// Over a path that holds strings every affix the translator writes for <c>LIKE</c> is Proven under <c>Emulator</c>,
        /// its form the affix that says the pattern, and so is its negation. Under <c>Production</c> each is too, but a
        /// <c>CONTAINS</c> over strings that may hold U+0000, which Azure's reads only up to. A pattern of two runs and one
        /// holding <c>_</c> are <c>LIKE</c>'s own form under <c>Emulator</c> since h15b, which read its matcher as its
        /// affixes', but the one holding <c>_</c> over strings that may hold U+0000, which the emulator stores as two code
        /// points, or a line terminator, which Calcite's <c>_</c> misses.
        /// </summary>
        [Fact]
        public void AnAffixOverStringsIsProvenUnderEmulator()
        {
            var t = SqlValue.Spell(TPath);
            var proven = new (string Pattern, ServiceForm Form)[]
            {
                ("a%", ServiceForm.AffixOf(CosmosServiceTest.StartsWith, t, "a")),
                ("%b", ServiceForm.AffixOf(CosmosServiceTest.EndsWith, t, "b")),
                ("%x%", ServiceForm.AffixOf(CosmosServiceTest.Contains, t, "x")),
                ("0%", ServiceForm.AffixOf(CosmosServiceTest.StartsWith, t, "0")),
            };

            foreach (var (pattern, form) in proven)
            {
                var read = Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), pattern));
                foreach (var shape in new[] { read, new SqlPredicate.Not(read) })
                {
                    var answer = Generate(shape, RenderingSet.Emulator, Strings);
                    answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{shape}: {answer.Reason}");
                    answer.Best!.Subforms.Should().Contain(form);
                    var azure = Generate(shape, RenderingSet.Production, Strings);
                    azure.Kind.Should().Be(RenderingVerdictKind.Proven, $"{shape}: {azure.Reason}");
                    azure.Best!.Subforms.Should().Contain(form);
                    Generate(shape, RenderingSet.Production, Strings, JsonCharacterBand.Nul).Kind.Should().Be(form.Test == CosmosServiceTest.Contains ? RenderingVerdictKind.Unproven : RenderingVerdictKind.Proven, shape.ToString());
                }
            }

            foreach (var pattern in new[] { "a%b", "a_b" })
            {
                var answer = Generate(Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), pattern)), RenderingSet.Emulator, Strings);
                answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{pattern}: {answer.Reason}");
                answer.Best!.Subforms.Should().Contain(ServiceForm.AffixOf(CosmosServiceTest.Like, t, pattern));
            }

            Generate(Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), "a%b")), RenderingSet.Emulator, Strings, JsonCharacterBand.Nul).Kind.Should().Be(RenderingVerdictKind.Proven);
            Generate(Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), "a_b")), RenderingSet.Emulator, Strings, JsonCharacterBand.Nul).Kind.Should().Be(RenderingVerdictKind.Unproven, "U+0000 is two code points stored");
            Generate(Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), "a_b")), RenderingSet.Emulator, Strings, JsonCharacterBand.LineTerminator).Kind.Should().Be(RenderingVerdictKind.Unproven, "Calcite's _ misses a line terminator");
        }

        /// <summary>
        /// Over a path that may hold anything a pattern holding a unit no number renders in and that neither <c>true</c> nor
        /// <c>false</c> matches is Proven, its negation by the affix where the path holds neither; one a number's rendering
        /// may match is not, the service reading a number as no string.
        /// </summary>
        [Fact]
        public void APatternNoRenderingMatchesIsProvenOverAnything()
        {
            var read = Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), "%b"));
            foreach (var shape in new[] { read, new SqlPredicate.Not(read) })
                Generate(shape, RenderingSet.Emulator, PathValues.Anything).Kind.Should().Be(RenderingVerdictKind.Proven, shape.ToString());

            Generate(Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), "%0")), RenderingSet.Emulator, PathValues.Anything).Kind.Should().Be(RenderingVerdictKind.Unproven);
        }

        /// <summary>Over a <c>VARCHAR</c> column holding strings, and over a computed text place of ASCII strings, an affix is Proven under <c>Emulator</c>, and over the place under <c>Production</c>.</summary>
        [Fact]
        public void AnAffixOverAColumnAndAPlaceIsProvenUnderEmulator()
        {
            var column = Generate(Loading.Read(Like(Column, "B%")), RenderingSet.Emulator, Strings);
            column.Kind.Should().Be(RenderingVerdictKind.Proven, column.Reason);
            column.Best!.Subforms.Should().Contain(ServiceForm.AffixOf(CosmosServiceTest.StartsWith, SqlValue.Spell(RPath), "B"));

            var read = Loading.Read(Like(Rex.makeCall(SqlStdOperatorTable.UPPER, Name), "%B%"));
            var place = ((SqlValue.Call)((SqlPredicate.Affix)read).Value).Spelled!;
            var upper = Generate(read, RenderingSet.Emulator, Strings);
            upper.Kind.Should().Be(RenderingVerdictKind.Proven, upper.Reason);
            upper.Best!.Subforms.Should().Contain(ServiceForm.AffixOf(CosmosServiceTest.Contains, place, "B"));
            Generate(read, RenderingSet.Production, Strings).Best!.Subforms.Should().Contain(ServiceForm.AffixOf(CosmosServiceTest.Contains, place, "B"));
        }

        /// <summary>
        /// The printer writes an affix as the translator does, its argument a parameter, in the stored spelling where the
        /// target stores one, and the check reader reads it back as the form.
        /// </summary>
        [Fact]
        public void AnAffixPrintsAndReadsBack()
        {
            var t = SqlValue.Spell(TPath);
            var paths = new Dictionary<string, JsonDocumentPath> { [t] = TPath };
            foreach (var (form, text) in new[]
            {
                (ServiceForm.AffixOf(CosmosServiceTest.StartsWith, t, "a"), "STARTSWITH(c.t, @p0)"),
                (ServiceForm.AffixOf(CosmosServiceTest.EndsWith, t, "b"), "ENDSWITH(c.t, @p0)"),
                (ServiceForm.AffixOf(CosmosServiceTest.Contains, t, "x"), "CONTAINS(c.t, @p0)"),
                (ServiceForm.AffixOf(CosmosServiceTest.Like, t, "a%b"), "(c.t LIKE @p0)"),
            })
            {
                var parameters = new CosmosParameterList();
                var printed = RenderingPrinter.Print(form, paths, "c", parameters);
                printed.Should().Be(text);
                CosmosSqlFormReader.Read(printed, parameters.Parameters).Form.Should().Be(form);
            }

            var stored = new CosmosParameterList();
            RenderingPrinter.Print(ServiceForm.AffixOf(CosmosServiceTest.EndsWith, t, "\0"), paths, "c", stored, stored: true);
            System.Convert.ToString(stored.Parameters.Single().Value).Should().Be(EmulatorStoredText.Of("\0"));
        }

    }

}
