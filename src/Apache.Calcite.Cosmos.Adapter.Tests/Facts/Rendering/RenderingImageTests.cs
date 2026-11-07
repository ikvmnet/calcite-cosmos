using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// What S reads off a call's gathered operands (<see cref="RenderingImage"/>, h10): the keys each row class falls under,
    /// the operands the rows name alone, and the call at a point; and what S's rules read beside them (G6), the ordinals
    /// and readers of a gathering, the name a call is keyed by, and the images the column gate's replica gives. It decides
    /// nothing.
    /// </summary>
    public sealed class RenderingImageTests
    {

        static PathImport Text(long ordinal, ValueSet<string> kinds, JsonCharacterBand chars = JsonCharacterBand.None) =>
            PathImport.Of(ordinal, PredicateAtom.TextReader, PathValues.OfKinds(kinds), ClassSet(chars));

        static PathImport Literal(long ordinal, string value) => PathImport.Literal(ordinal, value);

        static ValueSet<PathImport> Gathered(params PathImport[] imports) => ValueSet.From(imports);

        /// <summary>The keys of an operand of strings of unknown values under each band: one an atom of a string, empty or not.</summary>
        static ValueSet<FunctionKey> Strings(string function, long ordinal, params JsonCharacterBand[] bands) =>
            ValueSet.From(from band in bands from atom in new[] { FunctionValueClass.Empty, FunctionValueClass.NonEmpty } select FunctionKey.Of(function, ordinal, CosmosValueClass.String, band, atom));

        /// <summary>The keys of a call under the subjects its rows carry.</summary>
        static ValueSet<FunctionKey> Keys(string function, ValueSet<PathImport> gathered) =>
            Keys(function, gathered, ValueSet.From(ProbeRows.Measured.FunctionSubjects.Where(f => f.Function == function).Select(f => f.Subject)));

        /// <summary>The keys of a call under the subjects given, a literal read through the registry's literal readers.</summary>
        static ValueSet<FunctionKey> Keys(string function, ValueSet<PathImport> gathered, ValueSet<long> subjects) =>
            RenderingImage.Keys(function, gathered, subjects, ValueSet.From(ProbeRows.Measured.LiteralReaders));

        /// <summary>A string falls under its kind with no band and with each band its characters may hold; every other kind under its class.</summary>
        [Fact]
        public void EachRowClassIsAKey()
        {
            var keys = Keys("LOWER", Gathered(Text(0, ValueSet.Of(StringType, IntegerType, BooleanType, NullType, PathValues.AbsentKind), JsonCharacterBand.NonAscii | JsonCharacterBand.Nul)));
            keys.AsEnumerable().Select(k => (k.Kind, k.Band)).Distinct().Should().BeEquivalentTo(new[]
            {
                (CosmosValueClass.String, JsonCharacterBand.None), (CosmosValueClass.String, JsonCharacterBand.NonAscii), (CosmosValueClass.String, JsonCharacterBand.Nul),
                (CosmosValueClass.Number, JsonCharacterBand.None), (CosmosValueClass.True, JsonCharacterBand.None), (CosmosValueClass.False, JsonCharacterBand.None),
                (CosmosValueClass.Null, JsonCharacterBand.None), (CosmosValueClass.Absent, JsonCharacterBand.None),
            });
        }

        /// <summary>
        /// An operand is keyed where a row names it, and only there: <c>REPLACE</c>'s search and replacement, which rows name
        /// (F2), each by its own ordinal, its band and its atom; <c>POSITION</c>'s needle by its ordinal, as its rows name it;
        /// and a concatenation's literal at <see cref="FunctionKey.Literal"/> as well as every operand's key, its rows keying a
        /// literal apart from a stored operand, which a path is not keyed at.
        /// </summary>
        [Fact]
        public void AnOperandIsKeyedOnlyWhereARowNamesIt()
        {
            static FunctionKey Operand(long ordinal, JsonCharacterBand band) => FunctionKey.Of("REPLACE", ordinal, CosmosValueClass.String, band, FunctionValueClass.NonEmpty);
            Keys("REPLACE", Gathered(Text(0, ValueSet.Of(StringType)), Literal(1, Of("a\u0000")), Literal(2, Of("b"))))
                .Should().BeEquivalentTo(Strings("REPLACE", 0, JsonCharacterBand.None).Add(Operand(1, JsonCharacterBand.None)).Add(Operand(1, JsonCharacterBand.Nul)).Add(Operand(2, JsonCharacterBand.None)));

            static FunctionKey Needle(JsonCharacterBand band) => FunctionKey.Of("POSITION", 0, CosmosValueClass.String, band, FunctionValueClass.NonEmpty);
            Keys("POSITION", Gathered(Literal(0, Of("é")), Text(1, ValueSet.Of(StringType))))
                .Should().BeEquivalentTo(Strings("POSITION", 1, JsonCharacterBand.None).Add(Needle(JsonCharacterBand.None)).Add(Needle(JsonCharacterBand.NonAscii)));

            static FunctionKey Joined(long ordinal) => FunctionKey.Of("||", ordinal, CosmosValueClass.String, JsonCharacterBand.None, FunctionValueClass.NonEmpty);
            Keys("||", Gathered(Text(0, ValueSet.Of(StringType)), Literal(1, Of("z"))))
                .Should().BeEquivalentTo(Strings("||", FunctionKey.Every, JsonCharacterBand.None).Add(Joined(FunctionKey.Literal)));
            Keys("||", Gathered(Text(0, ValueSet.Of(StringType)), Text(1, ValueSet.Of(StringType))))
                .Should().BeEquivalentTo(Strings("||", FunctionKey.Every, JsonCharacterBand.None));
        }

        /// <summary>
        /// Every operand the rows name is keyed by its ordinal (F2), and a refinement of a class keys under the class it
        /// lies within: a string that may hold U+FFFD or an unpaired surrogate falls under the high band's and the
        /// surrogates' keys, never a key of its own.
        /// </summary>
        [Fact]
        public void EveryOperandTheRowsNameIsKeyed()
        {
            var gathered = Gathered(Text(0, ValueSet.Of(StringType), JsonCharacterBand.HighBmp | JsonCharacterBand.Replacement), Literal(1, Of("a\uD83D")), Literal(2, Of("b")));
            static FunctionKey Search(JsonCharacterBand band) => FunctionKey.Of("REPLACE", 1, CosmosValueClass.String, band, FunctionValueClass.NonEmpty);
            Keys("REPLACE", gathered, ValueSet.Of(0L, 1L)).Should().BeEquivalentTo(
                Strings("REPLACE", 0, JsonCharacterBand.None, JsonCharacterBand.HighBmp).Add(Search(JsonCharacterBand.None)).Add(Search(JsonCharacterBand.Surrogates)));
            Keys("||", Gathered(Literal(0, Of("a")), Literal(1, Of("b"))), ValueSet.Of(FunctionKey.Every))
                .Should().Equal(ValueSet.Of(FunctionKey.Of("||", FunctionKey.Every, CosmosValueClass.String, JsonCharacterBand.None, FunctionValueClass.NonEmpty)));
        }

        /// <summary>
        /// A cell is keyed by the atoms its values may be in (F2), whatever the rows name: a literal by its own, a path of
        /// unknown values by every atom of its kind, a number by its sign and its place against 1 met, and a kind no class
        /// splits by its kind alone. Which rows cover an atom is S's (<see cref="RenderingValuedTests.ARowCoversTheAtomsItsClassHolds"/>).
        /// </summary>
        [Fact]
        public void ACellIsKeyedByTheAtomsItsValuesAreIn()
        {
            static ValueSet<FunctionValueClass> Atoms(PathImport import, CosmosValueClass kind) =>
                ValueSet.From(Keys("F", Gathered(import), ValueSet.Of(0L)).AsEnumerable()
                    .Where(k => k.Kind == kind && k.Band == JsonCharacterBand.None).Select(k => k.Value));

            Atoms(Literal(0, Of("")), CosmosValueClass.String).Should().Equal(ValueSet.Of(FunctionValueClass.Empty));
            Atoms(Literal(0, Of("a")), CosmosValueClass.String).Should().Equal(ValueSet.Of(FunctionValueClass.NonEmpty));
            Atoms(Text(0, ValueSet.Of(StringType)), CosmosValueClass.String).Should().Equal(ValueSet.Of(FunctionValueClass.Empty, FunctionValueClass.NonEmpty));
            Atoms(Literal(0, Of(-2m)), CosmosValueClass.Number).Should().Equal(ValueSet.Of(FunctionValueClass.Negative));
            Atoms(Literal(0, Of(0.5m)), CosmosValueClass.Number).Should().Equal(ValueSet.Of(FunctionValueClass.PositiveBelowOne));
            Atoms(Literal(0, Of(1m)), CosmosValueClass.Number).Should().Equal(ValueSet.Of(FunctionValueClass.AtLeastOne));
            Atoms(Text(0, ValueSet.Of(IntegerType)), CosmosValueClass.Number).Should().Equal(ValueSet.Of(
                FunctionValueClass.Negative, FunctionValueClass.Zero, FunctionValueClass.PositiveBelowOne, FunctionValueClass.AtLeastOne));
            Atoms(Text(0, ValueSet.Of(NullType)), CosmosValueClass.Null).Should().Equal(ValueSet.Of(FunctionValueClass.Any));
        }

        /// <summary>
        /// A number held approximately, whose place against 0 and 1 no bound reads, is in every atom of a number, whatever
        /// its sign: a cell holding one is keyed by each, as a cell of unknown numbers is, and every class holds it.
        /// </summary>
        [Fact]
        public void ACellHoldingAnApproximateNumberIsKeyedByEveryNumberAtom()
        {
            var every = ValueSet.From(FunctionValueClasses.AtomsOf(CosmosValueClass.Number));
            foreach (var approximate in new[] { Of(1e30), Of(-1e30), Of(1e-30) })
            {
                IsExactNumber(approximate).Should().BeFalse(approximate);
                ValueSet.From(Keys("F", Gathered(Literal(0, approximate)), ValueSet.Of(0L)).AsEnumerable().Select(k => k.Value))
                    .Should().Equal(every, approximate);
                foreach (var (_, @class, _) in FunctionValueClasses.Holding.Where(h => h.Kind == CosmosValueClass.Number))
                    FunctionValueClasses.Holds(@class, approximate).Should().BeTrue($"{@class} of {approximate}");
            }
        }

        /// <summary>Every class holds the atoms it is the union of, and every atom is held by its kind's <see cref="FunctionValueClass.Any"/>.</summary>
        [Fact]
        public void EveryClassHoldsTheAtomsItIsTheUnionOf()
        {
            var values = new[] { Of(-2m), Of(0m), Of(0.5m), Of(1m), Of(7m), Of(""), Of("a") };
            foreach (var (kind, @class, atom) in FunctionValueClasses.Holding.Where(h => h.Class != FunctionValueClass.Any))
                FunctionValueClasses.AtomsOf(kind).Should().Contain(atom, $"{@class} holds an atom of {kind}");
            foreach (var kind in new[] { CosmosValueClass.Number, CosmosValueClass.String })
                foreach (var @class in FunctionValueClasses.Holding.Where(h => h.Kind == kind).Select(h => h.Class).Distinct())
                    foreach (var value in values.Where(v => CosmosValueClasses.Of(v) == kind))
                        FunctionValueClasses.Holds(@class, value).Should().Be(
                            FunctionValueClasses.Holding.Any(h => h.Kind == kind && h.Class == @class && FunctionValueClasses.Holds(h.Atom, value)), $"{@class} of {value}");
        }

        /// <summary>
        /// A repeated operand is gathered once an ordinal, so <c>x || x</c> is two imports at two ordinals; a gathering
        /// doubling an ordinal holds fewer ordinals than imports, which S's image rules read as no gathering of the call's
        /// arity (G6), and a kind no class names is keyed as it is, a key no row holds.
        /// </summary>
        [Fact]
        public void ARepeatedOperandIsOneImportAnOrdinal()
        {
            var x = ValueSet.Of(StringType);
            PathImport.OrdinalsOf(Gathered(Text(0, x), Text(1, x))).Should().Equal(ValueSet.Of(0L, 1L));
            PathImport.OrdinalsOf(Gathered(Text(0, x), Text(0, ValueSet.Of(StringType, NullType)))).Should().Equal(ValueSet.Of(0L), "two imports at one ordinal are no gathering of two operands");
            Keys("F", Gathered(Text(0, ValueSet.Of("date"))), ValueSet.Of(0L)).Should().Equal(ValueSet.Of(FunctionKey.Of("F", 0, CosmosValueClass.None, JsonCharacterBand.None)));
        }

        /// <summary>
        /// A nested place is read through its type's reader, a text place as text, which the rows were measured through; a
        /// raw reading was measured by none, and its reader lies beyond what S's image rules license (G6). A call is keyed by
        /// the name its rows give it at its type, an arithmetic operator's with its type, and none where they name none.
        /// </summary>
        [Fact]
        public void ANestedPlaceIsReadThroughItsType()
        {
            var inner = PathImport.Of(0, PredicateAtom.TypedReader("VARCHAR"), PathValues.OfKinds(ValueSet.Of(StringType)), ClassSet(JsonCharacterBand.None));
            Keys("LOWER", Gathered(inner)).Should().Equal(Strings("LOWER", 0, JsonCharacterBand.None));
            var (_, readers) = ProbeRows.Measured.FunctionReads.Single(f => f.Function == "LOWER");
            var licensed = ValueSet.From(readers).Union(ValueSet.From(ProbeRows.Measured.LiteralReaders));
            PathImport.ReadersOf(Gathered(inner, Literal(1, Of("a")))).AsEnumerable().All(licensed.Contains).Should().BeTrue();
            PathImport.ReadersOf(Gathered(PathImport.Of(0, PredicateAtom.RawReader, PathValues.OfKinds(ValueSet.Of(StringType)), NoBand))).AsEnumerable().All(licensed.Contains).Should().BeFalse();
            ProbeRows.Measured.CallKey("+", "INTEGER").Should().Be("+:INTEGER");
            ProbeRows.Measured.CallKey("LOWER", "VARCHAR").Should().Be("LOWER");
            ProbeRows.Measured.CallKey("+", "SMALLINT").Should().BeNull("no row measured + at SMALLINT");
            ProbeRows.Measured.CallKey("ABS", "INTEGER").Should().BeNull();
        }

        /// <summary>
        /// The image is the call evaluated on single values C# computes as Calcite does, and elsewhere the kinds S's rule
        /// gave it: never narrower than the call.
        /// </summary>
        [Fact]
        public void TheImageIsTheCallOnPointsAndItsKindsElsewhere()
        {
            var strings = PathValues.OfKinds(ValueSet.Of(StringType));
            RenderingImage.Of("LOWER", Gathered(Literal(0, Of("AbC"))), strings).Should().Be(PathImport.CellOf(Of("abc")));
            RenderingImage.Of("UPPER", Gathered(Literal(0, Of("straße"))), strings).Should().Be(strings, "Java's full mapping is not C#'s outside ASCII");
            RenderingImage.Of("CHAR_LENGTH", Gathered(Literal(0, Of("a\U0001F600"))), PathValues.OfKinds(ValueSet.Of(IntegerType))).Should().Be(PathImport.CellOf(Of(3m)), "Calcite counts UTF-16 units");
            RenderingImage.Of("TRIM BOTH", Gathered(Literal(0, Of(" ")), Literal(1, Of(" a\t "))), strings).Should().Be(PathImport.CellOf(Of("a\t")));
            RenderingImage.Of("LOWER", Gathered(Text(0, ValueSet.Of(StringType))), strings).Should().Be(strings);
        }

    }

}
