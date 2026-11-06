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
    /// the operands the rows name alone, a repeated operand once an ordinal, a nested place, a (D) function's image,
    /// and a gathering that is no import an ordinal falling under no row. It decides nothing.
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

        /// <summary>The keys of a call under the subjects and readers its rows carry.</summary>
        static ValueSet<FunctionKey> Keys(string function, ValueSet<PathImport> gathered)
        {
            var subjects = ValueSet.From(ProbeRows.Measured.FunctionSubjects.Where(f => f.Function == function).Select(f => f.Subject));
            var (_, readers) = ProbeRows.Measured.FunctionReads.Single(f => f.Function == function);
            return RenderingImage.Keys(function, gathered, subjects, ValueSet.From(readers));
        }

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
        /// (F2), each by its own ordinal, its band and its atom; <c>POSITION</c>'s needle, which none names, by none.
        /// </summary>
        [Fact]
        public void AnOperandIsKeyedOnlyWhereARowNamesIt()
        {
            static FunctionKey Operand(long ordinal, JsonCharacterBand band) => FunctionKey.Of("REPLACE", ordinal, CosmosValueClass.String, band, FunctionValueClass.NonEmpty);
            Keys("REPLACE", Gathered(Text(0, ValueSet.Of(StringType)), Literal(1, Of("a\u0000")), Literal(2, Of("b"))))
                .Should().BeEquivalentTo(Strings("REPLACE", 0, JsonCharacterBand.None).Add(Operand(1, JsonCharacterBand.None)).Add(Operand(1, JsonCharacterBand.Nul)).Add(Operand(2, JsonCharacterBand.None)));
            Keys("POSITION", Gathered(Literal(0, Of("é")), Text(1, ValueSet.Of(StringType))))
                .Should().BeEquivalentTo(Strings("POSITION", 1, JsonCharacterBand.None));
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
            RenderingImage.Keys("REPLACE", gathered, ValueSet.Of(0L, 1L), ValueSet.Of(PredicateAtom.TextReader)).Should().BeEquivalentTo(
                Strings("REPLACE", 0, JsonCharacterBand.None, JsonCharacterBand.HighBmp).Add(Search(JsonCharacterBand.None)).Add(Search(JsonCharacterBand.Surrogates)));
            RenderingImage.Keys("||", Gathered(Literal(0, Of("a")), Literal(1, Of("b"))), ValueSet.Of(FunctionKey.Every), ValueSet<string>.Empty)
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
                ValueSet.From(RenderingImage.Keys("F", Gathered(import), ValueSet.Of(0L), ValueSet.Of(PredicateAtom.TextReader)).AsEnumerable()
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
                ValueSet.From(RenderingImage.Keys("F", Gathered(Literal(0, approximate)), ValueSet.Of(0L), ValueSet<string>.Empty).AsEnumerable().Select(k => k.Value))
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

        /// <summary>A repeated operand is gathered once an ordinal, so <c>x || x</c> is two imports; a gathering missing or doubling an ordinal is malformed.</summary>
        [Fact]
        public void ARepeatedOperandIsOneImportAnOrdinal()
        {
            var x = ValueSet.Of(StringType);
            Keys("||", Gathered(Text(0, x), Text(1, x))).Should().NotContain(RenderingImage.Malformed);
            Keys("||", Gathered(Text(0, x), Text(2, x))).Should().Equal(ValueSet.Of(RenderingImage.Malformed));
            Keys("||", Gathered(Text(0, x), Text(0, ValueSet.Of(StringType, NullType)))).Should().Equal(ValueSet.Of(RenderingImage.Malformed));
        }

        /// <summary>A nested place is read through its type's reader, a text place as text; a raw reading was measured by no row.</summary>
        [Fact]
        public void ANestedPlaceIsReadThroughItsType()
        {
            var inner = PathImport.Of(0, PredicateAtom.TypedReader("VARCHAR"), PathValues.OfKinds(ValueSet.Of(StringType)), ClassSet(JsonCharacterBand.None));
            Keys("LOWER", Gathered(inner)).Should().Equal(Strings("LOWER", 0, JsonCharacterBand.None));
            Keys("LOWER", Gathered(PathImport.Of(0, PredicateAtom.RawReader, PathValues.OfKinds(ValueSet.Of(StringType)), NoBand))).Should().Equal(ValueSet.Of(RenderingImage.Malformed));
            RenderingImage.KeyFunction("+", "INTEGER").Should().Be("+:INTEGER");
            RenderingImage.KeyFunction("LOWER", "VARCHAR").Should().Be("LOWER");
        }

        /// <summary>
        /// The image is the call evaluated on single values C# computes as Calcite does, and elsewhere every value of the
        /// result's type, with absent wherever an operand may be other than a string: never narrower than the call.
        /// </summary>
        [Fact]
        public void TheImageIsTheCallOnPointsAndItsTypeElsewhere()
        {
            RenderingImage.Of("LOWER", "VARCHAR", Gathered(Literal(0, Of("AbC")))).Should().Be(PathImport.CellOf(Of("abc")));
            RenderingImage.Of("UPPER", "VARCHAR", Gathered(Literal(0, Of("straße")))).Should().Be(PathValues.OfKinds(ValueSet.Of(StringType)), "Java's full mapping is not C#'s outside ASCII");
            RenderingImage.Of("CHAR_LENGTH", "INTEGER", Gathered(Literal(0, Of("a\U0001F600")))).Should().Be(PathImport.CellOf(Of(3m)), "Calcite counts UTF-16 units");
            RenderingImage.Of("TRIM BOTH", "VARCHAR", Gathered(Literal(0, Of(" ")), Literal(1, Of(" a\t ")))).Should().Be(PathImport.CellOf(Of("a\t")));
            RenderingImage.Of("LOWER", "VARCHAR", Gathered(Text(0, ValueSet.Of(StringType)))).Should().Be(PathValues.OfKinds(ValueSet.Of(StringType)));
            RenderingImage.Of("LOWER", "VARCHAR", Gathered(Text(0, ValueSet.Of(StringType, PathValues.AbsentKind)))).Should().Be(PathValues.OfKinds(ValueSet.Of(StringType, PathValues.AbsentKind)));
        }

        /// <summary>A (D) function's image is every kind of its result type and undefined, never narrower.</summary>
        [Fact]
        public void ADeclaredFunctionsImageIsItsResultTypeAndUndefined()
        {
            RenderingImage.Of("ToString", "VARCHAR", Gathered(Text(0, ValueSet.Of(IntegerType, FractionType)))).Should().Be(PathValues.OfKinds(ValueSet.Of(StringType, PathValues.AbsentKind)));
            RenderingImage.Of("+:INTEGER", "INTEGER", Gathered(PathImport.Of(0, PredicateAtom.TypedReader("INTEGER"), PathValues.OfKinds(ValueSet.Of(IntegerType)), NoBand), Literal(1, Of(1m))))
                .Should().Be(PathValues.OfKinds(ValueSet.Of(IntegerType)));
        }

    }

}
