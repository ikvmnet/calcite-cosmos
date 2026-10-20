using System.Linq;

using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// What the theory derives of a computed string's characters: a call's, from what its function draws from each operand
    /// and what each holds, asked of a fact set as a string function's rule asks it (<c>JsonFactSet.CharactersOf</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every answer here is a run of the theory's rules of a value's characters (<c>PredicateRules.Characters</c>) over the
    /// value's term, beneath the context the facts were read from; nothing in C# combines one operand's classes with
    /// another's. <c>PredicateSoundnessTests</c> holds the same answers to documents, through population D.
    /// </para>
    /// </remarks>
    public sealed class ComputedCharactersTests
    {

        const JsonCharacterBand S = JsonCharacterBand.Surrogates;
        const JsonCharacterBand H = JsonCharacterBand.HighBmp;
        const JsonCharacterBand N = JsonCharacterBand.NonAscii;
        const JsonCharacterBand W = JsonCharacterBand.Trimmed;
        const JsonCharacterBand Z = JsonCharacterBand.Nul;

        static readonly JsonDocumentPath A = JsonDocumentPath.Root.Property("a");

        static readonly JsonDocumentPath B = JsonDocumentPath.Root.Property("b");

        static SqlValue Text(JsonDocumentPath path) => new SqlValue.Text(path);

        static SqlValue Lit(string text) => SqlValue.Of(text);

        static SqlValue Number(int value) => SqlValue.Of((decimal)value);

        static SqlValue Call(string function, params SqlValue[] operands) => new SqlValue.Call(function, operands);

        /// <summary>Facts saying what <c>a</c> and <c>b</c> hold: characters of these classes, if strings.</summary>
        static JsonFactSet Holding(JsonCharacterBand a, JsonCharacterBand b, params JsonFact[] more) =>
            new JsonFactTheory(new[] { new JsonFact(A, new JsonClaim.Characters(a)), new JsonFact(B, new JsonClaim.Characters(b)) }.Concat(more)
                .Select(JsonFactRule.Unconditional).ToArray()).Derive(null);

        [Fact]
        public void AConcatenationHoldsWhatEitherSideHolds()
        {
            var facts = Holding(S, W);

            facts.CharactersOf(Call("||", Text(A), Text(B))).Should().Be(S | W);
            facts.CharactersOf(Call("CONCAT", Text(A), Lit("xß"), Text(B))).Should().Be(S | N | W);
            facts.CharactersOf(Call("||", Lit("a"), Lit("b"))).Should().Be(JsonCharacterBand.None);
        }

        /// <summary>
        /// Java's full upper-case mapping takes U+FB13 to Armenian letters below U+D800, so a string from U+E000 may
        /// hold one once upper-cased; the lower-case mapping keeps every class where it was.
        /// </summary>
        [Fact]
        public void ACaseMappingKeepsEveryClassButWhatUpperMapsFromTheHighBand()
        {
            var facts = Holding(H, S);

            facts.CharactersOf(Call("UPPER", Text(A))).Should().Be(H | N);
            facts.CharactersOf(Call("LOWER", Text(A))).Should().Be(H);
            facts.CharactersOf(Call("UPPER", Text(B))).Should().Be(S);
        }

        /// <summary>
        /// A window draws from its string and not from its numbers; a replacement from its subject and its replacement,
        /// not from what it seeks; a trim from what it trims, its flag spelled in its name; a conditional from its arms.
        /// </summary>
        [Fact]
        public void EachFunctionDrawsFromTheOperandsItsValueIsMadeOf()
        {
            var facts = Holding(S, N | W);

            facts.CharactersOf(Call("SUBSTRING", Text(A), Number(2), Number(1))).Should().Be(S);
            facts.CharactersOf(Call("LEFT", Text(B), Number(2))).Should().Be(N | W);
            facts.CharactersOf(Call("REPLACE", Text(A), Text(B), Lit("!"))).Should().Be(S, "the search is replaced, and none of it is kept");
            facts.CharactersOf(Call("REPLACE", Text(A), Lit("x"), Text(B))).Should().Be(S | N | W);
            facts.CharactersOf(Call("TRIM BOTH", Lit(" "), Text(B))).Should().Be(N | W);
            facts.CharactersOf(Call("CASE", Text(A), Lit("\0"))).Should().Be(S | Z);
            facts.CharactersOf(Call("REVERSE", Call("REPEAT", Text(B), Number(2)))).Should().Be(N | W);
        }

        [Fact]
        public void ACallOverACallDrawsThroughIt()
        {
            var facts = Holding(H, JsonCharacterBand.None);

            facts.CharactersOf(Call("UPPER", Call("||", Text(B), Text(A)))).Should().Be(H | N);
            facts.CharactersOf(Call("SUBSTRING", Call("LOWER", Text(B)), Number(1), Number(3))).Should().Be(JsonCharacterBand.None);
        }

        /// <summary>
        /// A parameter may hold anything until it is bound, a path nothing is said of anything, and a function the theory
        /// does not read may make characters of its own: a call over one, or of one, is no narrower than anything.
        /// </summary>
        [Fact]
        public void WhatNothingBoundsHoldsAnything()
        {
            var facts = Holding(JsonCharacterBand.None, JsonCharacterBand.None);

            facts.CharactersOf(Call("||", Text(A), new SqlValue.Parameter(0, "VARCHAR"))).Should().Be(JsonCharacterBand.Any);
            facts.CharactersOf(Call("||", Text(A), Text(JsonDocumentPath.Root.Property("c")))).Should().Be(JsonCharacterBand.Any);
            facts.CharactersOf(Call("INITCAP", Text(A))).Should().Be(JsonCharacterBand.Any);
            facts.CharactersOf(Call("UPPER", Call("ToString", Text(A)))).Should().Be(JsonCharacterBand.Any);
            JsonFactSet.Empty.CharactersOf(Call("||", Lit("a"), Lit("b"))).Should().Be(JsonCharacterBand.Any, "nothing is known where there is no theory");
        }

        /// <summary>
        /// A cast to text of the value a path holds is the path's characters where it holds scalars alone: a number or a
        /// boolean renders in ASCII, and an object as the JSON of whatever it holds.
        /// </summary>
        [Fact]
        public void TheCastToTextOfAScalarHoldsThePathsCharacters()
        {
            var cast = new SqlValue.Cast("VARCHAR", new SqlValue.Raw(A));

            Holding(S, JsonCharacterBand.None, new JsonFact(A, new JsonClaim.OfType(JsonType.String, OrNull: true))).CharactersOf(cast).Should().Be(S);
            Holding(S, JsonCharacterBand.None).CharactersOf(cast).Should().Be(JsonCharacterBand.Any, "the path may hold an object");
        }

        /// <summary>
        /// What a filter's values say of a path's characters says it of a call over the path, in the stratum the filter
        /// holds in: a domain of ASCII strings, and the strings concatenated with an ASCII literal.
        /// </summary>
        [Fact]
        public void ADomainsCharactersReachACallOverItsPath()
        {
            var facts = new JsonFactTheory(new[] { JsonFactRule.Unconditional(new JsonFact(A, new JsonClaim.OneOf(new JsonScalar[] { "ab", "c" }))) }).Derive(null);

            facts.CharactersOf(Call("||", Text(A), Lit("!"))).Should().Be(JsonCharacterBand.None);
        }

    }

}
