using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The numeric forms a declared <c>pattern</c> is recognised as, and what each one licenses.
    /// </summary>
    /// <remarks>
    /// <b>These are the forms that separate the two bits.</b> Calcite's cast reads <c>'042'</c> and
    /// <c>'42'</c> as one value, so a form admitting both spellings cannot have its equality lowered
    /// to a string equality; forbidding the leading zero gives one spelling per value and buys the
    /// equality alone, because the strings still vary in length and length dominates a lexical
    /// comparison. Only a fixed width buys the order with it. They are parametric rather than
    /// tabulated for the reason a width is: one family with a member per width.
    /// </remarks>
    public static class CosmosNumericForms
    {

        /// <summary>
        /// A non-negative integer written without padding, so every value has one spelling and no
        /// value has two.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Equality only, and the reason is the width.</b> One spelling per value is what equality
        /// needs, and forbidding a leading zero gives it. Ordering needs more than that and does not
        /// get it: the strings vary in length, and length dominates a lexical comparison, so
        /// <c>'9'</c> sorts after <c>'42'</c> while nine is less than forty-two.
        /// </para>
        /// <para>
        /// <b>Unsigned, and that is the same argument rather than a second one.</b> Calcite's cast
        /// reads <c>'-0'</c> as zero, so a form admitting a sign gives zero two spellings and stops
        /// being injective. Measured, along with the rest of what the cast accepts.
        /// </para>
        /// </remarks>
        public static readonly CosmosRepresentation IntegerUnpadded = new("integer-unpadded", PreservesEquality: true, PreservesOrder: false);

        /// <summary>
        /// A non-negative integer zero-padded to a fixed width, whose lexical order is numeric order.
        /// </summary>
        /// <remarks>
        /// Every value below the width's ceiling has exactly one spelling, so equality survives; and
        /// because the strings are all the same length, a lexical comparison compares digits at equal
        /// significance, which is what numeric comparison does. Both properties come from the width,
        /// which is why the form carries it.
        /// </remarks>
        /// <param name="width">How many digits every spelling has.</param>
        /// <returns>The form.</returns>
        public static CosmosRepresentation IntegerFixedWidth(int width) =>
            new($"integer-fixed-width-{width.ToString(CultureInfo.InvariantCulture)}", PreservesEquality: true, PreservesOrder: true, Width: width);

        /// <summary>
        /// Recognises <c>^[0-9]{n}$</c>, the zero-padded fixed-width integer.
        /// </summary>
        static readonly Regex FixedWidthInteger = new(@"^\^\[0-9\]\{([0-9]{1,2})\}\$$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Recognises the two ways a pattern forbids a leading zero.
        /// </summary>
        /// <remarks>
        /// <c>^(0|[1-9][0-9]*)$</c> admits zero and <c>^[1-9][0-9]*$</c> does not; both give one
        /// spelling per value, which is the only thing being asked. A bare <c>^[0-9]+$</c> is neither
        /// and is the trap this exists to distinguish from — it admits <c>42</c> and <c>042</c>
        /// alike, so comparing the stored strings would miss documents.
        /// </remarks>
        static readonly Regex UnpaddedInteger = new(@"^\^(?:\(0\|\[1-9\]\[0-9\]\*\)|\[1-9\]\[0-9\]\*)\$$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns the numeric form a normalised pattern is recognised as, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Parametric rather than tabulated: a fixed width is a family with one member per width, and
        /// no table would hold it. Both matchers require the anchors, because JSON Schema's
        /// <c>pattern</c> is a search rather than a full match — an unanchored <c>[0-9]{5}</c> is
        /// satisfied by <c>x12345y</c>, which constrains nothing this could rest on.
        /// </remarks>
        /// <param name="normalised">The pattern, already normalised.</param>
        /// <returns>The representation, or <c>null</c>.</returns>
        internal static CosmosRepresentation? Recognise(string normalised)
        {
            if (FixedWidthInteger.Match(normalised) is { Success: true } fixedWidth)
                return int.TryParse(fixedWidth.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var width) && width > 0
                    ? IntegerFixedWidth(width)
                    : null;

            if (UnpaddedInteger.IsMatch(normalised))
                return IntegerUnpadded;

            return null;
        }

        /// <summary>
        /// Writes an integer the way a path in this form stores it, or returns <c>null</c> where the
        /// form does not store one or the value has no spelling in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Refusing is the interesting half.</b> A negative value has no spelling in an unsigned
        /// form, and a value with more digits than the width has none in a padded one — so neither is
        /// rendered as something close. A comparison against a value the container cannot hold is the
        /// caller's to make and the rewrite's to decline, because writing <c>'100000'</c> where every
        /// stored string is five characters would compare strings of different lengths and answer by
        /// length rather than by value.
        /// </para>
        /// <para>
        /// The unpadded form writes the digits as they stand, having no width to pad to.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path form.</param>
        /// <param name="value">The value to write.</param>
        /// <returns>The stored spelling, or <c>null</c>.</returns>
        public static string? Render(CosmosRepresentation representation, long value)
        {
            if (representation.PreservesEquality == false || value < 0)
                return null;

            var digits = value.ToString(CultureInfo.InvariantCulture);

            if (representation.Width is not int width)
                return string.Equals(representation.Name, IntegerUnpadded.Name, StringComparison.Ordinal) ? digits : null;

            if (string.Equals(representation.Name, IntegerFixedWidth(width).Name, StringComparison.Ordinal) == false)
                return null;

            return digits.Length <= width ? digits.PadLeft(width, '0') : null;
        }

    }

}
