using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Which patterns say a stored string spells a number faithfully, and what each one licenses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The measurement everything here rests on: Calcite's cast reads <c>'042'</c>, <c>'0042'</c>,
    /// <c>'+42'</c>, <c>' 42'</c>, <c>'42 '</c> and <c>'42'</c> all as forty-two, and <c>'-0'</c> as
    /// zero. So a value has as many spellings as the pattern admits, and a string equality answers
    /// what a numeric one answers only where the pattern admits one.
    /// </para>
    /// <para>
    /// Ordering asks the stronger question. A lexical comparison compares the first differing
    /// character, which compares digits at equal significance only when the strings are the same
    /// length — so padding to a fixed width buys the ordering and forbidding padding does not.
    /// </para>
    /// </remarks>
    public class CosmosStoredFormsTests
    {

        /// <summary>
        /// A fixed width gives one spelling per value and a lexical order that is the numeric one.
        /// </summary>
        [Fact]
        public void AFixedWidthIsBothFaithfulAndSortable()
        {
            var form = CosmosStoredForms.Recognise("^[0-9]{5}$");

            form.Should().NotBeNull();
            form!.Value.PreservesEquality.Should().BeTrue();
            form.Value.PreservesOrder.Should().BeTrue("equal-length digit strings compare digit by digit at equal significance");
            form.Value.Width.Should().Be(5, "which is what lets a literal be written into the container's own shape");
        }

        /// <summary>
        /// Written with <c>\d</c> it is the same pattern, which normalisation already knew.
        /// </summary>
        [Fact]
        public void TheDigitShorthandIsTheSamePattern()
        {
            CosmosStoredForms.Recognise(@"^\d{5}$").Should().Be(CosmosStoredForms.Recognise("^[0-9]{5}$"));
        }

        /// <summary>
        /// Forbidding a leading zero gives one spelling per value and nothing more.
        /// </summary>
        /// <remarks>
        /// Both spellings of the constraint are recognised — one admitting zero and one not — because
        /// the only thing being asked is whether a value has two spellings, and neither lets it.
        /// </remarks>
        [Fact]
        public void ForbiddingTheLeadingZeroBuysEqualityAndNotOrder()
        {
            foreach (var pattern in new[] { "^(0|[1-9][0-9]*)$", "^[1-9][0-9]*$" })
            {
                var form = CosmosStoredForms.Recognise(pattern);

                form.Should().NotBeNull("for " + pattern);
                form!.Value.PreservesEquality.Should().BeTrue("for " + pattern);
                form.Value.PreservesOrder.Should().BeFalse("'9' sorts after '42' while nine is less than forty-two");
            }
        }

        /// <summary>
        /// The trap, and the reason the two forms above are spelled out rather than approximated.
        /// </summary>
        /// <remarks>
        /// <c>^[0-9]+$</c> looks like the obvious way to say "a number", and it admits <c>42</c> and
        /// <c>042</c> alike. Reading it as faithful would push a string equality that misses every
        /// document written the other way, which is the one failure mode the whole model exists to
        /// avoid.
        /// </remarks>
        [Fact]
        public void AnyRunOfDigitsIsNotFaithfulAndIsRecognisedAsNothing()
        {
            CosmosStoredForms.Recognise("^[0-9]+$").Should().BeNull();
            CosmosStoredForms.Recognise(@"^\d+$").Should().BeNull();
            CosmosStoredForms.Recognise("^[0-9]*$").Should().BeNull();
        }

        /// <summary>
        /// A sign is refused for the same reason the padding is, rather than for a separate one.
        /// </summary>
        /// <remarks>
        /// Calcite reads <c>'-0'</c> as zero, so a form admitting a sign gives zero two spellings and
        /// stops being injective before ordering is even asked about.
        /// </remarks>
        [Fact]
        public void ASignedPatternIsRecognisedAsNothing()
        {
            CosmosStoredForms.Recognise("^-?[0-9]{5}$").Should().BeNull();
            CosmosStoredForms.Recognise("^[+-][0-9]{5}$").Should().BeNull();
        }

        /// <summary>
        /// An unanchored pattern constrains nothing, because JSON Schema searches rather than matches.
        /// </summary>
        /// <remarks>
        /// <c>[0-9]{5}</c> is satisfied by <c>x12345y</c>. Treating it as a fixed width would be a
        /// claim about documents the schema never made.
        /// </remarks>
        [Fact]
        public void AnUnanchoredPatternIsRecognisedAsNothing()
        {
            CosmosStoredForms.Recognise("[0-9]{5}").Should().BeNull();
            CosmosStoredForms.Recognise("^[0-9]{5}").Should().BeNull();
            CosmosStoredForms.Recognise("[0-9]{5}$").Should().BeNull();
        }

        /// <summary>
        /// A literal is written into the container's own shape, and refused where it has none there.
        /// </summary>
        [Fact]
        public void RenderingPadsToTheDeclaredWidth()
        {
            var form = CosmosStoredForms.Recognise("^[0-9]{5}$")!.Value;

            CosmosStoredForms.RenderInteger(form, 42).Should().Be("00042");
            CosmosStoredForms.RenderInteger(form, 0).Should().Be("00000");
            CosmosStoredForms.RenderInteger(form, 99999).Should().Be("99999");
        }

        /// <summary>
        /// Refusing is the interesting half.
        /// </summary>
        /// <remarks>
        /// A value wider than the container's spelling has no stored form at all, and writing
        /// <c>'100000'</c> beside five-character strings would compare by length rather than by
        /// value. A negative has none either, the form being unsigned.
        /// </remarks>
        [Fact]
        public void AValueWithNoSpellingInTheFormIsRefused()
        {
            var padded = CosmosStoredForms.Recognise("^[0-9]{5}$")!.Value;

            CosmosStoredForms.RenderInteger(padded, 100000).Should().BeNull("six digits do not fit a five-character spelling");
            CosmosStoredForms.RenderInteger(padded, -1).Should().BeNull("the form is unsigned");

            var unpadded = CosmosStoredForms.Recognise("^(0|[1-9][0-9]*)$")!.Value;

            CosmosStoredForms.RenderInteger(unpadded, 42).Should().Be("42", "with no width there is nothing to pad to");
            CosmosStoredForms.RenderInteger(unpadded, -1).Should().BeNull();
        }

        /// <summary>
        /// A UUID pattern is read as a shape rather than matched against a table, so a confinement
        /// nobody enumerated still states everything the shape states.
        /// </summary>
        /// <remarks>
        /// Each of these was unrecognised while the rows were enumerated, and unrecognised meant the
        /// path stated <em>nothing</em> — so a comparison against it read whole documents rather than
        /// merely sorting in process. The version nibble in particular: <c>[1-5]</c> is what a schema
        /// written against RFC 4122 pins, and it was outside a table that knew <c>[1-8]</c>.
        /// </remarks>
        [Fact]
        public void AConfinementNobodyEnumeratedIsStillACanonicalUuid()
        {
            foreach (var pattern in new[]
            {
                // The version nibble, pinned to the five RFC 4122 defined, to a pair, or to a range
                // nobody wrote a row for.
                "^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                "^[0-9a-f]{8}-[0-9a-f]{4}-[45][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                "^[0-9a-f]{8}-[0-9a-f]{4}-[1-7][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",

                // The variant nibble written as ranges rather than as four members, which is the
                // same set three ways.
                "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[8-9a-b][0-9a-f]{3}-[0-9a-f]{12}$",
                "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89a-b][0-9a-f]{3}-[0-9a-f]{12}$",
                "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[8-9ab][0-9a-f]{3}-[0-9a-f]{12}$",

                // The hex class spelled out, and written as two ranges the other way round.
                "^[0123456789abcdef]{8}-[0123456789abcdef]{4}-[0123456789abcdef]{4}-[0123456789abcdef]{4}-[0123456789abcdef]{12}$",
                "^[0-9abcdef]{8}-[0-9abcdef]{4}-[0-9abcdef]{4}-[0-9abcdef]{4}-[0-9abcdef]{12}$",
                "^[a-cd-f0-9]{8}-[a-cd-f0-9]{4}-[a-cd-f0-9]{4}-[a-cd-f0-9]{4}-[a-cd-f0-9]{12}$",

                // The high half confined to the negative side, which is sign-constant too and which
                // the plain row carries under the unsigned comparison.
                "^[89abcdef][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
            })
            {
                var form = CosmosStoredForms.Recognise(pattern);

                form.Should().Be(CosmosStoredForms.UuidCanonicalLower, "for " + pattern);
            }
        }

        /// <summary>
        /// A container that pins more than the first digit is confined by that much more, and is
        /// sortable on the argument the first digit alone was making.
        /// </summary>
        /// <remarks>
        /// Confining a slot only removes strings, so a narrower pattern inherits every relation the
        /// widest one preserves. A pinned prefix is what a container generating v7 identifiers within
        /// a known epoch declares, and it was outside the enumerated rows.
        /// </remarks>
        [Fact]
        public void PinningMoreThanTheFirstDigitIsStillConfined()
        {
            foreach (var pattern in new[]
            {
                "^0[0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                "^01[0-9a-f]{6}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                "^[0-7][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-a[0-9a-f]{3}-[0-9a-f]{12}$",
            })
                CosmosStoredForms.Recognise(pattern).Should().Be(CosmosStoredForms.UuidCanonicalLowerSortable, "for " + pattern);
        }

        /// <summary>
        /// A range in a character class is over code points and not over hex digits, which is a trap
        /// rather than a spelling.
        /// </summary>
        /// <remarks>
        /// <c>[8-f]</c> reads as though it meant the eight digits from <c>8</c> to <c>f</c> and spans
        /// <c>0x38</c>–<c>0x66</c>, so it admits <c>@</c>, <c>Z</c> and <c>_</c> — and admits
        /// <c>A</c>–<c>F</c> beside <c>a</c>–<c>f</c>, which is two spellings of one nibble. So every
        /// member of a range is asked rather than its two ends, and <c>[0-;]</c> is refused on the
        /// same test although it admits no letter at all to give the case away.
        /// </remarks>
        [Fact]
        public void ARangeOverCodePointsIsNotASetOfNibbles()
        {
            foreach (var pattern in new[]
            {
                "^[8-f][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                "^[0-;][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                "^[0-f]{8}-[0-f]{4}-[0-f]{4}-[0-f]{4}-[0-f]{12}$",
            })
                CosmosStoredForms.Recognise(pattern).Should().BeNull("for " + pattern);
        }

        /// <summary>
        /// An alternation is the union of its branches, which is what the nil-UUID spelling needs and
        /// what it used to get from a row written by hand.
        /// </summary>
        /// <remarks>
        /// The nil value's variant nibble is <c>0</c> rather than <c>8</c>–<c>b</c>, so the union's
        /// 17th slot is <c>[089ab]</c> and the pattern is no longer confined — which is the answer the
        /// enumerated row gave, now derived rather than asserted. The three spellings differ only in
        /// where the anchors sit, which is the axis a table had no way to cover.
        /// </remarks>
        [Fact]
        public void AnAlternationIsTheUnionOfItsBranches()
        {
            const string Body = "[0-7][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}";
            const string Nil = "00000000-0000-0000-0000-000000000000";

            CosmosStoredForms.Recognise($"^{Body}$")
                .Should().Be(CosmosStoredForms.UuidCanonicalLowerSortable, "the branch on its own is confined");

            foreach (var pattern in new[]
            {
                $"(?:^{Body}$)|(?:^{Nil}$)",
                $"^{Body}$|^{Nil}$",
                $"^(?:{Body}|{Nil})$",
                $"^({Body}|{Nil})$",
            })
                CosmosStoredForms.Recognise(pattern).Should().Be(CosmosStoredForms.UuidCanonicalLower,
                    "the nil value's variant nibble is 0, which widens the union past the confinement, for " + pattern);
        }

        /// <summary>
        /// A branch that is not anchored at both ends is not a canonical UUID, however the other
        /// branch reads.
        /// </summary>
        /// <remarks>
        /// A top-level <c>|</c> binds looser than the anchors, so <c>^a|b$</c> is <c>(^a)|(b$)</c> and
        /// its second branch admits a conforming value with anything in front of it — which is the
        /// same objection an unanchored pattern gets, one branch down.
        /// </remarks>
        [Fact]
        public void AHalfAnchoredBranchIsRecognisedAsNothing()
        {
            const string Body = "[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}";

            CosmosStoredForms.Recognise($"^{Body}|{Body}$").Should().BeNull();
            CosmosStoredForms.Recognise($"^{Body}$|{Body}").Should().BeNull();
        }

        /// <summary>
        /// A group this cannot read through is refused rather than stripped.
        /// </summary>
        /// <remarks>
        /// An inline flag is the one that matters: <c>(?i)</c> makes the whole pattern
        /// case-insensitive, so a recogniser that took the group off would read a two-spellings-per-value
        /// language as a canonical one. A lookahead and a named group are refused with it, on the
        /// grounds that reading neither is cheaper than reading both wrongly.
        /// </remarks>
        [Fact]
        public void AGroupThisCannotReadThroughIsRefused()
        {
            const string Body = "[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}";

            CosmosStoredForms.Recognise($"(?i)^{Body}$").Should().BeNull();
            CosmosStoredForms.Recognise($"^(?=.)({Body})$").Should().BeNull();
            CosmosStoredForms.Recognise($"^(?<uuid>{Body})$").Should().BeNull();
        }

        /// <summary>
        /// The brace and the hyphen are axes of the <em>shape</em>, and a shape costs neither relation.
        /// </summary>
        /// <remarks>
        /// A brace is in the same place in every stored string and the hyphens are in the same places
        /// or in none, so neither ever decides a comparison: the strings still compare nibble by
        /// nibble in significance order and one value still has one spelling. So all four shapes carry
        /// the two bits the canonical one carries, and what the shape decides is only how a literal is
        /// written into the container.
        /// </remarks>
        [Fact]
        public void TheBraceAndTheHyphenAreAxesOfTheShapeAndCostNoRelation()
        {
            const string Groups = "[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}";
            const string Run = "[0-9a-f]{4}7[0-9a-f]{3}[89ab][0-9a-f]{3}[0-9a-f]{12}";

            var value = Guid.Parse("0123456f-89ab-7cde-8f01-23456789abcd");

            var shapes = new (string Pattern, string Name, string Stored)[]
            {
                ($"^{Groups}$", "uuid-canonical-lower", "0123456f-89ab-7cde-8f01-23456789abcd"),
                ($@"^\{{{Groups}\}}$", "uuid-braced-lower", "{0123456f-89ab-7cde-8f01-23456789abcd}"),
                ($"^[0-9a-f]{{8}}{Run}$", "uuid-hyphenless-lower", "0123456f89ab7cde8f0123456789abcd"),
                ($@"^\{{[0-9a-f]{{8}}{Run}\}}$", "uuid-braced-hyphenless-lower", "{0123456f89ab7cde8f0123456789abcd}"),

                // The brace written without its escape, which is a literal in an ECMA-262 pattern
                // because nothing precedes it for a count to apply to.
                ($"^{{{Groups}}}$", "uuid-braced-lower", "{0123456f-89ab-7cde-8f01-23456789abcd}"),

                // And confined, which the shape does not touch either way.
                ($"^[0-7]{Groups.Substring("[0-9a-f]".Length)}$", "uuid-canonical-lower-sortable", "0123456f-89ab-7cde-8f01-23456789abcd"),
                ($@"^\{{[0-7]{Groups.Substring("[0-9a-f]".Length)}\}}$", "uuid-braced-lower-sortable", "{0123456f-89ab-7cde-8f01-23456789abcd}"),
            };

            foreach (var (pattern, name, stored) in shapes)
            {
                var form = CosmosStoredForms.Recognise(pattern);

                form.Should().NotBeNull("for " + pattern);
                form!.Value.Name.Should().Be(name, "for " + pattern);
                form.Value.PreservesEquality.Should().BeTrue("one value still has one spelling, for " + pattern);
                form.Value.PreservesOrder.Should().BeTrue("and the fixed characters never decide a comparison, for " + pattern);

                CosmosStoredForms.IsUuid(form.Value).Should().BeTrue("the reader takes every one of these, for " + pattern);
                CosmosStoredForms.RenderUuid(form.Value, value).Should().Be(stored, "for " + pattern);
            }
        }

        /// <summary>
        /// The uppercase shapes are spelled and rendered on the same terms.
        /// </summary>
        [Fact]
        public void TheShapesCarryTheCaseAsTheCanonicalOneDoes()
        {
            var value = Guid.Parse("0123456f-89ab-7cde-8f01-23456789abcd");

            var upper = CosmosStoredForms.Recognise(@"^\{[0-9A-F]{8}[0-9A-F]{4}7[0-9A-F]{3}[89AB][0-9A-F]{3}[0-9A-F]{12}\}$");

            upper.Should().NotBeNull();
            upper!.Value.Name.Should().Be("uuid-braced-hyphenless-upper");

            CosmosStoredForms.RenderUuid(upper.Value, value).Should().Be("{0123456F89AB7CDE8F0123456789ABCD}");
        }

        /// <summary>
        /// A shape that is none of the four is recognised as nothing.
        /// </summary>
        /// <remarks>
        /// The <c>urn:uuid:</c> and parenthesised spellings parse to the same value and are
        /// string-equal to none of the others, so each would be a form of its own — and neither is
        /// one, for a reason measured rather than chosen: <c>SqlFunctions.stringToUuid</c> refuses
        /// both, so a projection reading one back would raise where the four shapes read. Half a
        /// brace and some of the four hyphens are refused on the older argument, that they give one
        /// value more than one spelling or name a place a canonical UUID has no hyphen in.
        /// </remarks>
        [Fact]
        public void AShapeThatIsNoneOfTheFourIsRecognisedAsNothing()
        {
            foreach (var pattern in new[]
            {
                // The two spellings the reader refuses.
                "^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
                @"^\([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)$",

                // Half a brace.
                @"^\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",

                // The hyphens in the wrong places, one group too few, and some of the four.
                "^[0-9a-f]{4}-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
                "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
                "^[0-9a-f]{8}-[0-9a-f]{4}[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",

                // A hyphen that may or may not be there, which is two spellings of one value.
                "^[0-9a-f]{8}-?[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            })
                CosmosStoredForms.Recognise(pattern).Should().BeNull("for " + pattern);
        }

        /// <summary>
        /// An alternation whose branches disagree on the shape gives one value two spellings.
        /// </summary>
        /// <remarks>
        /// Each branch is a canonical UUID on its own and the union of them is not a form, because a
        /// comparison would have to render the literal both ways to select the documents written
        /// either way. The slots union; the shape has to agree.
        /// </remarks>
        [Fact]
        public void BranchesThatDisagreeOnTheShapeAreRecognisedAsNothing()
        {
            const string Groups = "[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}";
            const string Run = "[0-9a-f]{8}[0-9a-f]{4}4[0-9a-f]{3}[89ab][0-9a-f]{3}[0-9a-f]{12}";

            CosmosStoredForms.Recognise($"^{Groups}$|^{Run}$").Should().BeNull("hyphenated beside hyphenless");
            CosmosStoredForms.Recognise($@"^{Groups}$|^\{{{Groups}\}}$").Should().BeNull("braced beside bare");
        }

        /// <summary>
        /// A literal outside a confined path's own sign class is still rendered, because under the
        /// unsigned comparison the sign decides nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The confined rows exist for the comparison <c>calcite.uuid.unsigned.comparison</c> turns
        /// off, and under <em>that</em> comparison a literal on the other side of the sign is the one
        /// case the confinement does not cover: every stored value is signed-positive, so
        /// <c>&gt;</c> against a signed-negative literal is true of every document and lexically true
        /// of none. <c>RenderUuid</c> refuses there.
        /// </para>
        /// <para>
        /// This asserts the other half, which is the half this process can see: the property defaults
        /// to on — measured in <c>CalciteUuidOrderingMeasurementTests</c> — and under it the literal
        /// is rendered whatever its sign, so an equality against a value the container cannot hold
        /// still pushes a predicate that correctly selects nothing. The refusing branch needs the
        /// property off before Calcite is loaded, which is a process rather than a test.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnOutOfClassLiteralIsRenderedUnderTheUnsignedComparison()
        {
            var confined = CosmosStoredForms.Recognise("^[0-7][0-9a-f]{7}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")!.Value;

            confined.Should().Be(CosmosStoredForms.UuidCanonicalLowerSortable);

            CosmosStoredForms.RenderUuid(confined, Guid.Parse("f0000000-0000-7000-a000-000000000000"))
                .Should().Be("f0000000-0000-7000-a000-000000000000", "the first digit is outside [0-7] and the unsigned order does not care");

            CosmosStoredForms.RenderUuid(confined, Guid.Parse("00000000-0000-0000-0000-000000000000"))
                .Should().Be("00000000-0000-0000-0000-000000000000", "and so is the variant nibble of the nil value");
        }

        /// <summary>
        /// A form that stores something else is not a numeric one, however well declared.
        /// </summary>
        [Fact]
        public void ATemporalFormRendersNoInteger()
        {
            var iso = CosmosStoredForms.Recognise("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")!.Value;

            CosmosStoredForms.RenderInteger(iso, 42).Should().BeNull();
        }

    }

}
