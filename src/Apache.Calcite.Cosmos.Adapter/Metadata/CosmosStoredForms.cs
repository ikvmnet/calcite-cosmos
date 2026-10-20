using System;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The stored forms a declared <c>pattern</c> is recognised as, and what each one licenses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Recognition, not inference.</b> The tempting design is to derive the form by probing — take a
    /// canonical sample and an uppercased one, run the declared pattern against both, and conclude the
    /// stored form from which is accepted. That is evidence and not proof. The conclusion needed is
    /// universal, that <em>every</em> string the pattern accepts is canonical, and two samples say
    /// nothing about the rest: a pattern with one group left case-insensitive accepts the lowercase
    /// sample, rejects the fully uppercased one, and still admits <c>123e4567-E89B-12d3-…</c>. A
    /// document storing that conforms to the declared schema and would be dropped by the equality the
    /// probe licensed — a wrong answer from a conforming document. An unanchored pattern fails the same
    /// way. So a pattern yields a form only by being read as a language whose every string is
    /// canonical, which is sound by construction.
    /// </para>
    /// <para>
    /// <b><c>format</c> yields nothing on its own.</b> JSON Schema calls it an annotation rather than an
    /// assertion, and RFC 9562 relaxed the lowercase-output rule RFC 4122 §3 had, so
    /// <c>format: uuid</c> does not say what is stored. The pattern beside it does.
    /// </para>
    /// <para>
    /// <b>This is the entry point and not the argument.</b> A family decides its own forms and writes
    /// its own literals, and the three differ in kind rather than in detail: the temporal shapes are
    /// tabulated because their axes close, <see cref="CosmosUuidForms"/> decides a shape because a
    /// UUID pattern's axes do not, and the numeric forms are parametric because a width is a family
    /// rather than a row. What is left here is the dispatch, which is the one thing a caller holding a
    /// bare <c>pattern</c> cannot do for itself.
    /// </para>
    /// <para>
    /// <b>No family reads the pattern.</b> The pattern is read once, by the one reader of the keyword
    /// (<see cref="CosmosPatternReader"/>), into the strings it admits under both of the readings a validator may
    /// give it, and every family is asked of those strings: a form is recognised where they are its language, however
    /// the pattern was spelled. The same reader says what characters the strings hold, and neither question is asked
    /// of the pattern's text.
    /// </para>
    /// </remarks>
    public static class CosmosStoredForms
    {

        /// <summary>
        /// Returns the stored form a declared pattern is recognised as, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The pattern is read once and its strings offered to each family in turn. A pattern the reader refuses, or
        /// whose two readings read different strings, is no form.
        /// </para>
        /// <para>
        /// <b>The order decides two languages.</b> Eight digits, <c>^[0-9]{8}$</c>, is a basic calendar date and a fixed
        /// width of digits alike, and is read as the date; thirty-two, <c>^[0-9]{32}$</c>, is a hyphenless UUID with no
        /// letter in it and a fixed width alike, and is read as the UUID. Both are what they were always read as, and
        /// either form proves both relations. The families are otherwise disjoint, a canonical UUID being no temporal
        /// shape and neither being a run of digits.
        /// </para>
        /// </remarks>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c>.</param>
        /// <returns>The representation, or <c>null</c> where the pattern is not one this knows.</returns>
        public static CosmosRepresentation? Recognise(string? pattern)
        {
            if (CosmosPatternReader.Strings(pattern) is not { } strings)
                return null;

            return CosmosTemporalForms.Recognise(strings)
                ?? CosmosUuidForms.Recognise(strings)
                ?? CosmosNumericForms.Recognise(strings);
        }

        /// <summary>
        /// Returns the strings a form's defining pattern admits, read by the reader every declared pattern is read by.
        /// </summary>
        /// <remarks>
        /// A form defined by a pattern is defined by its language, and the definition is read as a declaration is, so
        /// that a declaration is the form exactly where it admits what the definition does. A definition the reader
        /// cannot read is a defect in the definition, and fails as one.
        /// </remarks>
        /// <param name="pattern">The pattern a form is defined by.</param>
        /// <returns>Its strings.</returns>
        internal static CosmosPatternStrings Definition(string pattern) =>
            CosmosPatternReader.Strings(pattern) ?? throw new InvalidOperationException($"The pattern '{pattern}' defining a stored form is not one the reader reads.");

        /// <summary>
        /// Gets every stored form a pattern can be recognised as, a numeric form at each width a pattern can name.
        /// </summary>
        /// <remarks>
        /// <b>A hook for the tests, and read by nothing else.</b> What <c>CosmosStoredFormsTests</c> holds to the claim
        /// the service's order rests on: every stored form is ASCII, as <see cref="CharactersOf"/> reads it off the form's
        /// definition, so a path in one holds no character at or above U+D800, where code point and UTF-16 order part — a
        /// rule of the theory reads that off the form. The tables it enumerates are the recognisers' own, so a form added
        /// to one is held to the claim with no list to keep beside it.
        /// </remarks>
        internal static System.Collections.Generic.IEnumerable<CosmosRepresentation> Registered =>
            System.Linq.Enumerable.Concat(System.Linq.Enumerable.Concat(CosmosTemporalForms.Registered, CosmosUuidForms.Registered), CosmosNumericForms.Registered);

        /// <summary>
        /// Returns the classes a form's strings hold characters of, read off what defines the form —
        /// a language the one reader of a pattern parsed, or a shape's alphabet — by the reader's own reading of a
        /// range's bands; <see cref="Facts.JsonCharacterBand.Any"/> for a form no family defines, which says nothing.
        /// </summary>
        internal static Facts.JsonCharacterBand CharactersOf(CosmosRepresentation representation) =>
            CosmosTemporalForms.CharactersOf(representation)
                ?? CosmosUuidForms.CharactersOf(representation)
                ?? CosmosNumericForms.CharactersOf(representation)
                ?? Facts.JsonCharacterBand.Any;

        /// <inheritdoc cref="CosmosUuidForms.Render" />
        public static string? RenderUuid(CosmosRepresentation representation, Guid value) =>
            CosmosUuidForms.Render(representation, value);

        /// <inheritdoc cref="CosmosTemporalForms.Render(CosmosRepresentation, DateTime)" />
        public static string? RenderDateTime(CosmosRepresentation representation, DateTime value) =>
            CosmosTemporalForms.Render(representation, value);

        /// <inheritdoc cref="CosmosTemporalForms.Render(CosmosRepresentation, DateTime, CosmosTemporalRounding)" />
        public static string? RenderDateTime(CosmosRepresentation representation, DateTime value, CosmosTemporalRounding rounding) =>
            CosmosTemporalForms.Render(representation, value, rounding);

        /// <inheritdoc cref="CosmosNumericForms.Render" />
        public static string? RenderInteger(CosmosRepresentation representation, long value) =>
            CosmosNumericForms.Render(representation, value);

        /// <inheritdoc cref="CosmosTemporalForms.ParsesExactly" />
        public static bool ParsesExactly(CosmosRepresentation representation, string? format, CosmosTemporalParts held) =>
            CosmosTemporalForms.ParsesExactly(representation, format, held);

        /// <inheritdoc cref="CosmosTemporalForms.ReadsBackAs" />
        public static bool ReadsBackAs(CosmosRepresentation representation, CosmosTemporalParts held) =>
            CosmosTemporalForms.ReadsBackAs(representation, held);

        /// <inheritdoc cref="CosmosTemporalForms.EngineReads" />
        public static bool EngineReads(CosmosRepresentation representation, CosmosTemporalParts target) =>
            CosmosTemporalForms.EngineReads(representation, target);

        /// <summary>
        /// Determines whether a conversion of a form's stored text reads it as the value it spells: a parse by its
        /// format, a cast to an instant where the engine reads the form into that type, and a conversion naming
        /// neither — a cast to a UUID or to a number — as the engine always does.
        /// </summary>
        /// <remarks>
        /// What an equality and an order through the form both need, so asked once: comparing the stored strings in
        /// place of a conversion the engine would have failed at answers rows where the query answers an error, which
        /// is a different query rather than a faster one.
        /// </remarks>
        /// <param name="representation">The path's declared form.</param>
        /// <param name="format">The parse's format, or <c>null</c> for a cast.</param>
        /// <param name="held">The halves of an instant the conversion's value holds; none for a UUID or a number.</param>
        /// <returns><c>true</c> where the conversion reads the form.</returns>
        public static bool Reads(CosmosRepresentation representation, string? format, CosmosTemporalParts held) =>
            format is not null
                ? ParsesExactly(representation, format, held)
                : held == CosmosTemporalParts.None || EngineReads(representation, held);

        /// <inheritdoc cref="CosmosTemporalForms.ParseFormats" />
        public static System.Collections.Generic.IReadOnlyCollection<string> ParseFormats(CosmosRepresentation representation) =>
            CosmosTemporalForms.ParseFormats(representation);

    }

}
