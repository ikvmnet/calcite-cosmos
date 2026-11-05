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

        static readonly CosmosPredicateLoader.Loading Loading = new(Fields, Readings, CosmosImplementor.DefaultRootAlias, places: true);

        static readonly CosmosPredicateLoader.Loading Legacy = new(Fields, Readings, CosmosImplementor.DefaultRootAlias, legacy: true);

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

        /// <summary>The loader types a call the rows name and spells its place; the legacy reading S reads until h15 never does.</summary>
        [Fact]
        public void TheLoaderTypesACallTheRowsName()
        {
            var lower = Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.LOWER, Name), Lit("abc"));
            var read = Loading.Read(lower);
            PlaceOf(read).Type.Should().Be("VARCHAR");
            PlaceOf(read).Spelled.Should().Be("LOWER(" + SqlValue.Spell(NamePath) + ")");
            read.Places.Should().Equal(NamePath);
            read.Paths.Should().BeEmpty("F reads the paths a predicate compares, a call's operands no part of them");
            Legacy.Read(lower).Computed.Should().BeEmpty();
            Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlLibraryOperators.SOUNDEX, Name), Lit("abc"))).Computed.Should().BeEmpty("no row names SOUNDEX");
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

        /// <summary>A window with a literal start of one or more round trips too, and a nested call is gathered once its inner place is written.</summary>
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
                    computed: new Dictionary<string, SqlValue.Call> { [place.Spelled!] = place }) : null;
            printed.Should().Be("(SUBSTRING(c.name, (@p0 - 1), @p1) = @p2)");
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
        /// negative length, which Calcite raises over.
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
                computed: new Dictionary<string, SqlValue.Call> { [window.Spelled!] = window })
                .Should().Be("(SUBSTRING(c.a, (@p0 < 1 ? 0 : (@p0 - 1)), (@p0 < 1 ? (((@p0 + @p1) < 1) ? 0 : ((@p0 + @p1) - 1)) : @p1)) = @p2)", "the translator's clip");
            Loading.Read(Rex.makeCall(SqlStdOperatorTable.EQUALS, Rex.makeCall(SqlStdOperatorTable.SUBSTRING, Path("a"), Int(0), Int(-1)), Lit("a"))).Computed.Should().BeEmpty();
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

    }

}
