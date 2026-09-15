using System;
using System.Collections.Generic;
using System.Text;

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
    /// way. So a pattern yields a form only by being one of the spellings below, which is sound by
    /// construction and extended by adding a row.
    /// </para>
    /// <para>
    /// <b><c>format</c> yields nothing on its own.</b> JSON Schema calls it an annotation rather than an
    /// assertion, and RFC 9562 relaxed the lowercase-output rule RFC 4122 §3 had, so
    /// <c>format: uuid</c> does not say what is stored. The pattern beside it does.
    /// </para>
    /// <para>
    /// <b>Why the UUID rows differ in what they license.</b> Calcite compares UUIDs as two
    /// <em>signed</em> 64-bit halves, so lexical order of the canonical string matches its order only
    /// where the top bit of each half is constant — the 1st and 17th hex digits confined to one side of
    /// 8. RFC 4122 and 9562 pin the 17th to <c>8</c>–<c>b</c> for every conforming value, so the low
    /// half always agrees; the first digit is what varies, and a pattern that confines it is the
    /// difference between an equality-only form and a sortable one. Measured; see
    /// <c>DESIGN-93.md</c> §1.
    /// </para>
    /// </remarks>
    public static class CosmosStoredForms
    {

        /// <summary>A lowercase canonical UUID whose order is not the order Calcite compares in.</summary>
        public static readonly CosmosRepresentation UuidCanonicalLower = new("uuid-canonical-lower", PreservesEquality: true, PreservesOrder: false);

        /// <summary>A lowercase canonical UUID whose first hex digit is confined, so that lexical order is Calcite's order.</summary>
        public static readonly CosmosRepresentation UuidCanonicalLowerSortable = new("uuid-canonical-lower-sortable", PreservesEquality: true, PreservesOrder: true);

        /// <summary>An ISO-8601 UTC instant at one fixed precision, whose lexical order is chronological.</summary>
        public static readonly CosmosRepresentation Iso8601UtcSeconds = new("iso8601-utc-seconds", PreservesEquality: true, PreservesOrder: true);

        /// <inheritdoc cref="Iso8601UtcSeconds" />
        public static readonly CosmosRepresentation Iso8601UtcMilliseconds = new("iso8601-utc-milliseconds", PreservesEquality: true, PreservesOrder: true);

        /// <inheritdoc cref="Iso8601UtcSeconds" />
        public static readonly CosmosRepresentation Iso8601UtcMicroseconds = new("iso8601-utc-microseconds", PreservesEquality: true, PreservesOrder: true);

        /// <summary>A calendar date, whose lexical order is chronological.</summary>
        public static readonly CosmosRepresentation Iso8601Date = new("iso8601-date", PreservesEquality: true, PreservesOrder: true);

        const string Hex = "[0-9a-f]";

        static readonly Dictionary<string, CosmosRepresentation> Known = Build();

        static Dictionary<string, CosmosRepresentation> Build()
        {
            var known = new Dictionary<string, CosmosRepresentation>(StringComparer.Ordinal);

            void Add(string pattern, CosmosRepresentation representation) => known[Normalise(pattern)!] = representation;

            // Canonical lowercase, in the spellings people write: bare, with the RFC variant nibble
            // pinned, and with a version digit pinned. None of them confines the first digit, so none
            // of them is sortable.
            Add($"^{Hex}{{8}}-{Hex}{{4}}-{Hex}{{4}}-{Hex}{{4}}-{Hex}{{12}}$", UuidCanonicalLower);
            Add($"^{Hex}{{8}}-{Hex}{{4}}-{Hex}{{4}}-[89ab]{Hex}{{3}}-{Hex}{{12}}$", UuidCanonicalLower);
            Add($"^{Hex}{{8}}-{Hex}{{4}}-4{Hex}{{3}}-[89ab]{Hex}{{3}}-{Hex}{{12}}$", UuidCanonicalLower);
            Add($"^{Hex}{{8}}-{Hex}{{4}}-7{Hex}{{3}}-[89ab]{Hex}{{3}}-{Hex}{{12}}$", UuidCanonicalLower);

            // The first digit confined, which is what v7 gives for every timestamp anyone will store,
            // and the variant pinned so the low half agrees too. Both halves constant, so lexical
            // order is Calcite's order.
            Add($"^[0-7]{Hex}{{7}}-{Hex}{{4}}-{Hex}{{4}}-[89ab]{Hex}{{3}}-{Hex}{{12}}$", UuidCanonicalLowerSortable);
            Add($"^[0-7]{Hex}{{7}}-{Hex}{{4}}-7{Hex}{{3}}-[89ab]{Hex}{{3}}-{Hex}{{12}}$", UuidCanonicalLowerSortable);

            // One fixed ISO-8601 UTC shape. The fixedness is the whole of it: mixed precision sorts
            // '.500Z' before 'Z' because '.' is 0x2E and 'Z' is 0x5A, and a mixed Z/offset breaks it
            // the same way.
            Add("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", Iso8601UtcSeconds);
            Add("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$", Iso8601UtcMilliseconds);
            Add("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}Z$", Iso8601UtcMicroseconds);
            Add("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", Iso8601Date);

            return known;
        }

        /// <summary>
        /// Determines whether a form is one of the UUID spellings, whose stored string is the
        /// canonical lowercase rendering.
        /// </summary>
        /// <remarks>
        /// Asked by a rewrite that has to <em>write</em> the stored string for a value, which it can
        /// only do for a form it knows the spelling of. The two differ in what they license and not in
        /// how they are written.
        /// </remarks>
        /// <param name="representation">The form.</param>
        /// <returns><c>true</c> where the stored string is a canonical lowercase UUID.</returns>
        public static bool IsUuid(CosmosRepresentation representation) =>
            string.Equals(representation.Name, UuidCanonicalLower.Name, StringComparison.Ordinal) ||
            string.Equals(representation.Name, UuidCanonicalLowerSortable.Name, StringComparison.Ordinal);

        /// <summary>
        /// Returns the stored form a declared pattern is recognised as, or <c>null</c>.
        /// </summary>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c>.</param>
        /// <returns>The representation, or <c>null</c> where the pattern is not one this knows.</returns>
        public static CosmosRepresentation? Recognise(string? pattern)
        {
            if (Normalise(pattern) is not string normalised)
                return null;

            return Known.TryGetValue(normalised, out var representation) ? representation : null;
        }

        /// <summary>
        /// Puts a pattern into the one spelling the table is keyed by.
        /// </summary>
        /// <remarks>
        /// Two liberties only, and both are identities rather than guesses. Whitespace outside a
        /// character class means nothing in an unextended ECMA-262 pattern, which is the dialect JSON
        /// Schema specifies; and <c>\d</c> is exactly <c>[0-9]</c> there. Anything else — a different
        /// quantifier, a looser class, a missing anchor — is a different language and gets no entry.
        /// </remarks>
        internal static string? Normalise(string? pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return null;

            var builder = new StringBuilder(pattern!.Length);

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (char.IsWhiteSpace(c))
                    continue;

                if (c == '\\' && i + 1 < pattern.Length && pattern[i + 1] == 'd')
                {
                    builder.Append("[0-9]");
                    i++;
                    continue;
                }

                if (c == '\\' && i + 1 < pattern.Length)
                {
                    builder.Append(c).Append(pattern[i + 1]);
                    i++;
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

    }

}
