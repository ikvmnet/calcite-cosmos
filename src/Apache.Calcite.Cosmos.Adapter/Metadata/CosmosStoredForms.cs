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
    /// <b>This is the entry point and not the argument.</b> A family reads its own patterns and writes
    /// its own literals, and the three differ in kind rather than in detail: the temporal shapes are
    /// tabulated because their axes close, <see cref="CosmosUuidForms"/> decides a shape because a
    /// UUID pattern's axes do not, and the numeric forms are parametric because a width is a family
    /// rather than a row. What is left here is the dispatch, which is the one thing a caller holding a
    /// bare <c>pattern</c> cannot do for itself.
    /// </para>
    /// </remarks>
    public static class CosmosStoredForms
    {

        /// <summary>
        /// Returns the stored form a declared pattern is recognised as, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The pattern is normalised once and offered to each family in turn. The order decides
        /// nothing — the three languages are disjoint, a canonical UUID being no temporal shape and
        /// neither being a run of digits — so it is the order they are cheapest in.
        /// </remarks>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c>.</param>
        /// <returns>The representation, or <c>null</c> where the pattern is not one this knows.</returns>
        public static CosmosRepresentation? Recognise(string? pattern)
        {
            if (CosmosPatternLanguage.Normalise(pattern) is not string normalised)
                return null;

            return CosmosTemporalForms.Recognise(normalised)
                ?? CosmosUuidForms.Recognise(normalised)
                ?? CosmosNumericForms.Recognise(normalised);
        }

        /// <inheritdoc cref="CosmosUuidForms.Render" />
        public static string? RenderUuid(CosmosRepresentation representation, Guid value) =>
            CosmosUuidForms.Render(representation, value);

        /// <inheritdoc cref="CosmosTemporalForms.Render" />
        public static string? RenderDateTime(CosmosRepresentation representation, DateTime value) =>
            CosmosTemporalForms.Render(representation, value);

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

        /// <inheritdoc cref="CosmosTemporalForms.ParseFormats" />
        public static System.Collections.Generic.IReadOnlyCollection<string> ParseFormats(CosmosRepresentation representation) =>
            CosmosTemporalForms.ParseFormats(representation);

    }

}
