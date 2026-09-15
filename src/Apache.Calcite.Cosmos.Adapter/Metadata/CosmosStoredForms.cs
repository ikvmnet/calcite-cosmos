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
    /// <c>DESIGN.md</c> under <em>A fact says which relations it preserves</em>.
    /// </para>
    /// </remarks>
    public static class CosmosStoredForms
    {

        /// <summary>
        /// A lowercase canonical UUID whose order is not the order Calcite compares in.
        /// </summary>
        public static readonly CosmosRepresentation UuidCanonicalLower = new("uuid-canonical-lower", PreservesEquality: true, PreservesOrder: false);

        /// <summary>
        /// A lowercase canonical UUID whose first hex digit is confined, so that lexical order is Calcite's order.
        /// </summary>
        public static readonly CosmosRepresentation UuidCanonicalLowerSortable = new("uuid-canonical-lower-sortable", PreservesEquality: true, PreservesOrder: true);

        /// <summary>
        /// An uppercase canonical UUID, which is as canonical as the lowercase one and spelled differently.
        /// </summary>
        /// <remarks>
        /// Canonical means one value has one spelling, not that the spelling is the one RFC 4122 prints.
        /// A container written in uppercase throughout is exactly as addressable; what changes is which
        /// way a comparison has to render its literal, and <see cref="RenderUuid"/> is where that is
        /// decided. What is <em>not</em> canonical is a container holding both, which no pattern here
        /// recognises.
        /// </remarks>
        public static readonly CosmosRepresentation UuidCanonicalUpper = new("uuid-canonical-upper", PreservesEquality: true, PreservesOrder: false);

        /// <inheritdoc cref="UuidCanonicalUpper" />
        public static readonly CosmosRepresentation UuidCanonicalUpperSortable = new("uuid-canonical-upper-sortable", PreservesEquality: true, PreservesOrder: true);

        /// <summary>
        /// An ISO-8601 UTC instant at one fixed precision, whose lexical order is chronological.
        /// </summary>
        public static readonly CosmosRepresentation Iso8601UtcSeconds = new("iso8601-utc-seconds", PreservesEquality: true, PreservesOrder: true);

        /// <inheritdoc cref="Iso8601UtcSeconds" />
        public static readonly CosmosRepresentation Iso8601UtcMilliseconds = new("iso8601-utc-milliseconds", PreservesEquality: true, PreservesOrder: true);

        /// <inheritdoc cref="Iso8601UtcSeconds" />
        public static readonly CosmosRepresentation Iso8601UtcMicroseconds = new("iso8601-utc-microseconds", PreservesEquality: true, PreservesOrder: true);

        /// <summary>
        /// A calendar date, whose lexical order is chronological.
        /// </summary>
        public static readonly CosmosRepresentation Iso8601Date = new("iso8601-date", PreservesEquality: true, PreservesOrder: true);

        const string Hex = "[0-9a-f]";
        const string HexUpper = "[0-9A-F]";

        /// <summary>
        /// The recognised spellings, keyed by their normalised form.
        /// </summary>
        static readonly Dictionary<string, CosmosRepresentation> Known = Build();

        /// <summary>
        /// Builds the table of spellings this recognises.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The UUID rows are generated rather than written out, because the spellings in the wild vary
        /// along four independent axes and the product of them is eighty patterns nobody would keep
        /// correct by hand: the case of the hex class, whether the first digit is confined, which
        /// version nibble is pinned if any, and whether the RFC variant nibble is pinned. Sorting a
        /// character class in <see cref="Normalise"/> collapses a fifth axis — the order its members
        /// are written in, <c>[a-f0-9]</c> being as common as <c>[0-9a-f]</c>.
        /// </para>
        /// <para>
        /// Each also gets the nil-UUID alternation the uuid package documents, which admits
        /// <c>00000000-0000-0000-0000-000000000000</c> beside the base pattern. That value is
        /// canonical in either case, having no letters, so equality survives — but its variant nibble
        /// is <c>0</c> rather than <c>8</c>–<c>b</c>, which puts the low half on the other side of
        /// zero and breaks the ordering argument. So the alternation is never sortable, whatever the
        /// pattern it wraps.
        /// </para>
        /// </remarks>
        /// <returns>The table.</returns>
        static Dictionary<string, CosmosRepresentation> Build()
        {
            var known = new Dictionary<string, CosmosRepresentation>(StringComparer.Ordinal);

            void Add(string pattern, CosmosRepresentation representation) => known[Normalise(pattern)!] = representation;

            // Version nibbles people pin: none, one of the eight RFC versions, or the range of them.
            var versions = new[] { null, "1", "2", "3", "4", "5", "6", "7", "8", "[1-8]" };

            foreach (var upper in new[] { false, true })
            {
                var hex = upper ? HexUpper : Hex;
                var variant = upper ? "[89AB]" : "[89ab]";
                var plain = upper ? UuidCanonicalUpper : UuidCanonicalLower;
                var sortable = upper ? UuidCanonicalUpperSortable : UuidCanonicalLowerSortable;

                foreach (var confined in new[] { false, true })
                {
                    // The high half's sign is constant only where the first digit is confined, which
                    // is what v7 gives for any timestamp anyone will store.
                    var head = confined ? $"[0-7]{hex}{{7}}" : $"{hex}{{8}}";

                    foreach (var version in versions)
                    {
                        var third = version is null ? $"{hex}{{4}}" : $"{version}{hex}{{3}}";

                        foreach (var pinned in new[] { false, true })
                        {
                            // The low half's sign is constant only where the variant nibble is pinned,
                            // which RFC 4122 does for every conforming value anyway.
                            var fourth = pinned ? $"{variant}{hex}{{3}}" : $"{hex}{{4}}";
                            var body = $"^{head}-{hex}{{4}}-{third}-{fourth}-{hex}{{12}}$";

                            Add(body, confined && pinned ? sortable : plain);
                            Add($"(?:{body})|(?:^0{{8}}-0{{4}}-0{{4}}-0{{4}}-0{{12}}$)", plain);
                        }
                    }
                }
            }

            // One fixed ISO-8601 UTC shape. The fixedness is the whole of it: mixed precision sorts
            // '.500Z' before 'Z' because '.' is 0x2E and 'Z' is 0x5A, and a mixed Z/offset breaks it
            // the same way.
            Add("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", Iso8601UtcSeconds);
            Add(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$", Iso8601UtcMilliseconds);
            Add(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z$", Iso8601UtcMicroseconds);
            Add("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", Iso8601Date);

            return known;
        }

        /// <summary>
        /// Writes a UUID the way a path in this form stores it, or returns <c>null</c> where the form
        /// is not a UUID at all.
        /// </summary>
        /// <remarks>
        /// Asked by a rewrite that has to put a literal into the stored spelling before comparing
        /// against it. Which spelling that is, is the whole of what the UUID forms differ by: the
        /// comparison is exact either way, and a container written in uppercase is addressable on the
        /// same terms as one written in lowercase.
        /// </remarks>
        /// <param name="representation">The path form.</param>
        /// <param name="value">The value to write.</param>
        /// <returns>The stored spelling, or <c>null</c> where this form does not store a UUID.</returns>
        public static string? RenderUuid(CosmosRepresentation representation, Guid value)
        {
            if (IsLowerUuid(representation))
                return value.ToString("D");

            if (IsUpperUuid(representation))
                return value.ToString("D").ToUpperInvariant();

            return null;
        }

        /// <summary>
        /// Determines whether a form spells a UUID, in either case.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked where the spelling does not matter but the type does — a projection reading the
        /// stored text back as the <c>UUID</c> the plan declared, which needs to know only that the
        /// text is one. Both forms are the canonical hyphenated 36 characters, which is what
        /// <c>java.util.UUID.fromString</c> reads, so either parses to the value the container means.
        /// </para>
        /// <para>
        /// Sortability is not consulted and must not be: it says whether the lexical order of the
        /// stored strings is the order Calcite compares the values in, which a projection never asks.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path form.</param>
        /// <returns><c>true</c> where the form stores a UUID.</returns>
        public static bool IsUuid(CosmosRepresentation representation) => IsLowerUuid(representation) || IsUpperUuid(representation);

        /// <summary>
        /// Determines whether a form spells a UUID in lowercase.
        /// </summary>
        /// <param name="representation">The path form.</param>
        /// <returns><c>true</c> where the form stores a lowercase UUID.</returns>
        static bool IsLowerUuid(CosmosRepresentation representation) =>
            string.Equals(representation.Name, UuidCanonicalLower.Name, StringComparison.Ordinal) ||
            string.Equals(representation.Name, UuidCanonicalLowerSortable.Name, StringComparison.Ordinal);

        /// <summary>
        /// Determines whether a form spells a UUID in uppercase.
        /// </summary>
        /// <param name="representation">The path form.</param>
        /// <returns><c>true</c> where the form stores an uppercase UUID.</returns>
        static bool IsUpperUuid(CosmosRepresentation representation) =>
            string.Equals(representation.Name, UuidCanonicalUpper.Name, StringComparison.Ordinal) ||
            string.Equals(representation.Name, UuidCanonicalUpperSortable.Name, StringComparison.Ordinal);

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
        /// <para>
        /// Three liberties, and each is an identity rather than a guess. Whitespace outside a character
        /// class means nothing in an unextended ECMA-262 pattern, which is the dialect JSON Schema
        /// specifies; <c>\d</c> is exactly <c>[0-9]</c> there; and a character class is a <em>set</em>,
        /// so the order its members are written in carries no meaning — <c>[a-f0-9]</c> and
        /// <c>[0-9a-f]</c> are one class written two ways, and both are in use.
        /// </para>
        /// <para>
        /// Anything else — a different quantifier, a looser class, a missing anchor — is a different
        /// language and gets no entry.
        /// </para>
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

                if (c == '[' && pattern.IndexOf(']', i + 1) is int close && close > i)
                {
                    builder.Append(SortClass(pattern.Substring(i, close - i + 1)));
                    i = close;
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Writes a character class with its members in one order.
        /// </summary>
        /// <remarks>
        /// A class is a set, so <c>[a-f0-9]</c> and <c>[0-9a-f]</c> denote the same characters and only
        /// one of them can be the key the table is looked up by. The members are ranges and single
        /// characters; anything else in there — an escape, a negation, a literal <c>-</c> that is not
        /// part of a range — is left exactly as written rather than guessed at, which costs a spelling
        /// and never mistakes one class for another.
        /// </remarks>
        /// <param name="characters">The class, including its brackets.</param>
        /// <returns>The class with its members ordered, or unchanged where it could not be taken apart.</returns>
        static string SortClass(string characters)
        {
            var inner = characters.Substring(1, characters.Length - 2);

            if (inner.Length == 0 || inner[0] == '^' || inner.IndexOf('\\') >= 0)
                return characters;

            var members = new List<string>();

            for (var i = 0; i < inner.Length; i++)
            {
                if (i + 2 < inner.Length && inner[i + 1] == '-')
                {
                    members.Add(inner.Substring(i, 3));
                    i += 2;
                    continue;
                }

                // A bare '-' is a literal whose meaning depends on where it sits, so the class is left
                // as written rather than reordered around it.
                if (inner[i] == '-')
                    return characters;

                members.Add(inner[i].ToString());
            }

            members.Sort(StringComparer.Ordinal);

            return "[" + string.Concat(members) + "]";
        }

    }

}
