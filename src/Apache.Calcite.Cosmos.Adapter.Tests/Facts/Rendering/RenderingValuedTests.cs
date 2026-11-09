using System;
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
    /// A computed value as a place (h10): the loader types a call the function rows name, S reads it as a path of its own
    /// whose import is the call's image once every key its operands fall under agrees on every target the set serves, and
    /// the printer and the Rex builder write a form over it that the loader reads back. Under <c>Production</c>, Azure
    /// having no function rows until run 1, no place has an image.
    /// </summary>
    public sealed class RenderingValuedTests
    {

        static readonly JsonDocumentPath NamePath = JsonDocumentPath.Root.Property("name"), NPath = JsonDocumentPath.Root.Property("n");

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias);

        static RexNode Name => JsonValue("name", SqlTypeName.VARCHAR);

        static RexNode Lit(string value) => Rex.makeLiteral(value);

        static RexNode Int(int value) => Rex.makeExactLiteral(new java.math.BigDecimal(value));

        /// <summary>What S generates for a read predicate under a set, each path imported as these values and characters, each place's characters these.</summary>
        static (RenderingAnswer Answer, RenderingRun Run) Generate(SqlPredicate root, RenderingSet set, PathValues values, JsonCharacterBand chars, RenderingTheory? theory = null)
        {
            theory ??= ProbeTheory;
            using var bundle = theory.Open(set);
            var term = bundle.Write(root);
            foreach (var path in root.Places)
                bundle.Import(Source, path, values, ClassSet(chars));
            foreach (var call in bundle.ComputedCalls)
                bundle.ImportComputed(call, ClassSet(chars));

            bundle.Generate(term);
            var run = bundle.RunGenerate(RenderingBundle.WidthOf([root]));
            bundle.Failure.Should().BeNull();
            return (bundle.ReadBest(term), run);
        }

        static readonly PathValues Strings = PathValues.OfKinds(ValueSet.Of(StringType));

        static SqlValue.Call PlaceOf(SqlPredicate read) => read.Computed.Last();

        /// <summary>The loader types a call the rows name and spells its place.</summary>
        [Fact]
        public void TheLoaderTypesACallTheRowsName()
        {
            var lower = Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc"));
            var read = Loading.Read(lower);
            PlaceOf(read).Type.Should().Be("VARCHAR");
            PlaceOf(read).Spelled.Should().Be("LOWER(" + SqlValue.Spell(NamePath) + ")");
            read.Places.Should().Equal(NamePath);
            read.Paths.Should().BeEmpty("F reads the paths a predicate compares, a call's operands no part of them");
            Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlLibraryOperators.SOUNDEX, Name), Lit("abc"))).Computed.Should().BeEmpty("no row names SOUNDEX");
        }

        /// <summary>
        /// S chooses a window's spelling by its start (G6's <c>callspelling</c> rules): from its start where it is a literal
        /// whole number of one or more, and clipped as Calcite clips it anywhere else, a start below one or one read off a
        /// path, whose place is written though no row images it. A function with one spelling has no choice.
        /// </summary>
        [Fact]
        public void SChoosesAWindowsSpellingByItsStart()
        {
            long? Chosen(RexNode start, RexNode? subject = null)
            {
                var read = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.SUBSTRING, subject ?? Name, start, Int(2)), Lit("ab")));
                read.Computed.Should().ContainSingle();
                return ChosenSpellings.Of(read, RenderingSet.Production)(PlaceOf(read));
            }

            Chosen(Int(1)).Should().Be(CosmosFunctionSpellings.WindowFromItsStart);
            Chosen(Int(7)).Should().Be(CosmosFunctionSpellings.WindowFromItsStart);
            Chosen(Int(0)).Should().Be(CosmosFunctionSpellings.WindowClipped);
            Chosen(Int(-3)).Should().Be(CosmosFunctionSpellings.WindowClipped);
            Chosen(JsonValue("n", SqlTypeName.INTEGER)).Should().Be(CosmosFunctionSpellings.WindowClipped, "a start read off a path is no literal");
            var lower = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")));
            ChosenSpellings.Of(lower, RenderingSet.Production)(PlaceOf(lower)).Should().BeNull("LOWER has one spelling");
        }

        /// <summary>
        /// S keys a call by the name its rows give it at its type, an arithmetic operator's with its type, and by none where
        /// they name none (G6's <c>callkey</c> rules), which the tests that read the rows apart from S read alike
        /// (<see cref="RowNames"/>).
        /// </summary>
        [Fact]
        public void SKeysACallByTheNameItsRowsGiveIt()
        {
            string? KeyOf(RexNode call, RexNode than)
            {
                var read = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, call, than));
                using var bundle = ProbeTheory.Open(RenderingSet.Emulator);
                var term = bundle.Write(read);
                foreach (var path in read.Places)
                    bundle.Import(Source, path, PathValues.Anything, JsonValues.AllBands);
                foreach (var computed in bundle.ComputedCalls)
                    bundle.ImportComputed(computed, JsonValues.AllBands);
                bundle.Generate(term);
                bundle.RunGenerate(RenderingBundle.WidthOf([read])).StopReason.Should().Be(RunStopReason.Completed);
                var place = PlaceOf(read);
                bundle.KeyOf(place).Should().Be(RowNames.KeyOf(ProbeRows.Measured, place.Function, place.Type));
                return bundle.KeyOf(place);
            }

            var n = JsonValue("n", SqlTypeName.INTEGER);
            KeyOf(Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")).Should().Be("LOWER");
            KeyOf(Rex.makeCall(SqlStdOperatorTable.PLUS, n, Int(1)), Int(2)).Should().Be("+:INTEGER");
            KeyOf(Rex.makeCall(SqlStdOperatorTable.ABS, n), Int(2)).Should().BeNull("no row names ABS");
        }

        /// <summary>
        /// <c>LOWER(name) = 'abc'</c> over ASCII strings is Proven under the emulator, whose rows say LOWER agrees there; the
        /// form is the comparison over the place, printed as the service's call and built as the Rex the loader reads back.
        /// </summary>
        [Fact]
        public void ACaseMappingOverAsciiRoundTrips()
        {
            var rex = Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc"));
            var read = Loading.Read(rex);
            var place = PlaceOf(read);
            var (answer, run) = Generate(read, RenderingSet.Emulator, Strings, JsonCharacterBand.None);
            (answer.Kind, run.StopReason).Should().Be((RenderingVerdictKind.Proven, RunStopReason.Completed));
            answer.Best.Should().Be(ServiceForm.CompareOf(CosmosServiceTest.Equal, place.Spelled!, Of("abc")));

            var parameters = new CosmosParameterList();
            RenderingPrinter.Print(answer.Best!, read.Places.ToDictionary(SqlValue.Spell), "c", parameters, computed: new Dictionary<string, SqlValue.Call> { [place.Spelled!] = place })
                .Should().Be("(LOWER(c.name) = @p0)");

            var built = RenderingRexBuilder.Build(answer.Best!, Rex, Loading.AccessorsOf(rex, Rex));
            built.Should().NotBeNull();
            Loading.Read(built!).Should().Be(read, "what is sent is what Calcite reads");
        }

        /// <summary>
        /// A window with a literal start of one or more round trips too, printed from its start as S chose (<c>callspelling</c>,
        /// G6), and a nested call is gathered once its inner place is written.
        /// </summary>
        [Fact]
        public void AWindowAndANestedCallRoundTrip()
        {
            var window = Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.SUBSTRING, Name, Int(2), Int(3)), Lit("abc"));
            var nested = Rex.makeCall(SqlStdOperatorTable.EQUALS,
                Rex.makeCall(SqlStdOperatorTable.LOWER, Rex.makeCall(SqlStdOperatorTable.TRIM, Rex.makeFlag(SqlTrimFunction.Flag.BOTH), Lit(" "), Name)), Lit("abc"));
            foreach (var rex in new[] { window, nested })
            {
                var read = Loading.Read(rex);
                var (answer, _) = Generate(read, RenderingSet.Emulator, Strings, JsonCharacterBand.None);
                answer.Kind.Should().Be(RenderingVerdictKind.Proven, read.ToString());
                var built = RenderingRexBuilder.Build(answer.Best!, Rex, Loading.AccessorsOf(rex, Rex)) ?? throw new InvalidOperationException($"{answer.Best} was not built");
                Loading.Read(built).Should().Be(read);
            }

            var printed = Loading.Read(window) is var w && PlaceOf(w) is var place
                ? RenderingPrinter.Print(ServiceForm.CompareOf(CosmosServiceTest.Equal, place.Spelled!, Of("abc")), w.Places.ToDictionary(SqlValue.Spell), "c", new CosmosParameterList(),
                    computed: new Dictionary<string, SqlValue.Call> { [place.Spelled!] = place }, spellings: ChosenSpellings.Of(w, RenderingSet.Emulator)) : null;
            printed.Should().Be("(SUBSTRING(c.name, (@p0 - 1), @p1) = @p2)");
        }

        /// <summary>
        /// S images a call <see cref="RenderingTheory.PlaceDepth"/> deep and no deeper (G6): a conjunct over one nested past
        /// that is unproven alone, the run completing and a conjunct beside it proven, where a run spent on the depth would
        /// leave every answer in the bundle incomplete; the probe of check 2 of g6 at depths 1 to 32.
        /// </summary>
        [Fact]
        public void ACallNestedPastSsDepthLeavesItsOwnConjunctUnprovenAlone()
        {
            var plain = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, JsonValue("cat", SqlTypeName.VARCHAR), Lit("bikes")));
            foreach (var depth in new[] { 1, RenderingTheory.PlaceDepth, RenderingTheory.PlaceDepth + 1, 12, 32 })
            {
                var nested = Name;
                for (var i = 0; i < depth; i++)
                    nested = Rex.makeCall(i % 2 == 0 ? SqlStdOperatorTable.UPPER : SqlStdOperatorTable.LOWER, nested);

                var deep = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, nested, Lit("X")));
                foreach (var set in new[] { RenderingSet.Emulator, RenderingSet.Production })
                {
                    using var bundle = ProbeTheory.Open(set);
                    var terms = new[] { plain, deep }.Select(r => bundle.Write(r)).ToList();
                    foreach (var path in new[] { plain, deep }.SelectMany(r => r.Places).Distinct())
                        bundle.Import(Source, path, Strings, NoBand);
                    foreach (var call in bundle.ComputedCalls)
                        bundle.ImportComputed(call, NoBand);
                    foreach (var term in terms)
                        bundle.Generate(term);

                    var run = bundle.RunGenerate(RenderingBundle.WidthOf([plain, deep]));
                    var because = $"depth {depth} under {set}";
                    run.StopReason.Should().Be(RunStopReason.Completed, because);
                    bundle.ReadBest(terms[0]).Kind.Should().Be(RenderingVerdictKind.Proven, because);
                    bundle.ReadBest(terms[1]).Kind.Should().Be(depth <= RenderingTheory.PlaceDepth ? RenderingVerdictKind.Proven : RenderingVerdictKind.Unproven, because);
                }
            }
        }

        /// <summary>
        /// S writes a <c>LIKE</c> over a fold as the case-insensitive affix its rows say matches alike over the fold's subject
        /// (G6's <c>foldedas</c>): the pattern's narrowest affix, of an argument the fold leaves as it is, where nothing the
        /// facts hold of the subject parts; and none for an argument the fold changes or beyond ASCII, a subject that may hold
        /// a character beyond it, a pattern no affix says, a call that folds nothing, or rows that say nothing of the match.
        /// </summary>
        [Fact]
        public void SWritesALikeOverAFoldAsTheCaseInsensitiveAffixItsRowsMatchAlike()
        {
            (CosmosServiceTest, string, long)? Folded(RexNode subject, string pattern, RenderingSet set, JsonCharacterBand chars = JsonCharacterBand.None, RenderingTheory? theory = null)
            {
                var like = Loading.Read(Rex.makeCall(SqlStdOperatorTable.LIKE, subject, Lit(pattern))).Should().BeOfType<SqlPredicate.Affix>().Subject;
                using var bundle = (theory ?? ProbeTheory).Open(set);
                var term = bundle.Write(like);
                foreach (var path in like.Places)
                    bundle.Import(Source, path, Strings, ClassSet(chars));
                foreach (var call in bundle.ComputedCalls)
                    bundle.ImportComputed(call, ClassSet(chars));

                bundle.RunBridge().StopReason.Should().Be(RunStopReason.Completed);
                return bundle.FoldOf(term);
            }

            var upper = Rex.makeCall(SqlStdOperatorTable.UPPER, Name);
            foreach (var set in new[] { RenderingSet.Production, RenderingSet.Emulator, RenderingSet.Portable })
            {
                Folded(upper, "%B%", set).Should().Be((CosmosServiceTest.Contains, "B", 0L), set.ToString());
                Folded(Rex.makeCall(SqlStdOperatorTable.LOWER, Name), "a%", set).Should().Be((CosmosServiceTest.StartsWith, "a", 0L));
                Folded(upper, "%ABC", set).Should().Be((CosmosServiceTest.EndsWith, "ABC", 0L));
                Folded(upper, "%%B%", set).Should().Be((CosmosServiceTest.Contains, "B", 0L), "a run of % is one");
                Folded(upper, "%b%", set).Should().BeNull("UPPER leaves no b");
                Folded(upper, "%" + (char)0xC9 + "%", set).Should().BeNull("Calcite's fold of an argument beyond ASCII is Java's");
                Folded(upper, "%B%", set, JsonCharacterBand.NonAscii).Should().BeNull("Java maps U+00DF to SS, and the service's match leaves it");
                Folded(upper, "%A_B%", set).Should().BeNull("_ is one code point, no affix's");
                Folded(upper, "%A%B%", set).Should().BeNull("two runs are a LIKE");
                Folded(upper, "AB", set).Should().BeNull("a pattern of no % is an equality");
                Folded(Rex.makeCall(SqlLibraryOperators.REVERSE, Name), "%B%", set).Should().BeNull("REVERSE folds nothing");
            }

            Folded(upper, "%B%", RenderingSet.Production, theory: RenderingTheory.Define(Measured with { Folds = [] })).Should().BeNull("no row says how the match folds");
        }

        /// <summary>
        /// No image where a key may disagree: over a band LOWER parts over, on the emulator and on Azure alike; over a path that may hold a number, which the service's LOWER leaves undefined; and over integer
        /// arithmetic, whose emulator rows differ or fail over numbers, so the 44 arithmetic statements are listed.
        /// </summary>
        [Fact]
        public void APlaceWhoseKeysMayDisagreeHasNoImage()
        {
            var lower = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")));
            Generate(lower, RenderingSet.Production, Strings, JsonCharacterBand.NonAscii).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, "Azure's LOWER parts over a character outside ASCII");
            Generate(lower, RenderingSet.Portable, Strings, JsonCharacterBand.NonAscii).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);
            Generate(lower, RenderingSet.Emulator, Strings, JsonCharacterBand.NonAscii).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);
            Generate(lower, RenderingSet.Emulator, PathValues.OfKinds(ValueSet.Of(StringType, IntegerType)), JsonCharacterBand.None).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);

            var sum = Loading.Read(Rex.makeCall(SqlStdOperatorTable.GREATER_THAN, Rex.makeCall(SqlStdOperatorTable.PLUS, JsonValue("n", SqlTypeName.INTEGER), Int(1)), Int(5)));
            PlaceOf(sum).Type.Should().Be("INTEGER");
            Generate(sum, RenderingSet.Emulator, PathValues.OfKinds(ValueSet.Of(IntegerType)), JsonCharacterBand.None).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// S's image of a call, what the translator's column bridge asks of each call it writes (G6): one whose keys agree and
        /// all yield a value is its type's kinds, with absent beside them where its operand may be absent, Calcite's case
        /// mapping being null over an absent path; one whose keys may disagree, Azure's case mapping beyond ASCII, has none.
        /// </summary>
        [Fact]
        public void TheImageIsItsTypesKindsAndAbsentWhereTheRowsSaySo()
        {
            var lower = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")));
            PathValues? Image(RenderingSet set, PathValues values, JsonCharacterBand chars)
            {
                using var bundle = ProbeTheory.Open(set);
                bundle.Write(lower);
                foreach (var path in lower.Places)
                    bundle.Import(Source, path, values, ClassSet(chars));
                foreach (var call in bundle.ComputedCalls)
                    bundle.ImportComputed(call, ClassSet(chars));

                bundle.Run().StopReason.Should().Be(RunStopReason.Completed);
                bundle.Failure.Should().BeNull();
                return bundle.ImageOf(PlaceOf(lower));
            }

            Image(RenderingSet.Emulator, Strings, JsonCharacterBand.None).Should().Be(Strings);
            Image(RenderingSet.Emulator, PathValues.OfKinds(ValueSet.Of(StringType, PathValues.AbsentKind)), JsonCharacterBand.None)
                .Should().Be(PathValues.OfKinds(ValueSet.Of(StringType, PathValues.AbsentKind)), "Calcite's LOWER is null over an absent path");
            Image(RenderingSet.Production, Strings, JsonCharacterBand.NonAscii).Should().BeNull("Azure's LOWER parts over a character outside ASCII");
        }

        /// <summary>
        /// A typed call reads its place whatever the definition holds, but a definition that holds no row for it gives the
        /// place no import: the image rule reads the measured keys, so what the registry proves (<see
        /// cref="ACaseMappingOverAsciiRoundTrips"/>) is not proven.
        /// </summary>
        [Fact]
        public void APlaceNoRowOfTheDefinitionNamesHasNoImport()
        {
            var lower = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")));
            var bare = RenderingTheory.Define(Measured with { Functions = [] });
            GC.KeepAlive(bare);
            Generate(lower, RenderingSet.Emulator, Strings, JsonCharacterBand.None, bare).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);
            bare.Graph.Rulesets.Should().NotContain(r => r.Name == "valued", "the bundle writes a typed call's reading");
        }

        /// <summary>
        /// A definition whose rows name no operand of a function they were measured over gives its place no image: the image
        /// rule joins the rows' subjects. The translator's bridge refuses the call alike
        /// (<see cref="Sql.CosmosFunctionRowsGateTests.AFunctionWhoseRowsNameNoSubjectIsRefused"/>).
        /// </summary>
        [Fact]
        public void APlaceWhoseRowsNameNoSubjectHasNoImage()
        {
            var lower = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")));
            var bare = RenderingTheory.Define(Measured with { FunctionSubjects = [.. Measured.FunctionSubjects.Where(f => f.Function != "LOWER")] });
            Generate(lower, RenderingSet.Emulator, Strings, JsonCharacterBand.None, bare).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);
            GC.KeepAlive(bare);
        }

        /// <summary>
        /// The shapes of the three plans the translator's bridge pushes over the node their conjunct is rendered beneath,
        /// under Production: differential 477, <c>UPPER(s) = 'ABC'</c> beneath <c>s IN ('aBc', 'axb')</c>; partitions 240,
        /// <c>UPPER(category) IS NULL</c> beneath <c>category IN ('true', '1.0E30')</c>; and partitions 2763,
        /// <c>UPPER(category) &gt; 'bikes'</c> beneath <c>category IS NULL</c>. Imported as that node holds the path, the
        /// place has its image and the conjunct is proven; imported as a string of any class, as the statement's root may
        /// hold it, 477's place has none. Since G6 S's admission states a later round's computed atom beneath that round's node
        /// (<see cref="ALaterRoundsComputedAtomIsReadBeneathItsRound"/>), and the bridge asks S's image over the node its
        /// conjunct is rendered beneath.
        /// </summary>
        [Fact]
        public void AnImportNarrowedWhereItsConjunctIsRenderedGivesThePlaceItsImage()
        {
            RexNode Upper() => Rex.makeCall(SqlStdOperatorTable.UPPER, Name);
            var upper = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Upper(), Lit("ABC")));
            var narrowed = PathValues.Meet(Strings, PathValues.OneOf(ValueSet.Of(Of("aBc"), Of("axb"))));

            Generate(upper, RenderingSet.Production, narrowed, JsonCharacterBand.None).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, "477");
            Generate(upper, RenderingSet.Production, Strings, JsonCharacterBand.Any).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven);

            var unknown = Loading.Read(Rex.makeCall(SqlStdOperatorTable.IS_NULL, Upper()));
            var listed = PathValues.Meet(Strings, PathValues.OneOf(ValueSet.Of(Of("true"), Of("1.0E30"))));
            Generate(unknown, RenderingSet.Production, listed, JsonCharacterBand.None).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, "240");

            var above = Loading.Read(Rex.makeCall(SqlStdOperatorTable.GREATER_THAN, Upper(), Lit("bikes")));
            var missing = PathValues.OfKinds(ValueSet.Of(NullType, PathValues.AbsentKind));
            Generate(above, RenderingSet.Production, missing, JsonCharacterBand.None).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, "2763");
        }

        /// <summary>
        /// The rows' classes of value are S's to read (F2): rows of a key split by classes cover the atoms those hold and no
        /// more, and a row covering an atom that disagrees keeps the place from its image whatever row of every value agrees.
        /// </summary>
        [Fact]
        public void ARowCoversTheAtomsItsClassHolds()
        {
            var lower = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")));
            static bool Subject(FunctionKey k) => k.Function == "LOWER" && k.Kind == CosmosValueClass.String && k.Band == JsonCharacterBand.None;
            static RenderingTheory Split(params FunctionValueClass[] classes) => RenderingTheory.Define(Measured with
            {
                Functions = [.. Measured.Functions.Where(f => Subject(f.Key) == false), .. from f in Measured.Functions where Subject(f.Key) from c in classes select (f.Key with { Value = c }, f.Target, f.Answer)],
            });
            var (whole, part) = (Split(FunctionValueClass.Empty, FunctionValueClass.NonEmpty), Split(FunctionValueClass.NonEmpty));
            var differing = RenderingTheory.Define(Measured with
            {
                Functions = [.. Measured.Functions, .. from f in Measured.Functions where Subject(f.Key) select (f.Key with { Value = FunctionValueClass.Empty }, f.Target, CosmosFunctionAnswer.Differs)],
            });

            Generate(lower, RenderingSet.Emulator, Strings, JsonCharacterBand.None, whole).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, "the two classes hold both atoms of a string");
            Generate(lower, RenderingSet.Emulator, Strings, JsonCharacterBand.None, part).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, "no row covers the empty string");
            Generate(lower, RenderingSet.Emulator, Strings, JsonCharacterBand.None, differing).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, "the empty string's row disagrees");
            GC.KeepAlive((whole, part, differing));
        }

        /// <summary>
        /// A row of another operand keys that operand by the kind it holds (F2): a count a number's row names is keyed as the
        /// number it is, so where that row agrees the place has its image and where it disagrees none, whatever the
        /// subject's rows say. The translator's bridge reads the same rows alike
        /// (<see cref="Sql.CosmosFunctionRowsGateTests.TheBridgeReadsEveryRowSsImageRuleReads"/>).
        /// </summary>
        [Fact]
        public void ANumberRowOfAnotherOperandKeysItByItsKind()
        {
            var left = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlLibraryOperators.LEFT, Name, Int(2)), Lit("ab")));
            static RenderingTheory Counted(CosmosFunctionAnswer answer) => RenderingTheory.Define(Measured with
            {
                Functions = [.. Measured.Functions, .. from target in new[] { RenderingTarget.Emulator, RenderingTarget.Azure } select (FunctionKey.Of("LEFT", 1, CosmosValueClass.Number, JsonCharacterBand.None), target, answer)],
                FunctionSubjects = [.. Measured.FunctionSubjects, ("LEFT", 1L)],
                FunctionYields = [.. Measured.FunctionYields, (FunctionKey.Of("LEFT", 1, CosmosValueClass.Number, JsonCharacterBand.None), CalciteYield.Value)],
            });
            var (agrees, differs) = (Counted(CosmosFunctionAnswer.Same), Counted(CosmosFunctionAnswer.Differs));

            Generate(left, RenderingSet.Emulator, Strings, JsonCharacterBand.None).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, "the registry's rows name the subject alone");
            Generate(left, RenderingSet.Emulator, Strings, JsonCharacterBand.None, agrees).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, "the count's row agrees");
            Generate(left, RenderingSet.Emulator, Strings, JsonCharacterBand.None, differs).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, "the count's row disagrees");
            GC.KeepAlive((agrees, differs));
        }

        /// <summary>
        /// A cell holding a number held approximately is answered, never faulted: it is keyed by every atom of a number
        /// (<see cref="RenderingImageTests.ACellHoldingAnApproximateNumberIsKeyedByEveryNumberAtom"/>), so where a row of one
        /// atom disagrees no image is written, though the number is negative and a negative cell's rows agree.
        /// </summary>
        [Fact]
        public void ACellHoldingAnApproximateNumberIsAnswered()
        {
            var lower = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")));
            static bool Subject(FunctionKey k) => k.Function == "LOWER" && k.Kind == CosmosValueClass.Number;
            var split = RenderingTheory.Define(Measured with
            {
                Functions =
                [
                    .. Measured.Functions.Where(f => Subject(f.Key) == false),
                    .. from f in Measured.Functions
                       where Subject(f.Key)
                       from row in new[] { (FunctionValueClass.BelowOne, CosmosFunctionAnswer.Same), (FunctionValueClass.AtLeastOne, CosmosFunctionAnswer.Differs) }
                       select (f.Key with { Value = row.Item1 }, f.Target, row.Item2),
                ],
            });
            static PathValues Cell(string number) => PathValues.Meet(PathValues.OneOf(ValueSet.Of(Of("ABC"), number)), PathValues.OfKinds(ValueSet.Of(StringType, TypeOf(number))));

            Generate(lower, RenderingSet.Emulator, Cell(Of(-7m)), JsonCharacterBand.None, split).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, "a negative number's row agrees");
            Generate(lower, RenderingSet.Emulator, Cell(Of(-1e30)), JsonCharacterBand.None, split).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, "a number held approximately is in every atom, from 1 among them");
            Generate(lower, RenderingSet.Emulator, Cell(Of(1e30)), JsonCharacterBand.None).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, "the service's LOWER is undefined over a number");
            GC.KeepAlive(split);
        }

        /// <summary>
        /// The census's string-function pushes under the emulator (h05, <c>coverage-J1-where.tsv</c>), each over a path
        /// imported an ASCII string: proven as the place's comparison, a window starting before 1 among them, spelled as the
        /// service's window from 0 (differential 488); but none over a path that may hold a number, and no window of a
        /// negative length, which Calcite raises over: its place is written, and its length keyed below 0, where the
        /// emulator's rows part and Azure's are unmeasured.
        /// </summary>
        [Fact]
        public void TheCensusStringPushesAreProvenOverAsciiStrings()
        {
            static RexNode Path(string name) => JsonValue(name, SqlTypeName.VARCHAR);
            var proven = new (string Name, RexNode Rex)[]
            {
                ("UPPER", Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.UPPER, Path("s")), Lit("ABC"))),
                ("TRIM", Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.TRIM, Rex.makeFlag(SqlTrimFunction.Flag.BOTH), Lit(" "), Path("w")), Lit("a b"))),
                ("REVERSE", Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlLibraryOperators.REVERSE, Path("t")), Lit("ba"))),
                ("CHAR_LENGTH", Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.CHAR_LENGTH, Path("t")), Int(1))),
                ("POSITION", Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.POSITION, Lit("b"), Path("t")), Int(2))),
                ("IS NULL", Rex.makeCall(SqlStdOperatorTable.IS_NULL, Rex.makeCall(SqlStdOperatorTable.UPPER, Path("category")))),
                ("SUBSTRING from 0", Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.SUBSTRING, Path("a"), Int(0), Int(2)), Lit("a"))),
            };
            foreach (var (name, rex) in proven)
            {
                var read = Loading.Read(rex);
                read.Computed.Should().ContainSingle(name);
                Generate(read, RenderingSet.Emulator, Strings, JsonCharacterBand.None).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, name);
                Generate(read, RenderingSet.Emulator, PathValues.OfKinds(ValueSet.Of(StringType, IntegerType)), JsonCharacterBand.None).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, name);
                Generate(read, RenderingSet.Production, Strings, JsonCharacterBand.None).Answer.Kind.Should().Be(RenderingVerdictKind.Proven, $"{name}, Azure's rows being run 1's");
            }

            var from0 = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.SUBSTRING, Path("a"), Int(0), Int(2)), Lit("a")));
            var window = PlaceOf(from0);
            RenderingPrinter.Print(ServiceForm.CompareOf(CosmosServiceTest.Equal, window.Spelled!, Of("a")), from0.Places.ToDictionary(SqlValue.Spell), "c", new CosmosParameterList(),
                computed: new Dictionary<string, SqlValue.Call> { [window.Spelled!] = window }, spellings: ChosenSpellings.Of(from0, RenderingSet.Emulator))
                .Should().Be("(SUBSTRING(c.a, (@p0 < 1 ? 0 : (@p0 - 1)), (@p0 < 1 ? (((@p0 + @p1) < 1) ? 0 : ((@p0 + @p1) - 1)) : @p1)) = @p2)", "the translator's clip");
            var negative = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.SUBSTRING, Path("a"), Int(0), Int(-1)), Lit("a")));
            negative.Computed.Should().ContainSingle();
            foreach (var set in new[] { RenderingSet.Emulator, RenderingSet.Production })
                Generate(negative, set, Strings, JsonCharacterBand.None).Answer.Kind.Should().NotBe(RenderingVerdictKind.Proven, $"a negative length under {set}");
        }

        /// <summary>
        /// An arm over a computed value: beneath the type test's stratum the atom is assumed as its place's image there, by
        /// the assume rule, and the bundle writes no <c>Assumed</c> of its own for it.
        /// </summary>
        [Fact]
        public void AnArmOverAComputedValueIsAssumedByTheRule()
        {
            var lower = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc")));
            var root = new SqlPredicate.Case(new SqlPredicate.TypeTest("IS_STRING", new SqlValue.Raw(NamePath)), lower, new SqlPredicate.Constant(false));
            var theory = ProbeTheory;
            using var bundle = theory.Open(RenderingSet.Emulator);
            var term = bundle.Write(root);
            foreach (var path in root.Places)
                bundle.Import(Source, path, Strings, NoBand);
            foreach (var call in bundle.ComputedCalls)
                bundle.ImportComputed(call, NoBand);

            var tagged = bundle.Atoms.OfType<SqlPredicate.Beneath>().Where(RenderingBundle.IsComputed).ToList();
            tagged.Should().NotBeEmpty();
            bundle.Generate(term);
            bundle.RunGenerate(RenderingBundle.WidthOf([root])).StopReason.Should().Be(RunStopReason.Completed);
            bundle.Failure.Should().BeNull();
            foreach (var atom in tagged)
                bundle.Scope.TryGet(theory.Assumed.Of(bundle.Write(atom)), out PathValues assumed).Should().BeTrue($"{atom} is assumed beneath its stratum");
            bundle.ReadBest(term).Kind.Should().Be(RenderingVerdictKind.Proven);
        }

        /// <summary>
        /// A later round's computed atom is read where its conjunct was rendered (G6): a stratum of the round's own, beneath
        /// which its operands' imports and characters are that node's. A case mapping compared over a path the root may hold
        /// any character of has no image, the case mapping beyond ASCII parting from Calcite's; where the round's node holds
        /// it ASCII the image is there, and the comparison proven, under each set.
        /// </summary>
        [Fact]
        public void ALaterRoundsComputedAtomIsReadBeneathItsRound()
        {
            var upper = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.UPPER, Name), Lit("ABC")));
            RenderingVerdictKind Asked(RenderingSet set, bool beneath)
            {
                using var bundle = ProbeTheory.Open(set);
                var term = bundle.Write(upper);
                foreach (var path in upper.Places)
                    bundle.Import(Source, path, Strings, JsonValues.AllBands);
                foreach (var call in bundle.ComputedCalls)
                    bundle.ImportComputed(call, JsonValues.AllBands);
                if (beneath)
                    bundle.AssumeComputed(Source with { Round = Source.Round + 1 }, upper, [(NamePath, Strings, NoBand)]);

                bundle.Generate(term);
                bundle.RunGenerate(RenderingBundle.WidthOf([upper])).StopReason.Should().Be(RunStopReason.Completed);
                bundle.Failure.Should().BeNull();
                return bundle.ReadBest(term).Kind;
            }

            foreach (var set in new[] { RenderingSet.Production, RenderingSet.Emulator })
            {
                Asked(set, beneath: false).Should().NotBe(RenderingVerdictKind.Proven, $"the root's path may hold a character beyond ASCII under {set}");
                Asked(set, beneath: true).Should().Be(RenderingVerdictKind.Proven, $"the round's node holds it ASCII under {set}");
            }
        }

        /// <summary>
        /// A later round's image of a place makes no atom over it strict elsewhere (G6's check): beneath the round the path
        /// holds strings and the count's keys agree, but at the root it may hold a boolean, whose count the service leaves
        /// undefined where Calcite's is a value, so the root's atom over the same count implies only its path present, as it
        /// does with no round at all; <c>LENGTH(true)</c> is undefined where <c>CHAR_LENGTH('true') = 4</c> is TRUE.
        /// </summary>
        [Fact]
        public void ALaterRoundsImageMakesNoAtomOverItsPlaceStrictAtTheRoot()
        {
            var four = Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.CHAR_LENGTH, Name), Int(4)));
            var above = Loading.Read(Rex.makeCall(SqlStdOperatorTable.GREATER_THAN, Rex.makeCall(SqlStdOperatorTable.CHAR_LENGTH, Name), Int(3)));
            var root = new SqlPredicate.Or([four, above]);
            var mixed = PathValues.OfKinds(ValueSet.Of(StringType, BooleanType));
            ValueSet<ServiceForm> Implied(bool beneath)
            {
                var theory = ProbeTheory;
                using var bundle = theory.Open(RenderingSet.Emulator);
                var term = bundle.Write(root);
                foreach (var path in root.Places)
                    bundle.Import(Source, path, mixed, NoBand);
                foreach (var call in bundle.ComputedCalls)
                    bundle.ImportComputed(call, NoBand);
                if (beneath)
                    bundle.AssumeComputed(Source with { Round = Source.Round + 1 }, above, [(NamePath, Strings, NoBand)]);

                bundle.Generate(term);
                bundle.RunGenerate(RenderingBundle.WidthOf([root])).StopReason.Should().Be(RunStopReason.Completed);
                bundle.Failure.Should().BeNull();
                bundle.Scope.TryGet(theory.Implied.Of(bundle.Write(four), RenderPosition.Pos), out ValueSet<ServiceForm> implied).Should().BeTrue();
                GC.KeepAlive(theory);
                return implied;
            }

            Implied(beneath: true).AsEnumerable().Select(f => f.ToString()).Should().Equal(Implied(beneath: false).AsEnumerable().Select(f => f.ToString()),
                "the round's image holds beneath its round alone");
            Implied(beneath: true).AsEnumerable().Should().NotContain(f => f.ToString().Contains("LENGTH", StringComparison.Ordinal));
        }

    }

}
