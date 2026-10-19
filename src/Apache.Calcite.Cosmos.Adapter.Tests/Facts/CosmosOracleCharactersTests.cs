using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// What a filter says of the characters of a path's strings, read off the scope a question is licensed to read: the
    /// theory's fact, derived from the values a filter's <c>=</c> and <c>IN</c> name, per disjunct, and met with what the
    /// declaration states.
    /// </summary>
    /// <remarks>
    /// Nothing here computes a character: each answer is <see cref="JsonFactSet.CharactersOf"/>, the theory's own fact,
    /// which is what an order over the path asks (<c>CosmosServiceOrder</c>), and a string function over it
    /// (<c>CosmosStringFunctions</c>).
    /// </remarks>
    public sealed class CosmosOracleCharactersTests
    {

        static readonly JsonDocumentPath S = P("s");
        static readonly JsonDocumentPath T = P("t");
        static readonly JsonDocumentPath Kind = P("kind");
        static readonly JsonDocumentPath Label = P("label");

        const string Emoji = "a😀";

        const string Private = "";

        static JsonCharacterBand Kept(JsonFactTheory theory, JsonDocumentPath path, params SqlPredicate[] conjuncts) =>
            Established(theory, conjuncts).CharactersOf(path);

        [Fact]
        public void AFiltersValuesProveItsCharactersInTheLastStratum()
        {
            Kept(Theory(), S, In(Text(S), "a", "b")).Should().Be(JsonCharacterBand.None);
            Kept(Theory(), S, Eq(Raw(S), Emoji)).Should().Be(JsonCharacterBand.Surrogates);
            Kept(Theory(), S, In(Text(S), Private, Emoji)).Should().Be(JsonCharacterBand.FromD800);
            Kept(Theory(), S, Eq(Text(S), "30")).Should().Be(JsonCharacterBand.None, "the rendering's preimage, a string and the numbers that render as it, is ASCII");
        }

        /// <summary>
        /// The classes a string function asks of its arguments come the same way: a value outside ASCII below the
        /// surrogates, and one the service's <c>TRIM</c> removes, each a class of the value's own, read as the theory reads
        /// it.
        /// </summary>
        [Fact]
        public void AFiltersValuesProveWhetherItsStringsAreAsciiAndWhatTheServiceTrims()
        {
            Kept(Theory(), S, In(Text(S), "a b", "c")).Should().Be(JsonCharacterBand.None, "a space is ASCII, and both trims remove it");
            Kept(Theory(), S, In(Text(S), "a\tb", "c")).Should().Be(JsonCharacterBand.Trimmed);
            Kept(Theory(), S, Eq(Text(S), "a\u00DFb")).Should().Be(JsonCharacterBand.NonAscii);
            Kept(Theory(), S, Eq(Text(S), "a\u00A0b")).Should().Be(JsonCharacterBand.NonAscii | JsonCharacterBand.Trimmed);
            Kept(Theory(), S, Or(Eq(Text(S), "a\u3000"), Eq(Text(S), Emoji))).Should().Be(JsonCharacterBand.NonAscii | JsonCharacterBand.Trimmed | JsonCharacterBand.Surrogates);
        }

        [Fact]
        public void ADisjunctionWidensToWhatEitherArmMayHold()
        {
            Kept(Theory(), S, Or(Eq(Text(S), "a"), In(Text(S), "b", "中"))).Should().Be(JsonCharacterBand.NonAscii, "one arm's value is outside ASCII, below U+D800");
            Kept(Theory(), S, Or(Eq(Text(S), "a"), Eq(Text(S), Emoji))).Should().Be(JsonCharacterBand.Surrogates);
            Kept(Theory(), S, Or(Eq(Text(S), Private), Eq(Text(S), Emoji))).Should().Be(JsonCharacterBand.FromD800);
            Kept(Theory(), S, Or(Eq(Text(S), "a"), Eq(Text(T), "b"))).Should().Be(JsonCharacterBand.Any, "the other arm says nothing of s");
        }

        [Fact]
        public void AConjunctionMeetsAFilterWithTheDeclaration()
        {
            var declared = Theory(Outright(S, Type(JsonType.String)), Outright(S, new JsonClaim.Characters(JsonCharacterBand.HighBmp)));

            Kept(declared, S, In(Text(S), "a", Emoji)).Should().Be(JsonCharacterBand.None);
            Kept(declared, S, IsNotNull(Text(S))).Should().Be(JsonCharacterBand.HighBmp, "the declaration's alone");
        }

        [Fact]
        public void AKindsBranchHoldsItsOwnUnderTheFilterThatPicksIt()
        {
            var theory = Theory(
                Outright(Kind, Enum("k1", "k2")),
                When(Kind, "k1", Label, new JsonClaim.Characters(JsonCharacterBand.None)),
                When(Kind, "k2", Label, new JsonClaim.Characters(JsonCharacterBand.HighBmp)));

            Kept(theory, Label, Eq(Text(Kind), "k1")).Should().Be(JsonCharacterBand.None);
            Kept(theory, Label, In(Text(Kind), "k1", "k2")).Should().Be(JsonCharacterBand.HighBmp, "both kinds', joined");
            Oracle(theory).Declared.CharactersOf(Label).Should().Be(JsonCharacterBand.Any, "a document of no kind may hold any");
        }

        /// <summary>
        /// Licensed per scope like any other fact: a conjunct is rendered from the facts above it, and what its own values
        /// say of its path enters only below.
        /// </summary>
        [Fact]
        public void AConjunctIsNotRenderedFromItsOwnCharacters()
        {
            var scope = Admitted(Theory(), In(Text(S), "a", "b"), IsNotNull(Text(T)));

            scope.Facts(OracleLicence.RenderConjunct(0)).CharactersOf(S).Should().Be(JsonCharacterBand.Any);
            scope.Facts(OracleLicence.RenderConjunct(1)).CharactersOf(S).Should().Be(JsonCharacterBand.Any);
            scope.Facts(OracleLicence.Select).CharactersOf(S).Should().Be(JsonCharacterBand.None);
            scope.Facts(OracleLicence.Declaration).CharactersOf(S).Should().Be(JsonCharacterBand.Any);
        }

    }

}
