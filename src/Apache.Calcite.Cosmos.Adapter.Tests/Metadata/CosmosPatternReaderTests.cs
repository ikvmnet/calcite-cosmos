using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// What a declared <c>pattern</c> says of the characters a string holds and of the strings it admits, read under both
    /// readings a validator may have given it: by UTF-16 unit, and by code point as the <c>u</c> flag reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The characters were checked beside these, while the reader was written, against V8's own <c>RegExp</c> with and
    /// without the flag: 36,000 generated patterns, each matched against some four thousand strings drawn from every band,
    /// and no band the reader called absent held by a string either reading matched, nor a pattern read that either
    /// reading rejects.
    /// </para>
    /// <para>
    /// The strings are what a stored form is recognised by (<c>CosmosStoredFormsSpellingTests</c>), and these hold them to
    /// the identities of the language they rest on.
    /// </para>
    /// </remarks>
    public class CosmosPatternReaderTests
    {

        static readonly JsonCharacterBand S = JsonCharacterBand.Surrogates;

        static readonly JsonCharacterBand H = JsonCharacterBand.HighBmp;

        static JsonCharacterBand[]? Absent(string pattern) => CosmosPatternReader.Absent(pattern)?.ToArray();

        static JsonCharacterBand[]? Absent(string pattern, bool unicode) => CosmosPatternReader.Absent(pattern, unicode)?.ToArray();

        /// <summary>
        /// The bands the theory says a path's stored strings hold no unit of, the pattern's claim of what was written the
        /// one thing stated of it, as <see cref="Absent(string)"/> lists them.
        /// </summary>
        static JsonCharacterBand[] Claimed(string pattern)
        {
            var at = JsonDocumentPath.Root.Property("s");
            var stored = new JsonFactTheory(CosmosPatternReader.Claims(pattern).Select(claim => JsonFactRule.Unconditional(new JsonFact(at, claim))).ToList())
                .Derive(null).CharactersOf(at);

            return new[] { S, H }.Where(band => (stored & band) == JsonCharacterBand.None).ToArray();
        }

        // ── What a declaration proves ─────────────────────────────────────────────────────────

        /// <remarks>
        /// A UUID in either case: hex digits and hyphens, so neither band, under either reading.
        /// </remarks>
        [Fact]
        public void AUuidHoldsNeitherBand()
        {
            const string uuid = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

            Absent(uuid, unicode: false).Should().Equal(S, H);
            Absent(uuid, unicode: true).Should().Equal(S, H);
            Claimed(uuid).Should().Equal(S, H);
        }

        /// <remarks>
        /// An identifier, the shape <c>id</c> is most often given: written with classes, with <c>\w</c>, which is ASCII
        /// in ECMA-262 under either reading, and as two anchored alternatives.
        /// </remarks>
        [Theory]
        [InlineData("^[A-Za-z0-9_-]+$")]
        [InlineData("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
        [InlineData("^\\w+(?:-\\w+)*$")]
        [InlineData("^[a-z]+$|^[0-9]+$")]
        [InlineData("^(?:order|invoice)-\\d{6}$")]
        public void AnIdentifierHoldsNeitherBand(string identifier)
        {
            Absent(identifier).Should().Equal(S, H);
            Claimed(identifier).Should().Equal(S, H);
        }

        /// <remarks>
        /// Every character below the surrogates, which is how text with no emoji and nothing from U+E000 is declared:
        /// the whole of the basic plane's letters, CJK included, and neither band.
        /// </remarks>
        [Fact]
        public void TheBasicPlaneBelowTheSurrogatesHoldsNeitherBand()
        {
            const string below = "^[\\u0000-\\uD7FF]*$";

            Absent(below, unicode: false).Should().Equal(S, H);
            Absent(below, unicode: true).Should().Equal(S, H);
            Claimed(below).Should().Equal(S, H);
        }

        /// <remarks>
        /// The pattern as written: a space matches a space, which is below U+D800 either way.
        /// </remarks>
        [Fact]
        public void ASpaceIsACharacter()
        {
            Claimed("^[a-z]+ [a-z]+$").Should().Equal(S, H);
        }

        // ── Bands, read by every character a span holds ───────────────────────────────────────

        /// <remarks>
        /// A supplementary character is two surrogate units read by unit and one code point past U+FFFF read by code
        /// point, and is band S either way, written raw or as two escapes. It proves nothing: a pattern admitting a
        /// surrogate says nothing of the high band, as the next test explains.
        /// </remarks>
        [Theory]
        [InlineData("^\U0001F600$")]
        [InlineData("^\\uD83D\\uDE00$")]
        [InlineData("^[a-z\U0001F600]+$")]
        public void ASupplementaryCharacterIsASurrogatePair(string pattern)
        {
            Absent(pattern, unicode: false).Should().Equal(H);
            Absent(pattern, unicode: true).Should().Equal(H);
            Claimed(pattern).Should().BeEmpty();
        }

        /// <remarks>
        /// Measured on the probe account, a lone surrogate written as a JSON escape is stored as U+FFFD, which is in the
        /// high band. A pattern admitting a surrogate unit admits a lone one read by unit, so what it admits as written
        /// lacks the high band and what the service stores may not.
        /// </remarks>
        [Fact]
        public void ASurrogateAdmittedIsAHighUnitStored()
        {
            Absent("^[a-z\\uD800-\\uDFFF]*$").Should().Equal(H);
            CosmosPatternReader.Claims("^[a-z\\uD800-\\uDFFF]*$").Should().Equal(new JsonClaim[] { new JsonClaim.Written(S) },
                "the reader states what was written, a surrogate and nothing from U+E000");
            Claimed("^[a-z\\uD800-\\uDFFF]*$").Should().BeEmpty("and the theory derives that what is stored may hold a high unit");
            Claimed("^[a-z\\uE000-\\uFFFF]*$").Should().Equal(new[] { S }, "one written without a surrogate is stored as it was written");
        }

        /// <remarks>
        /// A range's bands are those of every character it spans, not of its two ends: U+D000 and U+E100 are in neither
        /// band, and every surrogate and U+E000 lie between them. A range whose ends sit in the band below the surrogates
        /// holds nothing else.
        /// </remarks>
        [Fact]
        public void ARangeIsReadByEveryCharacterItSpans()
        {
            Absent("^[\\uD000-\\uE100]$").Should().BeEmpty();
            Absent("^[\\u0100-\\uD000]$").Should().Equal(S, H);
            Absent("^[\\uE000-\\uFFFF]+$").Should().Equal(S);
        }

        /// <remarks>
        /// A negated class is its complement, taken over each reading's own alphabet: everything to U+FFFF read by unit,
        /// everything to U+10FFFF read by code point. <c>[^\uE000-\uFFFF]</c> leaves the surrogates by unit, and the
        /// supplementary characters by code point, and the high band neither way; <c>[^\s]</c> is refused with
        /// <c>\s</c>.
        /// </remarks>
        [Fact]
        public void ANegatedClassIsItsComplement()
        {
            Absent("^[^\\uE000-\\uFFFF]*$", unicode: false).Should().Equal(H);
            Absent("^[^\\uE000-\\uFFFF]*$", unicode: true).Should().Equal(H);
            Claimed("^[^\\uE000-\\uFFFF]*$").Should().BeEmpty("it admits a surrogate, which may be stored as U+FFFD");

            Absent("^[^\\u0080-\\uFFFF]$", unicode: false).Should().Equal(S, H);
            Absent("^[^\\u0080-\\uFFFF]$", unicode: true).Should().Equal(new[] { H }, "read by code point it admits every supplementary character");

            Absent("^[^\\s]+$").Should().BeNull();
        }

        /// <remarks>
        /// <para>
        /// The two readings part, and the answer is what both allow. Excluding the surrogates excludes them read by unit;
        /// read by code point it excludes only lone ones, and admits every supplementary character, which is written in
        /// them.
        /// </para>
        /// <para>
        /// And two escapes naming a lead and a trail are one character read by code point: <c>\uDBFF\uDC00</c> is
        /// U+10FC00, so the class below is a range from U+D800 to U+10FC00 — the high band whole — where read by unit it is
        /// the surrogates and nothing else.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheReadingsPartAndTheAnswerIsWhatBothAllow()
        {
            Absent("^[^\\uD800-\\uDFFF]*$", unicode: false).Should().Equal(S);
            Absent("^[^\\uD800-\\uDFFF]*$", unicode: true).Should().BeEmpty();
            Absent("^[^\\uD800-\\uDFFF]*$").Should().BeEmpty();

            Absent("^[\\uD800-\\uDBFF\\uDC00-\\uDFFF]$", unicode: false).Should().Equal(H);
            Absent("^[\\uD800-\\uDBFF\\uDC00-\\uDFFF]$", unicode: true).Should().BeEmpty();
            Absent("^[\\uD800-\\uDBFF\\uDC00-\\uDFFF]$").Should().BeEmpty();
        }

        /// <remarks>
        /// A pattern only one reading accepts proves nothing: a validator using the other could not have checked a
        /// document with it. A range of two supplementary characters runs backwards read by unit; an identity escape of
        /// a letter, a lone bracket and a brace opening no count are literals read by unit and errors read by code point.
        /// </remarks>
        [Theory]
        [InlineData("^[\U0001F600-\U0001F64F]$")]
        [InlineData("^\\a$")]
        [InlineData("^a]$")]
        [InlineData("^a{,2}$")]
        [InlineData("^\\-$")]
        [InlineData("^[\\d-z]$")]
        public void APatternOneReadingRejectsProvesNothing(string pattern)
        {
            Absent(pattern).Should().BeNull();
            Claimed(pattern).Should().BeEmpty();
        }

        // ── What is refused ───────────────────────────────────────────────────────────────────

        /// <remarks>
        /// <c>\s</c> matches U+FEFF and the line and paragraph separators as well as the spaces, <c>\S</c>, <c>\D</c> and
        /// <c>\W</c> are complements of what they name, a property escape names what a Unicode version says, a
        /// back-reference repeats what a group matched, a lookaround and a named group and a flag are read differently,
        /// or not at all, by one reading, and <c>\u{…}</c> is a code point read by code point and a <c>u</c> repeated read
        /// by unit. None is read: each proves nothing.
        /// </remarks>
        [Theory]
        [InlineData("^\\s*$")]
        [InlineData("^.+$")]
        [InlineData("^\\S+$")]
        [InlineData("^\\D+$")]
        [InlineData("^\\W+$")]
        [InlineData("^\\p{L}+$")]
        [InlineData("^(a)\\1$")]
        [InlineData("^(?=a)a$")]
        [InlineData("^(?<n>a)$")]
        [InlineData("^(?i:a)$")]
        [InlineData("^\\u{1F600}$")]
        [InlineData("^\\k<n>$")]
        public void WhatBoundsNothingIsRefused(string pattern)
        {
            Absent(pattern).Should().BeNull();
            Claimed(pattern).Should().BeEmpty();
        }

        /// <remarks>
        /// JSON Schema's <c>pattern</c> searches: an alternative not anchored at both ends matches inside a string of
        /// anything. A group that may be passed over anchors nothing, and neither does a group one of whose branches is
        /// unanchored, or an empty one.
        /// </remarks>
        [Theory]
        [InlineData("[a-z]+")]
        [InlineData("^[a-z]+")]
        [InlineData("[a-z]+$")]
        [InlineData("^[a-z]+$|^x")]
        [InlineData("(?:^[a-z]+|[0-9]+)$")]
        [InlineData("(?:^[a-z]+)?[0-9]$")]
        [InlineData("^[a-z]+(?:$|x)")]
        [InlineData("(?:)^[a-z]+$")]
        [InlineData("")]
        public void AnUnanchoredAlternativeIsRefused(string pattern)
        {
            Absent(pattern).Should().BeNull();
            Strings(pattern).Should().BeNull();
        }

        /// <remarks>
        /// Anchoring is read off the structure, not off the first and last characters: every way through each of these
        /// asserts <c>^</c> before it consumes anything and <c>$</c> after it has consumed everything, which is all the
        /// atoms' characters need to bound the string's, and all a form needs to be the string. The nil-UUID alternation
        /// is published in the third shape.
        /// </remarks>
        [Theory]
        [InlineData("(?:^[a-z]+$)")]
        [InlineData("^(?:[a-z]+|[0-9]+)$")]
        [InlineData("(?:^[a-z]+$)|(?:^[0-9]+$)")]
        [InlineData("(?:^[a-z]+|^[0-9]+)$")]
        [InlineData("^(?:[a-z]+$|[0-9]+$)")]
        public void AnchoringInsideAGroupIsRead(string pattern)
        {
            Absent(pattern).Should().Equal(S, H);
            Strings(pattern).Should().NotBeNull();
        }

        // ── What a pattern admits, as strings ─────────────────────────────────────────────────

        static CosmosPatternStrings? Strings(string pattern) => CosmosPatternReader.Strings(pattern);

        /// <remarks>
        /// One language, written every way it commonly is and some it is not: <c>\d</c>, or the class, or its members in
        /// any order; the run counted, spelled out or split; a count of a group, and an alternation of single characters;
        /// a lazy count, a range of one count, a count with a leading zero; a run repeated no times; a group around the
        /// whole; the same alternative twice. Each rewrite is an identity of the language, and the reader reads them all
        /// as one, which is what a stored form is recognised by.
        /// </remarks>
        [Theory]
        [InlineData("^[0-9]{4}$")]
        [InlineData("^\\d{4}$")]
        [InlineData("^\\d\\d\\d\\d$")]
        [InlineData("^[0-9]{2}\\d{2}$")]
        [InlineData("^[0123456789]{4}$")]
        [InlineData("^[5-90-4]{4}$")]
        [InlineData("^(?:[0-9]{2}){2}$")]
        [InlineData("^((?:[0-4]|[5-9])){4}$")]
        [InlineData("^[0-9]{4}?$")]
        [InlineData("^[0-9]{4,4}$")]
        [InlineData("^[0-9]{04}$")]
        [InlineData("^x{0}[0-9]{2}x{0}[0-9]{2}$")]
        [InlineData("(?:^[0-9]{4}$)")]
        [InlineData("^[0-9]{4}$|^\\d{4}$")]
        public void OneLanguageIsReadAsOneWhateverItsSpelling(string pattern)
        {
            Strings(pattern).Should().NotBeNull("for " + pattern);
            Strings(pattern).Should().Be(Strings("^[0-9]{4}$"), "for " + pattern);
        }

        /// <remarks>
        /// A sequence's alternation is distributed across it, which the language does not see, and the alternatives are
        /// a set, written in whatever order.
        /// </remarks>
        [Fact]
        public void AnAlternationIsDistributedAcrossItsSequence()
        {
            var distributed = Strings("^ab$|^acd$");

            distributed.Should().NotBeNull();
            distributed!.Alternatives.Should().HaveCount(2);

            Strings("^a(?:b|cd)$").Should().Be(distributed);
            Strings("(?:^ab$)|(?:^acd$)").Should().Be(distributed);
            Strings("^(?:acd|ab)$").Should().Be(distributed);
        }

        /// <remarks>
        /// Languages that differ are read apart: by a count, a range of counts, one character more or fewer in a class,
        /// a space, which ECMA-262 matches as itself, and a run that may or may not be there.
        /// </remarks>
        [Theory]
        [InlineData("^[0-9]{5}$")]
        [InlineData("^[0-9]{3,4}$")]
        [InlineData("^[0-9]{4,}$")]
        [InlineData("^[0-8]{4}$")]
        [InlineData("^[0-9:]{4}$")]
        [InlineData("^[0-9]{4} $")]
        [InlineData("^ [0-9]{4}$")]
        [InlineData("^[0-9]{2} [0-9]{2}$")]
        [InlineData("^[0-9]{4}Z?$")]
        public void LanguagesThatDifferAreReadApart(string pattern)
        {
            Strings(pattern).Should().NotBeNull("for " + pattern);
            Strings(pattern).Should().NotBe(Strings("^[0-9]{4}$"), "for " + pattern);
        }

        /// <remarks>
        /// What is no union of sequences of runs has no strings, and is still read for its characters: a word boundary
        /// or a second anchor between the anchors, which narrows the language in a way no run says; a range of counts of
        /// a sequence; a count past the largest read; a class of surrogates, a pair read by unit and two lone surrogates
        /// read by code point, one sequence of classes and two languages; and two escapes naming a pair, two atoms read by
        /// unit and one read by code point.
        /// </remarks>
        [Theory]
        [InlineData("^a\\bb$", true)]
        [InlineData("^[a-z]{2}^$", true)]
        [InlineData("^(?:ab)*$", true)]
        [InlineData("^(?:ab|c)+$", true)]
        [InlineData("^a{100001}$", true)]
        [InlineData("^[\\uD800-\\uDFFF]{2}$", false)]
        [InlineData("^\\uD83D\\uDE00$", false)]
        public void WhatIsNoSequenceOfRunsHasNoStrings(string pattern, bool neitherBand)
        {
            Strings(pattern).Should().BeNull("for " + pattern);
            Absent(pattern).Should().NotBeNull("it is read for its characters, for " + pattern);

            if (neitherBand)
                Absent(pattern).Should().Equal(S, H);
        }

        // ── Read off a schema ─────────────────────────────────────────────────────────────────

        static JsonFactSet Facts(string schema) =>
            CosmosOracle.For(new CosmosContainerMetadata("items", new[] { "/pk" }).WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(schema)!)).Facts).Declared;

        static bool Lacks(JsonFactSet facts, string path, JsonCharacterBand band) =>
            (facts.CharactersOf(JsonDocumentPath.Root.Property(path)) & band) == JsonCharacterBand.None;

        /// <remarks>
        /// A pattern is read beside any type, or none: it constrains only a string, and so does what it says of the
        /// characters, which a number at the path satisfies.
        /// </remarks>
        [Fact]
        public void APatternIsReadBesideAnyType()
        {
            var facts = Facts("""{ "type": "object", "properties": { "a": { "type": "string", "pattern": "^[a-z]+$" }, "b": { "pattern": "^[a-z]+$" }, "c": { "type": "string" } } }""");

            Lacks(facts, "a", S).Should().BeTrue();
            Lacks(facts, "a", H).Should().BeTrue();
            Lacks(facts, "b", S).Should().BeTrue();
            Lacks(facts, "c", S).Should().BeFalse();
        }

        /// <remarks>
        /// The commonest spelling of an optional value is a union with <c>{"type": "null"}</c>, and a null holds no
        /// character; nor does a number, so a union of a declared string and a number keeps the claim too. A union with a
        /// string nothing constrains keeps nothing.
        /// </remarks>
        [Fact]
        public void AUnionKeepsWhatEveryBranchEntails()
        {
            var facts = Facts("""
                { "type": "object", "properties": {
                    "n": { "anyOf": [ { "type": "null" }, { "type": "string", "pattern": "^[a-z]+$" } ] },
                    "m": { "anyOf": [ { "type": "integer" }, { "type": "string", "pattern": "^[a-z]+$" } ] },
                    "h": { "anyOf": [ { "type": "string", "pattern": "^[\\u0000-\\uD7FF\\uE000-\\uFFFF]*$" }, { "type": "string", "pattern": "^[a-z]+$" } ] },
                    "x": { "anyOf": [ { "type": "string" }, { "type": "string", "pattern": "^[a-z]+$" } ] } } }
                """);

            Lacks(facts, "n", S).Should().BeTrue();
            Lacks(facts, "n", H).Should().BeTrue();
            Lacks(facts, "m", S).Should().BeTrue();
            Lacks(facts, "h", S).Should().BeTrue("both branches lack the surrogates");
            Lacks(facts, "h", H).Should().BeFalse("one branch admits the high band");
            Lacks(facts, "x", S).Should().BeFalse();
        }

        /// <remarks>
        /// The characters of <c>id</c> are declared the way any property's are, and a pattern under a <c>not</c> says
        /// nothing at all.
        /// </remarks>
        [Fact]
        public void AnIdIsDeclaredLikeAnyProperty()
        {
            Lacks(Facts("""{ "type": "object", "properties": { "id": { "type": "string", "pattern": "^[A-Za-z0-9_-]+$" } } }"""), "id", S).Should().BeTrue();
            Lacks(Facts("""{ "type": "object" }"""), "id", S).Should().BeFalse("Azure allows every character in an id but / and \\");
            Lacks(Facts("""{ "type": "object", "not": { "properties": { "id": { "pattern": "^[a-z]+$" } } } }"""), "id", S).Should().BeFalse();
        }

    }

}
