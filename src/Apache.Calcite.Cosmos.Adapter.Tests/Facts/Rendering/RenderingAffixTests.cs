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

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias, places: true);

        static readonly CosmosPredicateLoader.Loading Legacy = new(Fields, Readings, CosmosImplementor.DefaultRootAlias, legacy: true);

        static RexNode Name => JsonValue("name", SqlTypeName.VARCHAR);

        /// <summary>The view's raw string column, <c>r</c>, typed <c>VARCHAR</c>.</summary>
        static RexNode Column => Rex.makeInputRef(Nullable(SqlTypeName.VARCHAR), 4);

        static RexNode Like(RexNode subject, string pattern) => Rex.makeCall(SqlStdOperatorTable.LIKE, subject, Rex.makeLiteral(pattern));

        static readonly PathValues Strings = PathValues.OfKinds(ValueSet.Of(StringType));

        static RenderingAnswer Generate(SqlPredicate root, RenderingSet set, PathValues values, JsonCharacterBand chars = JsonCharacterBand.None)
        {
            using var bundle = SwitchedTheory.Open(set);
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

        /// <summary>A pattern is runs and <c>%</c>; one holding <c>_</c>, a backslash, a bracket or a lone surrogate is none, and each is written as the narrowest test that says it.</summary>
        [Fact]
        public void APatternIsWrittenAsTheNarrowestTest()
        {
            AffixPattern.Of(CosmosServiceTest.StartsWith, "ab").Should().Be("ab%");
            AffixPattern.Of(CosmosServiceTest.EndsWith, "ab").Should().Be("%ab");
            AffixPattern.Of(CosmosServiceTest.Contains, "").Should().Be("%");
            AffixPattern.Of(CosmosServiceTest.Like, "a%%b%%").Should().Be("a%b%");
            AffixPattern.Of(CosmosServiceTest.StartsWith, "a%").Should().BeNull();
            foreach (var refused in new[] { "a_b", "a\\%", "[a]%", "\ud800%" })
                AffixPattern.Canonical(refused).Should().BeNull(refused);

            AffixPattern.Written("ab").Should().BeNull("no % is an equality");
            AffixPattern.Written("%").Should().Be((CosmosServiceTest.StartsWith, ""));
            AffixPattern.Written("ab%").Should().Be((CosmosServiceTest.StartsWith, "ab"));
            AffixPattern.Written("%ab").Should().Be((CosmosServiceTest.EndsWith, "ab"));
            AffixPattern.Written("%ab%").Should().Be((CosmosServiceTest.Contains, "ab"));
            AffixPattern.Written("a%b").Should().Be((CosmosServiceTest.Like, "a%b"));

            AffixPattern.StoredAlike("0%").Should().BeTrue("a string's first unit is never an escape's second");
            AffixPattern.StoredAlike("%0").Should().BeFalse();
            AffixPattern.StoredAlike("%a\0").Should().BeFalse();
            AffixPattern.StoredAlike("%ab%").Should().BeTrue();
        }

        /// <summary>
        /// The loader reads <c>LIKE</c> over the text and a cast of a raw value as it always has, and with places over a
        /// <c>VARCHAR</c> column's raw value and a computed text place, which the legacy reading S reads until h15 never does.
        /// </summary>
        [Fact]
        public void TheLoaderReadsAnAffixOfEveryReading()
        {
            Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), "a%")).Should().Be(new SqlPredicate.Affix(PredicateAtom.LikeTest, new SqlValue.Text(TPath), "a%"));
            Loading.Read(Like(Column, "a%")).Should().Be(new SqlPredicate.Affix(PredicateAtom.LikeTest, new SqlValue.Raw(RPath) { Column = "VARCHAR" }, "a%"));
            Legacy.Read(Like(Column, "a%")).Should().BeOfType<SqlPredicate.Opaque>();

            var upper = Loading.Read(Like(Rex.makeCall(SqlStdOperatorTable.UPPER, Name), "%B%"));
            upper.Should().BeOfType<SqlPredicate.Affix>().Which.Value.Should().BeOfType<SqlValue.Call>().Which.Spelled.Should().Be("UPPER(" + SqlValue.Spell(NamePath) + ")");
            Legacy.Read(Like(Rex.makeCall(SqlStdOperatorTable.UPPER, Name), "%B%")).Should().BeOfType<SqlPredicate.Opaque>();
            RenderingFamily.Owns(upper).Should().BeFalse("S owns no affix until the switch");
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
        /// spelling, and anything else by its kind; <c>LIKE</c>'s matcher is owed there, and Azure has measured none.
        /// </summary>
        [Fact]
        public void TheLeafReadsTheMatcherItsEntryNames()
        {
            var path = SqlValue.Spell(TPath);
            var emulator = ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(RenderingSet.Emulator)));
            var production = ServiceTable.Of(ProbeTheory.Theory.Get(ProbeTheory.Table.Of(RenderingSet.Production)));
            var cells = new CellPartition(path, ValueSet.Of<PathCell>(new PathCell.Point(Of("ab")), new PathCell.Point(Of("ba")), new PathCell.Point(Of("a\0")), new PathCell.Point(Of(1m)), new PathCell.Null(), new PathCell.Absent()));
            // Each cell's outcome in the order written above, whatever order the partition keeps.
            ServiceOutcomes[] On(ServiceForm leaf, ServiceTable table) => ServiceLeaf.On(leaf, cells, table, ValueSet<string>.Empty) is var vector
                ? [.. new PathCell[] { new PathCell.Point(Of("ab")), new PathCell.Point(Of("ba")), new PathCell.Point(Of("a\0")), new PathCell.Point(Of(1m)), new PathCell.Null(), new PathCell.Absent() }
                    .Select(cell => vector.Outcomes[cells.Cells.ToList().IndexOf(cell)].Outcomes)]
                : [];

            On(ServiceForm.AffixOf(CosmosServiceTest.StartsWith, path, "a"), emulator).Should().Equal(
                ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.True, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
            On(ServiceForm.AffixOf(CosmosServiceTest.EndsWith, path, "\0"), emulator).Should().Equal(
                ServiceOutcomes.False, ServiceOutcomes.False, ServiceOutcomes.True, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);

            On(ServiceForm.AffixOf(CosmosServiceTest.Like, path, "a%b"), emulator)[0].Should().Be(ServiceOutcomes.Widest);
            ServiceLeaf.On(ServiceForm.AffixOf(CosmosServiceTest.Like, path, "a%b"), cells, emulator, ValueSet<string>.Empty).Rows.Should().Contain("missing:Like/String/None");

            // Azure's by code point, read past U+0000 by STARTSWITH and to it by CONTAINS (run 1).
            On(ServiceForm.AffixOf(CosmosServiceTest.StartsWith, path, "a"), production).Should().Equal(
                ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.True, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
            On(ServiceForm.AffixOf(CosmosServiceTest.Contains, path, "a"), production).Should().Equal(
                ServiceOutcomes.True, ServiceOutcomes.True, ServiceOutcomes.True | ServiceOutcomes.False, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined, ServiceOutcomes.Undefined);
        }

        /// <summary>
        /// Over a path that holds strings every affix the translator writes for <c>LIKE</c> is Proven under <c>Emulator</c>,
        /// its form the affix that says the pattern, and so is its negation; a pattern of two runs, a <c>LIKE</c> the emulator's
        /// unmeasured matcher reads, and a <c>_</c>, which no row measures, are not. Under <c>Production</c> each is too, but a
        /// <c>CONTAINS</c> over strings that may hold U+0000, which Azure's reads only up to.
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
                Generate(Loading.Read(Like(JsonValue("t", SqlTypeName.VARCHAR), pattern)), RenderingSet.Emulator, Strings).Kind.Should().Be(RenderingVerdictKind.Unproven, pattern);
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
