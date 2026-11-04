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
    /// the subject alone of a two-operand call, a repeated operand once an ordinal, a nested place, a (D) function's image,
    /// and a gathering that is no import an ordinal falling under no row. It decides nothing.
    /// </summary>
    public sealed class RenderingImageTests
    {

        static PathImport Text(long ordinal, ValueSet<string> kinds, JsonCharacterBand chars = JsonCharacterBand.None) =>
            PathImport.Of(ordinal, PredicateAtom.TextReader, PathValues.OfKinds(kinds), ClassSet(chars));

        static PathImport Literal(long ordinal, string value) => PathImport.Literal(ordinal, value);

        static ValueSet<PathImport> Gathered(params PathImport[] imports) => ValueSet.From(imports);

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
            keys.AsEnumerable().Select(k => (k.Kind, k.Band)).Should().BeEquivalentTo(new[]
            {
                (CosmosValueClass.String, JsonCharacterBand.None), (CosmosValueClass.String, JsonCharacterBand.NonAscii), (CosmosValueClass.String, JsonCharacterBand.Nul),
                (CosmosValueClass.Number, JsonCharacterBand.None), (CosmosValueClass.True, JsonCharacterBand.None), (CosmosValueClass.False, JsonCharacterBand.None),
                (CosmosValueClass.Null, JsonCharacterBand.None), (CosmosValueClass.Absent, JsonCharacterBand.None),
            });
        }

        /// <summary>A two-operand call's rows describe its subject: <c>REPLACE</c>'s search and replacement fall under no key, <c>POSITION</c>'s needle neither.</summary>
        [Fact]
        public void ATwoOperandCallIsKeyedByItsSubject()
        {
            Keys("REPLACE", Gathered(Text(0, ValueSet.Of(StringType)), Literal(1, Of("a\u0000")), Literal(2, Of("b"))))
                .Should().BeEquivalentTo(ValueSet.Of(FunctionKey.Of("REPLACE", 0, CosmosValueClass.String, JsonCharacterBand.None)));
            Keys("POSITION", Gathered(Literal(0, Of("é")), Text(1, ValueSet.Of(StringType))))
                .Should().BeEquivalentTo(ValueSet.Of(FunctionKey.Of("POSITION", 1, CosmosValueClass.String, JsonCharacterBand.None)));
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
            RenderingImage.Keys("REPLACE", gathered, ValueSet.Of(0L, 1L), ValueSet.Of(PredicateAtom.TextReader)).Should().BeEquivalentTo(ValueSet.Of(
                FunctionKey.Of("REPLACE", 0, CosmosValueClass.String, JsonCharacterBand.None), FunctionKey.Of("REPLACE", 0, CosmosValueClass.String, JsonCharacterBand.HighBmp),
                FunctionKey.Of("REPLACE", 1, CosmosValueClass.String, JsonCharacterBand.None), FunctionKey.Of("REPLACE", 1, CosmosValueClass.String, JsonCharacterBand.Surrogates)));
            RenderingImage.Keys("||", Gathered(Literal(0, Of("a")), Literal(1, Of("b"))), ValueSet.Of(FunctionKey.Every), ValueSet<string>.Empty)
                .Should().Equal(ValueSet.Of(FunctionKey.Of("||", FunctionKey.Every, CosmosValueClass.String, JsonCharacterBand.None)));
        }

        /// <summary>
        /// A key is split by the classes of value the rows split its operand by (F2), one a class its cells may hold a value
        /// of: a literal by its own, a path of unknown values by every one, beside the unsplit key where a value may lie in
        /// none of them. Rows that split nothing leave the keys as they are.
        /// </summary>
        [Fact]
        public void AKeyIsSplitByTheClassesItsRowsName()
        {
            static FunctionKey Search(FunctionValueClass value) => FunctionKey.Of("REPLACE", 1, CosmosValueClass.String, JsonCharacterBand.None, value);
            var subject = FunctionKey.Of("REPLACE", 0, CosmosValueClass.String, JsonCharacterBand.None);
            var measured = ValueSet.Of(subject, Search(FunctionValueClass.Empty), Search(FunctionValueClass.NonEmpty));
            ValueSet<FunctionKey> Classed(PathImport search) =>
                RenderingImage.Classed(ValueSet.Of(subject, Search(FunctionValueClass.Any)), Gathered(Text(0, ValueSet.Of(StringType)), search), measured);

            Classed(Literal(1, Of(""))).Should().Equal(ValueSet.Of(subject, Search(FunctionValueClass.Empty)));
            Classed(Literal(1, Of("a"))).Should().Equal(ValueSet.Of(subject, Search(FunctionValueClass.NonEmpty)));
            Classed(Text(1, ValueSet.Of(StringType))).Should().Equal(ValueSet.Of(subject, Search(FunctionValueClass.Empty), Search(FunctionValueClass.NonEmpty)));
            RenderingImage.Classed(ValueSet.Of(subject, Search(FunctionValueClass.Any)), Gathered(Text(0, ValueSet.Of(StringType)), Text(1, ValueSet.Of(StringType))),
                ValueSet.Of(subject, Search(FunctionValueClass.Empty))).Should().Equal(ValueSet.Of(subject, Search(FunctionValueClass.Any), Search(FunctionValueClass.Empty)),
                "a search may be of no class the rows name, so its unsplit key stays, which no row holds");
            RenderingImage.Classed(ValueSet.Of(subject), Gathered(Literal(0, Of("a"))), ValueSet.Of(subject)).Should().Equal(ValueSet.Of(subject));
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
            Keys("LOWER", Gathered(inner)).Should().Equal(ValueSet.Of(FunctionKey.Of("LOWER", 0, CosmosValueClass.String, JsonCharacterBand.None)));
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
