using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

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
    [TestClass]
    public class CosmosStoredFormsTests
    {

        /// <summary>
        /// A fixed width gives one spelling per value and a lexical order that is the numeric one.
        /// </summary>
        [TestMethod]
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
        [TestMethod]
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
        [TestMethod]
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
        [TestMethod]
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
        [TestMethod]
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
        [TestMethod]
        public void AnUnanchoredPatternIsRecognisedAsNothing()
        {
            CosmosStoredForms.Recognise("[0-9]{5}").Should().BeNull();
            CosmosStoredForms.Recognise("^[0-9]{5}").Should().BeNull();
            CosmosStoredForms.Recognise("[0-9]{5}$").Should().BeNull();
        }

        /// <summary>
        /// A literal is written into the container's own shape, and refused where it has none there.
        /// </summary>
        [TestMethod]
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
        [TestMethod]
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
        /// A form that stores something else is not a numeric one, however well declared.
        /// </summary>
        [TestMethod]
        public void ATemporalFormRendersNoInteger()
        {
            var iso = CosmosStoredForms.Recognise("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")!.Value;

            CosmosStoredForms.RenderInteger(iso, 42).Should().BeNull();
        }

    }

}
