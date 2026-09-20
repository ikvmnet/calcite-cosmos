using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

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
    /// <b>Why the UUID rows differ in what they license, and why the difference is now conditional.</b>
    /// A canonical UUID's ordinal text order is the <em>unsigned</em> order of the 128-bit value, for
    /// every value and with nothing pinned. Whether that is the order Calcite compares in is a
    /// property of the engine rather than of the spelling, and it changed: see
    /// <see cref="UnsignedUuidComparison"/>, which is what the unconfined rows read. Measured; see
    /// <c>DESIGN.md</c> under <em>A fact says which relations it preserves</em>.
    /// </para>
    /// </remarks>
    public static class CosmosStoredForms
    {

        /// <summary>
        /// Whether the engine orders UUIDs as unsigned 128-bit values, which is what the two
        /// unconfined canonical rows rest on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It was not always so, and this reads the switch on the fix.</b>
        /// <c>java.util.UUID.compareTo</c> compares the two 64-bit halves as <em>signed</em> longs — a
        /// documented JDK quirk — so the lexical order of the canonical string was Calcite's order
        /// only where the top bit of each half was constant across the container, which is the 1st and
        /// 17th hex digits confined to one side of <c>8</c>. CALCITE-7716 treated that as a defect
        /// rather than as semantics and added <c>org.apache.calcite.util.UuidValue</c>, which compares
        /// unsigned, in 1.43.
        /// </para>
        /// <para>
        /// <b>Read rather than assumed, because the fix sits behind a property.</b>
        /// <c>calcite.uuid.unsigned.comparison</c> defaults to on, and a runtime that turns it off
        /// gets the old semantics back — over which an unconfined row claiming an order is a sort
        /// pushed to the service that returns the rows in the wrong order, which is the one failure
        /// this whole model exists to avoid. Calcite reads the property once into <c>UuidValue</c>'s
        /// own static, so reading it once here agrees with it for the life of the process.
        /// </para>
        /// <para>
        /// Measured, in <c>CalciteUuidOrderingMeasurementTests</c>: the property defaults to on, and
        /// under it <c>ORDER BY</c> over UUIDs is exactly <see cref="StringComparer.Ordinal"/> over
        /// their canonical spellings.
        /// </para>
        /// </remarks>
        static readonly bool UnsignedUuidComparison =
            ((java.lang.Boolean)org.apache.calcite.config.CalciteSystemProperty.UUID_UNSIGNED_COMPARISON.value()).booleanValue();

        /// <summary>
        /// A lowercase canonical UUID, whose ordinal text order is the unsigned order of the value.
        /// </summary>
        /// <remarks>
        /// The hyphens sit at fixed positions so they never decide anything, and <c>0</c>–<c>9</c>
        /// then <c>a</c>–<c>f</c> sort in nibble order, so comparing the stored strings compares the
        /// 32 nibbles in significance order — which is the unsigned comparison of the 128-bit value,
        /// for every value the form admits. Whether that is the order licensed is therefore not a
        /// question about the pattern at all but about the engine, and
        /// <see cref="UnsignedUuidComparison"/> is where it is asked.
        /// </remarks>
        public static readonly CosmosRepresentation UuidCanonicalLower = new("uuid-canonical-lower", PreservesEquality: true, PreservesOrder: UnsignedUuidComparison);

        /// <summary>
        /// A lowercase canonical UUID whose first hex digit is confined and whose variant nibble is
        /// pinned, so that lexical order is Calcite's order under a signed comparison too.
        /// </summary>
        /// <remarks>
        /// Under the unsigned comparison that is the default this licenses nothing
        /// <see cref="UuidCanonicalLower"/> does not, and it is kept rather than folded into it
        /// because the engine's switch can be turned off. Signed and unsigned comparison of two 64-bit
        /// values agree exactly where their top bits match, and that is what confining the 1st hex
        /// digit to <c>[0-7]</c> and pinning the 17th to the RFC variant range <c>8</c>–<c>b</c> buys:
        /// the high half's sign is then constant across the container and the low half's always was.
        /// </remarks>
        public static readonly CosmosRepresentation UuidCanonicalLowerSortable = new("uuid-canonical-lower-sortable", PreservesEquality: true, PreservesOrder: true);

        /// <summary>
        /// An uppercase canonical UUID, which is as canonical as the lowercase one and spelled differently.
        /// </summary>
        /// <remarks>
        /// Canonical means one value has one spelling, not that the spelling is the one RFC 4122 prints.
        /// A container written in uppercase throughout is exactly as addressable; what changes is which
        /// way a comparison has to render its literal, and <see cref="RenderUuid"/> is where that is
        /// decided. What is <em>not</em> canonical is a container holding both, which no pattern here
        /// recognises — and which is why the case never costs the ordering either: <c>A</c>–<c>F</c>
        /// sit above <c>0</c>–<c>9</c> in code point order exactly as <c>a</c>–<c>f</c> do, so an
        /// all-uppercase container sorts in nibble order on the same terms as an all-lowercase one.
        /// </remarks>
        public static readonly CosmosRepresentation UuidCanonicalUpper = new("uuid-canonical-upper", PreservesEquality: true, PreservesOrder: UnsignedUuidComparison);

        /// <inheritdoc cref="UuidCanonicalLowerSortable" />
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
        /// An ISO-8601 UTC instant at .NET tick precision, whose lexical order is chronological.
        /// </summary>
        /// <remarks>
        /// The shape the round-trip format specifier <c>"O"</c> writes, which is what the service's own
        /// <c>GetCurrentDateTime</c> returns and what Microsoft documents as the recommended spelling.
        /// It is listed last among the instants and is the one most worth having: the .NET SDK's
        /// default serializer omits trailing zeros from the fraction, so a container written without a
        /// converter holds a <em>mixed</em> path that no row here recognises, and a container that
        /// followed the recommendation holds this one.
        /// </remarks>
        public static readonly CosmosRepresentation Iso8601UtcTicks = new("iso8601-utc-ticks", PreservesEquality: true, PreservesOrder: true);

        /// <summary>
        /// A calendar date, whose lexical order is chronological.
        /// </summary>
        public static readonly CosmosRepresentation Iso8601Date = new("iso8601-date", PreservesEquality: true, PreservesOrder: true);

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

        const string Hex = "[0-9a-f]";
        const string HexUpper = "[0-9A-F]";

        /// <summary>
        /// How one temporal form writes a value, and which values it can write.
        /// </summary>
        /// <param name="Format">
        /// The spelling, or <c>null</c> where the form holds too little of an instant to be written
        /// one — a time of day carries no date, and rendering into it would drop one silently.
        /// </param>
        /// <param name="Exact">
        /// Whether a value lands on the form's own resolution. A form that cannot hold the value
        /// exactly renders nothing rather than a truncation; see <see cref="RenderDateTime"/>.
        /// </param>
        sealed record TemporalForm(string? Format, Func<DateTime, bool> Exact);

        /// <summary>
        /// How each temporal form writes a value, keyed by representation name. Populated by
        /// <see cref="Build"/>, which is why it is declared above <see cref="Known"/>.
        /// </summary>
        static readonly Dictionary<string, TemporalForm> Temporal = new(StringComparer.Ordinal);

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
        /// canonical in either case, having no letters, so equality survives — and the alternation is
        /// registered as the plain row rather than the confined one whatever the pattern it wraps,
        /// which is right under either comparison. Under a signed one the nil value's variant nibble
        /// is <c>0</c> rather than <c>8</c>–<c>b</c>, which puts the low half on the other side of
        /// zero and breaks the confined row's argument; under the unsigned default the plain row
        /// carries the order anyway and the nil value is the least of them both lexically and
        /// numerically.
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
                    // Under a signed comparison the high half's sign is constant only where the first
                    // digit is confined, which is what v7 gives for any timestamp anyone will store.
                    // Under the unsigned default it decides nothing and the plain row carries the
                    // order regardless — see UnsignedUuidComparison.
                    var head = confined ? $"[0-7]{hex}{{7}}" : $"{hex}{{8}}";

                    foreach (var version in versions)
                    {
                        var third = version is null ? $"{hex}{{4}}" : $"{version}{hex}{{3}}";

                        foreach (var pinned in new[] { false, true })
                        {
                            // And the low half's sign only where the variant nibble is pinned, which
                            // RFC 4122 does for every conforming value anyway.
                            var fourth = pinned ? $"{variant}{hex}{{3}}" : $"{hex}{{4}}";
                            var body = $"^{head}-{hex}{{4}}-{third}-{fourth}-{hex}{{12}}$";

                            Add(body, confined && pinned ? sortable : plain);
                            Add($"(?:{body})|(?:^0{{8}}-0{{4}}-0{{4}}-0{{4}}-0{{12}}$)", plain);
                        }
                    }
                }
            }

            AddTemporal(Add);

            return known;
        }

        /// <summary>
        /// Builds the temporal rows, across every axis that can be pinned.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What makes a temporal form usable is its fixedness, not which shape it is.</b>
        /// Lexicographic order is chronological for any spelling whose every field is the same width
        /// in every value — so the axes are enumerated rather than chosen between: how many fraction
        /// digits, how the zero offset is spelled, whether the date and time carry separators, and how
        /// much of the instant is stored at all. Each combination is a language, and generating them
        /// is the same decision the UUID rows above make for the same reason.
        /// </para>
        /// <para>
        /// <b>What is never generated is the shape that varies.</b> A fraction of unstated width —
        /// <c>[0-9]{1,7}</c>, or an optional group — sorts <c>'.5Z'</c> before <c>'Z'</c> because
        /// <c>'.'</c> is 0x2E and <c>'Z'</c> is 0x5A. An offset not pinned to zero orders two instants
        /// by their local clocks rather than by when they happened. Both admit a conforming document
        /// the order gets wrong, so neither appears here however commonly it is written. This is the
        /// whole of why the .NET SDK's default output is unrecognised: it trims trailing zeros, so the
        /// path it writes has no fixed width.
        /// </para>
        /// <para>
        /// <b>The zone-less rows are sound on the same terms as the others.</b> A path storing
        /// <c>2024-01-15T12:30:00</c> has one spelling per value and one width, so both relations hold
        /// among the stored strings. What it does not say is which zone those readings are in — and
        /// neither does Calcite's <c>TIMESTAMP</c>, which carries none either, so a literal compares
        /// wall clock against wall clock and the two agree.
        /// </para>
        /// </remarks>
        /// <param name="add">Registers one spelling.</param>
        static void AddTemporal(Action<string, CosmosRepresentation> add)
        {
            void Register(string pattern, string name, string? format, Func<DateTime, bool> exact)
            {
                Temporal[name] = new TemporalForm(format, exact);
                add(pattern, new CosmosRepresentation(name, PreservesEquality: true, PreservesOrder: true));
            }

            // Zero to nine fraction digits. A tick is seven, so a wider form is written by padding and
            // still holds every value exactly; a narrower one has to land on its own resolution, which
            // is what its predicate tests.
            var fractions = new (string Pattern, string Format, Func<DateTime, bool> Exact)[10];

            for (var n = 0; n < fractions.Length; n++)
            {
                var digits = n < 7 ? n : 7;
                var padding = n > 7 ? new string('0', n - 7) : string.Empty;

                var resolution = 1L;
                for (var i = 0; i < 7 - digits; i++)
                    resolution *= 10;

                fractions[n] = (
                    n == 0 ? string.Empty : $@"\.[0-9]{{{n}}}",
                    n == 0 ? string.Empty : "." + new string('f', digits) + (padding.Length > 0 ? "'" + padding + "'" : string.Empty),
                    value => value.Ticks % resolution == 0);
            }

            // How the zero offset is spelled. `Z` and `+00:00` denote the same instant and sort
            // differently against each other, which is why a path may use either and not both.
            var zones = new (string Pattern, string Format, string Name)[]
            {
                ("Z", "'Z'", "z"),
                (@"\+00:00", "'+00:00'", "offset"),
                (string.Empty, string.Empty, "local"),
            };

            foreach (var zone in zones)
            {
                for (var n = 0; n < fractions.Length; n++)
                {
                    var fraction = fractions[n];

                    Register(
                        $"^[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}T[0-9]{{2}}:[0-9]{{2}}:[0-9]{{2}}{fraction.Pattern}{zone.Pattern}$",
                        InstantName(n, zone.Name),
                        "yyyy-MM-dd'T'HH:mm:ss" + fraction.Format + zone.Format,
                        fraction.Exact);

                    // The basic format, which drops the separators and is as fixed as the extended one.
                    Register(
                        $"^[0-9]{{8}}T[0-9]{{6}}{fraction.Pattern}{zone.Pattern}$",
                        $"iso8601-basic-f{n}-{zone.Name}",
                        "yyyyMMdd'T'HHmmss" + fraction.Format + zone.Format,
                        fraction.Exact);
                }

                // Minute precision, which plenty of feeds write and which is fixed like any other.
                Register(
                    $"^[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}T[0-9]{{2}}:[0-9]{{2}}{zone.Pattern}$",
                    $"iso8601-instant-minutes-{zone.Name}",
                    "yyyy-MM-dd'T'HH:mm" + zone.Format,
                    value => value.Ticks % TimeSpan.TicksPerMinute == 0);
            }

            // Calendar shapes, which store no time of day at all. A value carrying one does not land
            // on them, and is refused rather than truncated.
            Register("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", "iso8601-date", "yyyy-MM-dd", value => value.TimeOfDay == TimeSpan.Zero);
            Register("^[0-9]{8}$", "iso8601-date-basic", "yyyyMMdd", value => value.TimeOfDay == TimeSpan.Zero);
            Register("^[0-9]{4}-[0-9]{2}$", "iso8601-year-month", "yyyy-MM", value => value.Day == 1 && value.TimeOfDay == TimeSpan.Zero);

            // A time of day with no date. Recognised, because its lexical order is its chronological
            // order and a sort over one is sound on exactly those terms; not renderable, because
            // writing an instant into it would drop the date rather than refuse.
            for (var n = 0; n < fractions.Length; n++)
                Register($"^[0-9]{{2}}:[0-9]{{2}}:[0-9]{{2}}{fractions[n].Pattern}$", $"iso8601-time-f{n}", null, Never);

            Register("^[0-9]{2}:[0-9]{2}$", "iso8601-time-minutes", null, Never);
        }

        /// <summary>
        /// The predicate of a form no value is written into.
        /// </summary>
        static bool Never(DateTime value) => false;

        /// <summary>
        /// Names one instant form, keeping the four spellings that were named before this was
        /// generated on the names they already had.
        /// </summary>
        /// <remarks>
        /// The names are an API — they appear in diagnostics, in <c>DESIGN.md</c> and in the tests —
        /// so the four that predate the generator keep theirs rather than being renamed into the
        /// scheme. Everything else is named systematically.
        /// </remarks>
        /// <param name="fraction">The number of fraction digits.</param>
        /// <param name="zone">The zone spelling's name.</param>
        /// <returns>The form's name.</returns>
        static string InstantName(int fraction, string zone)
        {
            if (string.Equals(zone, "z", StringComparison.Ordinal))
                switch (fraction)
                {
                    case 0: return "iso8601-utc-seconds";
                    case 3: return "iso8601-utc-milliseconds";
                    case 6: return "iso8601-utc-microseconds";
                    case 7: return "iso8601-utc-ticks";
                }

            return $"iso8601-instant-f{fraction}-{zone}";
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
        /// Writes an instant the way a path in this form stores it, or returns <c>null</c> where the
        /// form is not a temporal one or the value does not land on its precision.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The refusal on precision is the point, not a limitation.</b> A comparison is rewritten
        /// only where the rewritten one selects the same documents, and truncating the literal to the
        /// stored precision does not preserve that: against a seconds path,
        /// <c>ts &gt; TIMESTAMP '2024-01-15 12:30:00.5'</c> truncated to <c>'2024-01-15T12:30:00Z'</c>
        /// admits the stored value <c>12:30:00Z</c>, which is earlier than the literal. So a literal
        /// carrying more precision than the form yields no rewrite and the comparison stays where it
        /// was. A literal carrying <em>less</em> is written out in full — <c>.000Z</c> for a
        /// milliseconds path — which loses nothing and is the ordinary case.
        /// </para>
        /// <para>
        /// <b>The literal is read as UTC.</b> Calcite's <c>TIMESTAMP</c> carries no zone, and these
        /// forms all store one; taking the literal's wall clock as the UTC wall clock is what makes the
        /// comparison mean what the query says. A container storing local time has no form here and
        /// gets no rewrite, which is the same answer <c>DESIGN.md</c> gives for a mixed offset.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path form.</param>
        /// <param name="value">The value to write, read as UTC.</param>
        /// <returns>The stored spelling, or <c>null</c> where this form does not store it exactly.</returns>
        public static string? RenderDateTime(CosmosRepresentation representation, DateTime value)
        {
            if (Temporal.TryGetValue(representation.Name, out var form) == false || form.Format is null)
                return null;

            return form.Exact(value) ? value.ToString(form.Format, CultureInfo.InvariantCulture) : null;
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
        public static string? RenderInteger(CosmosRepresentation representation, long value)
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

        /// <summary>
        /// Returns the stored form a declared pattern is recognised as, or <c>null</c>.
        /// </summary>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c>.</param>
        /// <returns>The representation, or <c>null</c> where the pattern is not one this knows.</returns>
        public static CosmosRepresentation? Recognise(string? pattern)
        {
            if (Normalise(pattern) is not string normalised)
                return null;

            if (Known.TryGetValue(normalised, out var representation))
                return representation;

            // The numeric forms are parametric rather than tabulated: a fixed width is a family with
            // one member per width, and no table would hold it. Both matchers require the anchors,
            // because JSON Schema's `pattern` is a search rather than a full match -- an unanchored
            // `[0-9]{5}` is satisfied by `x12345y`, which constrains nothing this could rest on.
            if (FixedWidthInteger.Match(normalised) is { Success: true } fixedWidth)
                return int.TryParse(fixedWidth.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var width) && width > 0
                    ? IntegerFixedWidth(width)
                    : null;

            if (UnpaddedInteger.IsMatch(normalised))
                return IntegerUnpadded;

            return null;
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

            return Collapse(builder.ToString());
        }

        /// <summary>
        /// Writes a run of one repeated atom as a single counted one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A fourth liberty, and an identity like the other three: for an atom matching exactly one
        /// character, <c>A{m}A{n}</c> and <c>A{m+n}</c> accept the same strings, and so do <c>A</c> and
        /// <c>A{1}</c>. That collapses the axis a date pattern varies along most — <c>\d\d\d\d-\d\d-\d\d</c>
        /// is as common as <c>\d{4}-\d{2}-\d{2}</c>, and neither spelling is more canonical than the
        /// other.
        /// </para>
        /// <para>
        /// Only the counted quantifier merges. <c>?</c>, <c>*</c>, <c>+</c> and <c>{m,n}</c> all admit a
        /// range of widths, which is the one property these forms turn on, so an atom carrying one is
        /// emitted exactly as written and ends the run beside it. So is anything that is not a
        /// single-character atom — an anchor, a group, an alternation — which is what keeps the
        /// nil-UUID alternation above intact.
        /// </para>
        /// </remarks>
        /// <param name="pattern">The pattern, already normalised in the other three respects.</param>
        /// <returns>The pattern with its runs counted.</returns>
        static string Collapse(string pattern)
        {
            var output = new StringBuilder(pattern.Length);
            string? pending = null;
            var count = 0L;

            void Flush()
            {
                if (pending is null)
                    return;

                output.Append(pending);

                if (count > 1)
                    output.Append('{').Append(count.ToString(CultureInfo.InvariantCulture)).Append('}');

                pending = null;
                count = 0;
            }

            var i = 0;
            while (i < pattern.Length)
            {
                var start = i;

                if (ReadAtom(pattern, ref i) is not string atom)
                {
                    Flush();
                    output.Append(pattern[i]);
                    i++;
                    continue;
                }

                var repeats = 1L;

                if (i < pattern.Length && pattern[i] == '{' && pattern.IndexOf('}', i + 1) is int close && close > i)
                {
                    var inner = pattern.Substring(i + 1, close - i - 1);

                    if (inner.Length > 0 && long.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var exact))
                    {
                        repeats = exact;
                        i = close + 1;
                    }
                    else
                    {
                        // A range rather than a count, which is a different language.
                        Flush();
                        output.Append(pattern, start, close + 1 - start);
                        i = close + 1;
                        continue;
                    }
                }
                else if (i < pattern.Length && (pattern[i] is '?' or '*' or '+'))
                {
                    Flush();
                    output.Append(pattern, start, i + 1 - start);
                    i++;
                    continue;
                }

                if (pending is not null && string.Equals(pending, atom, StringComparison.Ordinal))
                {
                    count += repeats;
                    continue;
                }

                Flush();
                pending = atom;
                count = repeats;
            }

            Flush();
            return output.ToString();
        }

        /// <summary>
        /// Reads the atom at a position, where one matching a single character starts there.
        /// </summary>
        /// <remarks>
        /// A character class, an escape and a bare literal each match exactly one character and are
        /// therefore mergeable. A structural character is not read at all — the position is left where
        /// it was and the caller copies it across, which is what keeps groups and anchors untouched.
        /// </remarks>
        /// <param name="pattern">The pattern.</param>
        /// <param name="i">The position, advanced past the atom where one was read.</param>
        /// <returns>The atom, or <c>null</c> where the position holds no single-character atom.</returns>
        static string? ReadAtom(string pattern, ref int i)
        {
            var c = pattern[i];

            if (c is '^' or '$' or '|' or '(' or ')' or '?' or '*' or '+' or '{' or '}')
                return null;

            if (c == '[')
            {
                var close = pattern.IndexOf(']', i + 1);
                if (close < 0)
                    return null;

                var characters = pattern.Substring(i, close - i + 1);
                i = close + 1;
                return characters;
            }

            if (c == '\\' && i + 1 < pattern.Length)
            {
                var escape = pattern.Substring(i, 2);
                i += 2;
                return escape;
            }

            i++;
            return c.ToString();
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
