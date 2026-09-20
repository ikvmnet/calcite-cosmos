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
    /// way. So a pattern yields a form only by being read as a language whose every string is
    /// canonical, which is sound by construction.
    /// </para>
    /// <para>
    /// <b>Two ways of reading one, and the UUID rows needed the second.</b> The temporal shapes are
    /// tabulated, generated across the axes a fixed spelling varies along, because those axes close:
    /// a fraction has a width, a zone has a spelling, and there is nothing else to vary. A UUID
    /// pattern varies along axes that do not — which version nibbles are pinned, which digits the
    /// variant admits, how a class is written, what prefix a container fixes — so the product was
    /// eighty rows that still missed <c>[1-5]</c>, the single commonest spelling published. Missing it
    /// was not a missed optimisation: an unrecognised pattern states <em>nothing</em>, so the path
    /// lost its equality with its order. <see cref="RecogniseUuid"/> reads the shape instead.
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

        /// <remarks>
        /// <b>Twelve more are generated beside these four and are not named here.</b> A UUID is also
        /// written brace-wrapped and written without its hyphens, and those axes cross the two above:
        /// <c>uuid-braced-lower</c>, <c>uuid-hyphenless-upper-sortable</c> and the rest, all built by
        /// <see cref="BuildUuids"/>. They carry the same two bits for the reason
        /// <see cref="UuidForm"/> gives — a character in the same place in every stored string never
        /// decides a comparison — so only the rendering differs, and only these four are fields
        /// because only these four are named in tests and in <c>DESIGN.md</c>.
        /// </remarks>

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
        /// The UUID spellings are not in here. They were, as eighty generated rows, and a table was
        /// the wrong shape for them: the axes people vary a UUID pattern along do not close, so every
        /// spelling outside the product was read as saying <em>nothing</em> — losing the equality as
        /// well as the order. <see cref="RecogniseUuid"/> reads the shape instead, which is the same
        /// soundness argument reached by deciding it rather than by enumerating it.
        /// </remarks>
        /// <returns>The table.</returns>
        static Dictionary<string, CosmosRepresentation> Build()
        {
            var known = new Dictionary<string, CosmosRepresentation>(StringComparer.Ordinal);

            void Add(string pattern, CosmosRepresentation representation) => known[Normalise(pattern)!] = representation;

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
        /// One of the sixteen UUID forms, as the four independent things a pattern says about it.
        /// </summary>
        /// <remarks>
        /// Only the first two decide how a literal is written; the last two decide what the form
        /// licenses and which name it carries. They are one record because a form is the product of
        /// all four and nothing here is ever asked about three of them.
        /// </remarks>
        /// <param name="Braced">Whether every stored string is wrapped in <c>{</c> and <c>}</c>.</param>
        /// <param name="Hyphenated">Whether the four hyphens are written.</param>
        /// <param name="Upper">Whether the hex digits are written in uppercase.</param>
        /// <param name="Confined">Whether both halves' signs are pinned across the container.</param>
        readonly record struct UuidSpelling(bool Braced, bool Hyphenated, bool Upper, bool Confined);

        /// <summary>
        /// The form each spelling is, keyed by the name it carries.
        /// </summary>
        /// <remarks>
        /// Read by <see cref="RenderUuid"/> the way <see cref="Temporal"/> is read by
        /// <see cref="RenderDateTime"/>: recognition hands back a name, and writing a literal needs
        /// the shape the name stands for.
        /// </remarks>
        static readonly Dictionary<string, UuidSpelling> Uuids = BuildUuids();

        /// <summary>
        /// Builds the table of UUID forms, one per combination of the four axes.
        /// </summary>
        /// <remarks>
        /// Sixteen rows, and they are rows rather than a decision because these axes <em>do</em>
        /// close: a UUID is braced or not, hyphenated or not, upper or lower, confined or not, and
        /// there is nothing else for a form to be. That is the distinction
        /// <see cref="RecogniseUuid"/> turns on — the *pattern* varies without bound and the *form* it
        /// denotes does not.
        /// </remarks>
        /// <returns>The table.</returns>
        static Dictionary<string, UuidSpelling> BuildUuids()
        {
            var uuids = new Dictionary<string, UuidSpelling>(StringComparer.Ordinal);

            foreach (var braced in new[] { false, true })
                foreach (var hyphenated in new[] { false, true })
                    foreach (var upper in new[] { false, true })
                        foreach (var confined in new[] { false, true })
                        {
                            var spelling = new UuidSpelling(braced, hyphenated, upper, confined);
                            uuids[UuidForm(spelling).Name] = spelling;
                        }

            return uuids;
        }

        /// <summary>
        /// The form one spelling is, named systematically and licensing what the spelling licenses.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What the shape axes cost, which is nothing.</b> A brace is in the same place in every
        /// stored string and the hyphens are in the same places or in none, so neither ever decides a
        /// comparison: the strings still compare nibble by nibble in significance order, and one value
        /// still has one spelling. So all sixteen forms carry the same two bits, and the shape decides
        /// only how <see cref="RenderUuid"/> writes a literal into the container.
        /// </para>
        /// <para>
        /// <b>The four canonical names predate this and are not renamed.</b> They appear in
        /// diagnostics, in <c>DESIGN.md</c> and in the tests, so the scheme is chosen to reproduce
        /// them rather than to replace them — <c>uuid-canonical-lower</c> and
        /// <c>uuid-canonical-lower-sortable</c> are what <c>uuid-{shape}-{case}</c> already spells for
        /// the hyphenated, unbraced shape.
        /// </para>
        /// </remarks>
        /// <param name="spelling">The four axes.</param>
        /// <returns>The form.</returns>
        static CosmosRepresentation UuidForm(UuidSpelling spelling)
        {
            var shape = (spelling.Braced, spelling.Hyphenated) switch
            {
                (false, true) => "canonical",
                (false, false) => "hyphenless",
                (true, true) => "braced",
                (true, false) => "braced-hyphenless",
            };

            var name = $"uuid-{shape}-{(spelling.Upper ? "upper" : "lower")}{(spelling.Confined ? "-sortable" : string.Empty)}";

            return new CosmosRepresentation(name, PreservesEquality: true, PreservesOrder: spelling.Confined || UnsignedUuidComparison);
        }

        /// <summary>
        /// Where each hyphen sits in a canonical UUID, counted in nibbles before it.
        /// </summary>
        static readonly int[] Hyphens = { 8, 12, 16, 20 };

        /// <summary>
        /// How many nibbles a canonical UUID spells.
        /// </summary>
        const int Nibbles = 32;

        /// <summary>
        /// The nibble whose top bit is the sign of <c>mostSigBits</c>.
        /// </summary>
        const int HighSignNibble = 0;

        /// <summary>
        /// The nibble whose top bit is the sign of <c>leastSigBits</c> — the RFC variant nibble.
        /// </summary>
        const int LowSignNibble = 16;

        /// <summary>
        /// Reads a declared pattern as a canonical UUID, deciding the shape rather than looking it up.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this is not a table, and what the table cost.</b> A UUID pattern is written along
        /// axes that do not close. The version nibble is pinned to a digit, or to <c>[1-5]</c> because
        /// RFC 4122 defined five, or to <c>[1-8]</c> because RFC 9562 defined eight, or to <c>[45]</c>
        /// because the writer generates both; the variant nibble is <c>[89ab]</c> or <c>[8-9a-b]</c>
        /// or <c>[89a-b]</c>; the hex class is <c>[0-9a-f]</c> or <c>[0-9abcdef]</c> or
        /// <c>[a-cd-f0-9]</c>; a container with a known epoch pins a prefix. Enumerating that product
        /// is hopeless, and the failure was not a missing optimisation: a spelling outside the table
        /// was recognised as <em>nothing</em>, so the path lost its equality as well as its order and
        /// every comparison against it read whole documents.
        /// </para>
        /// <para>
        /// <b>What is decided instead, and why it is still a proof.</b> The shape is 32 nibble slots,
        /// anchored at both ends, where every slot admits some set of hex digits — with the four
        /// hyphens at their fixed positions or absent altogether, and the whole thing brace-wrapped or
        /// not. Those two are axes of the spelling rather than of the argument: a brace sits in the
        /// same place in every stored string and the hyphens sit in the same places or in none, so
        /// neither ever decides a comparison. Three things follow for <em>every</em> string such a pattern accepts,
        /// with nothing enumerated. One spelling per value, so long as the whole pattern draws from a
        /// single case — which is <see cref="Classify"/>'s test, and is the one thing the shape does
        /// not give on its own. Lexical order is nibble order, because the hyphens sit at fixed
        /// positions and so never decide a comparison, and <c>0</c>–<c>9</c> sort below <c>a</c>–<c>f</c>
        /// and below <c>A</c>–<c>F</c> alike. And nibble order taken in significance order is the
        /// unsigned order of the 128-bit value. Confining a slot only removes strings, so a narrower
        /// pattern inherits all three from the widest one.
        /// </para>
        /// <para>
        /// <b>The confinement is read off the shape rather than matched.</b> Under the signed
        /// comparison <see cref="UnsignedUuidComparison"/> guards, lexical order is Calcite's order
        /// only where each half's sign is constant, and the sign of a half is the top bit of one
        /// nibble: the 1st and the 17th. So a form is sortable-regardless exactly where slot
        /// <see cref="HighSignNibble"/> is confined to <c>0</c>–<c>7</c> and slot
        /// <see cref="LowSignNibble"/> to <c>8</c>–<c>f</c>, which is what RFC 4122's variant gives
        /// for free and what a v7 container gives for the first. A shape sign-constant the other way
        /// round is sound on the same argument and is deliberately <em>not</em> claimed here, because
        /// the claim would need a name of its own for <see cref="RenderUuid"/> to refuse literals
        /// against; under the unsigned default the plain row carries its order anyway.
        /// </para>
        /// <para>
        /// <b>An alternation is a union rather than a special case.</b> The nil-UUID alternation the
        /// uuid package documents is a second body whose every slot is pinned to <c>0</c>, and taking
        /// the union of the two bodies' slots derives what used to be written down by hand: the 17th
        /// slot becomes <c>[089ab]</c>, which is no longer confined to <c>8</c>–<c>f</c>, so the
        /// pattern lands on the plain row exactly as it did before. Any other alternation — two
        /// version nibbles written as two branches, a nil beside a max — is read on the same terms.
        /// </para>
        /// </remarks>
        /// <param name="pattern">The pattern, already normalised.</param>
        /// <returns>The form, or <c>null</c> where the pattern is not a canonical UUID.</returns>
        static CosmosRepresentation? RecogniseUuid(string pattern)
        {
            return MatchUuid(pattern, 0) is UuidShape shape ? Classify(shape) : null;
        }

        /// <summary>
        /// What one pattern says a stored UUID looks like: which digits each nibble admits, and which
        /// of the three spellings the value is written in.
        /// </summary>
        /// <remarks>
        /// The slots decide which relations the form preserves and the two flags decide only how a
        /// literal is written into it, which is the same split <see cref="TemporalForm"/> makes. A
        /// constant brace and an absent hyphen change neither relation: they are in the same place in
        /// every stored string, so they never decide a comparison.
        /// </remarks>
        /// <param name="Slots">What each of the 32 nibbles admits.</param>
        /// <param name="Braced">Whether every stored string is wrapped in <c>{</c> and <c>}</c>.</param>
        /// <param name="Hyphenated">Whether the four hyphens are written.</param>
        sealed record UuidShape(string[] Slots, bool Braced, bool Hyphenated);

        /// <summary>
        /// Reads an anchored expression, a group around one, or an alternation of them.
        /// </summary>
        /// <remarks>
        /// Each alternative carries its own anchors here, because a top-level <c>|</c> binds looser
        /// than they do: <c>^a|b$</c> is <c>(^a)|(b$)</c>, whose second branch is unanchored at the
        /// front and admits a conforming value with anything before it. Inside an anchored body the
        /// anchors are already outside the alternation, which is why <see cref="MatchBody"/> asks for
        /// none.
        /// </remarks>
        /// <param name="pattern">The expression.</param>
        /// <param name="depth">Bounds the recursion a nested group can drive.</param>
        /// <returns>The shape, or <c>null</c>.</returns>
        static UuidShape? MatchUuid(string pattern, int depth)
        {
            if (depth > Nesting)
                return null;

            if (SplitAlternatives(pattern) is { Count: > 1 } alternatives)
                return Union(alternatives, part => MatchUuid(part, depth + 1));

            if (Unwrap(pattern) is string grouped)
                return MatchUuid(grouped, depth + 1);

            if (pattern.Length < 2 || pattern[0] != '^' || pattern[pattern.Length - 1] != '$')
                return null;

            return MatchBody(pattern.Substring(1, pattern.Length - 2), depth + 1);
        }

        /// <summary>
        /// Reads the inside of an anchored expression: the slots themselves, a group around them, or
        /// an alternation of either.
        /// </summary>
        /// <param name="body">The body, with the anchors already taken off.</param>
        /// <param name="depth">Bounds the recursion a nested group can drive.</param>
        /// <returns>The shape, or <c>null</c>.</returns>
        static UuidShape? MatchBody(string body, int depth)
        {
            if (depth > Nesting)
                return null;

            if (SplitAlternatives(body) is { Count: > 1 } alternatives)
                return Union(alternatives, part => MatchBody(part, depth + 1));

            if (Unwrap(body) is string grouped)
                return MatchBody(grouped, depth + 1);

            return MatchSlots(body);
        }

        /// <summary>
        /// How deep a pattern may nest groups and alternations before it is refused rather than read.
        /// </summary>
        const int Nesting = 8;

        /// <summary>
        /// Reads a run of single-character atoms as the nibbles and hyphens of a canonical UUID.
        /// </summary>
        /// <remarks>
        /// The layout is checked after the atoms are read rather than while they are, because a
        /// pattern is free to write the same language with its runs counted or spelled out and
        /// <see cref="Collapse"/> has already made those one spelling. What the layout asks is only
        /// that the hyphens land where a canonical UUID puts them.
        /// </remarks>
        /// <param name="body">The body.</param>
        /// <returns>The shape, or <c>null</c>.</returns>
        static UuidShape? MatchSlots(string body)
        {
            var braced = false;

            // A literal brace at the front cannot be a quantifier, nothing being there for it to
            // count, so it is read as the delimiter it is -- escaped or not, both spellings being in
            // use. The closing one is only looked for once the opening one is found, because a
            // trailing `}` is otherwise the end of a count.
            if (body.StartsWith(@"\{", StringComparison.Ordinal) || (body.Length > 0 && body[0] == '{'))
            {
                var open = body[0] == '{' ? 1 : 2;
                var close = body.EndsWith(@"\}", StringComparison.Ordinal) ? 2 : body.Length > 0 && body[body.Length - 1] == '}' ? 1 : 0;

                if (close == 0 || body.Length <= open + close)
                    return null;

                body = body.Substring(open, body.Length - open - close);
                braced = true;
            }

            var slots = new List<string?>();
            var i = 0;

            while (i < body.Length)
            {
                if (ReadAtom(body, ref i) is not string atom)
                    return null;

                var repeats = 1;

                if (i < body.Length && body[i] == '{')
                {
                    var close = body.IndexOf('}', i + 1);
                    if (close < 0)
                        return null;

                    // A range of widths is a different language, and a count wider than the shape
                    // cannot be one whatever else it is.
                    if (int.TryParse(body.Substring(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out repeats) == false || repeats > Nibbles)
                        return null;

                    i = close + 1;
                }

                // A quantifier admitting a range of widths ends the shape, whatever it quantifies.
                if (i < body.Length && (body[i] is '?' or '*' or '+'))
                    return null;

                string? slot;

                if (string.Equals(atom, "-", StringComparison.Ordinal) || string.Equals(atom, @"\-", StringComparison.Ordinal))
                    slot = null;
                else if (HexSet(atom) is string set)
                    slot = set;
                else
                    return null;

                for (var n = 0; n < repeats; n++)
                {
                    slots.Add(slot);

                    if (slots.Count > Nibbles + Hyphens.Length)
                        return null;
                }
            }

            // Hyphenated or not, and nothing in between: a shape carrying some of the four would give
            // one value a spelling per hyphen it happened to omit.
            var hyphenated = slots.Count == Nibbles + Hyphens.Length;

            if (hyphenated == false && slots.Count != Nibbles)
                return null;

            var read = new string[Nibbles];
            var nibble = 0;
            var hyphen = 0;

            foreach (var slot in slots)
            {
                if (slot is null)
                {
                    if (hyphen >= Hyphens.Length || Hyphens[hyphen] != nibble)
                        return null;

                    hyphen++;
                    continue;
                }

                if (nibble >= Nibbles)
                    return null;

                read[nibble] = slot;
                nibble++;
            }

            if (nibble != Nibbles || hyphen != (hyphenated ? Hyphens.Length : 0))
                return null;

            return new UuidShape(read, braced, hyphenated);
        }

        /// <summary>
        /// Reads the hex digits one atom admits, or returns <c>null</c> where it admits anything else.
        /// </summary>
        /// <remarks>
        /// A class is taken apart into its members rather than compared as text, which is the liberty
        /// <see cref="SortClass"/> takes one step further: <c>[89ab]</c>, <c>[8-9a-b]</c> and
        /// <c>[89a-b]</c> are one set written three ways, and a form that turned on which way would be
        /// recognising the writer rather than the language. A negation, an escape or a bare <c>-</c>
        /// is refused rather than guessed at, on the same grounds <see cref="SortClass"/> refuses it.
        /// </remarks>
        /// <param name="atom">A single-character atom.</param>
        /// <returns>The admitted digits, sorted and distinct, or <c>null</c>.</returns>
        static string? HexSet(string atom)
        {
            if (atom.Length == 1)
                return IsHex(atom[0]) ? atom : null;

            if (atom.Length < 3 || atom[0] != '[' || atom[atom.Length - 1] != ']')
                return null;

            var inner = atom.Substring(1, atom.Length - 2);

            if (inner[0] == '^' || inner.IndexOf('\\') >= 0)
                return null;

            var members = new SortedSet<char>();

            for (var i = 0; i < inner.Length; i++)
            {
                if (i + 2 < inner.Length && inner[i + 1] == '-')
                {
                    var low = inner[i];
                    var high = inner[i + 2];

                    if (low > high)
                        return null;

                    // A range is over code points rather than over hex digits, which is the trap this
                    // has to test for rather than around: `[8-f]` spans 0x38-0x66 and so admits `@`,
                    // `Z` and `_` beside the digits its writer meant -- and admits `A`-`F` as well as
                    // `a`-`f`, which is two spellings of one nibble. So every member is asked, not
                    // just the two ends.
                    for (var c = low; c <= high; c++)
                    {
                        if (IsHex(c) == false)
                            return null;

                        members.Add(c);
                    }

                    i += 2;
                    continue;
                }

                if (IsHex(inner[i]) == false)
                    return null;

                members.Add(inner[i]);
            }

            return string.Concat(members);
        }

        /// <summary>
        /// Determines whether a character spells a nibble, in either case.
        /// </summary>
        /// <param name="c">The character.</param>
        /// <returns><c>true</c> where it is a hex digit.</returns>
        static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        /// <summary>
        /// Takes the slot-wise union of several alternatives.
        /// </summary>
        /// <remarks>
        /// A union is what an alternation means, and it is the right thing to take because every claim
        /// a UUID form makes is a claim about the whole set of strings the pattern admits. A branch
        /// widening the 17th slot past <c>8</c>–<c>f</c> costs the confinement for the pattern rather
        /// than for the branch, which is exactly what the nil alternation should do.
        /// </remarks>
        /// <param name="alternatives">The branches.</param>
        /// <param name="match">Reads one branch.</param>
        /// <returns>The union, or <c>null</c> where a branch is not a canonical UUID.</returns>
        static UuidShape? Union(List<string> alternatives, Func<string, UuidShape?> match)
        {
            UuidShape? union = null;

            foreach (var alternative in alternatives)
            {
                if (match(alternative) is not UuidShape branch)
                    return null;

                if (union is null)
                {
                    union = branch;
                    continue;
                }

                // The branches have to agree on the spelling, because a value written one way in one
                // branch and another way in the other has two spellings and no equality survives it.
                if (union.Braced != branch.Braced || union.Hyphenated != branch.Hyphenated)
                    return null;

                for (var i = 0; i < Nibbles; i++)
                {
                    var members = new SortedSet<char>(union.Slots[i]);
                    members.UnionWith(branch.Slots[i]);
                    union.Slots[i] = string.Concat(members);
                }
            }

            return union;
        }

        /// <summary>
        /// Says which form a read shape is, or that it is none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The case test is the one thing the shape does not give.</b> A pattern admitting both
        /// <c>a</c> and <c>A</c> gives one value two spellings, and a comparison against either misses
        /// the documents written the other way — so a pattern drawing from both cases anywhere states
        /// nothing, which is the answer the enumerated rows gave <c>[0-9a-fA-F]</c> and this gives it
        /// for the same reason. A shape admitting no letter at all — the nil UUID alone — is read as
        /// lowercase, which costs nothing: the values it admits have no letters to spell either way.
        /// </para>
        /// <para>
        /// <b>Every slot is hex already</b>, so there is nothing left to check about the language.
        /// </para>
        /// </remarks>
        /// <param name="shape">The read shape.</param>
        /// <returns>The form, or <c>null</c>.</returns>
        static CosmosRepresentation? Classify(UuidShape shape)
        {
            var lower = false;
            var upper = false;

            foreach (var slot in shape.Slots)
                foreach (var c in slot)
                {
                    lower |= c is >= 'a' and <= 'f';
                    upper |= c is >= 'A' and <= 'F';
                }

            if (lower && upper)
                return null;

            var confined = Within(shape.Slots[HighSignNibble], '0', '7') && Within(shape.Slots[LowSignNibble], '8', 'f');

            return UuidForm(new UuidSpelling(shape.Braced, shape.Hyphenated, upper, confined));
        }

        /// <summary>
        /// Determines whether every digit a slot admits falls in a range, reading the two cases alike.
        /// </summary>
        /// <param name="slot">The admitted digits.</param>
        /// <param name="low">The range's first digit, in lowercase.</param>
        /// <param name="high">The range's last digit, in lowercase.</param>
        /// <returns><c>true</c> where the slot admits nothing outside the range.</returns>
        static bool Within(string slot, char low, char high)
        {
            foreach (var c in slot)
            {
                var folded = c is >= 'A' and <= 'F' ? (char)(c - 'A' + 'a') : c;

                if (folded < low || folded > high)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Splits an expression at the <c>|</c>s that are not inside a group or a character class.
        /// </summary>
        /// <param name="pattern">The expression.</param>
        /// <returns>The branches, which is a one-element list where there is no alternation.</returns>
        static List<string> SplitAlternatives(string pattern)
        {
            var parts = new List<string>();
            var depth = 0;
            var characters = false;
            var start = 0;

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (characters)
                {
                    if (c == ']')
                        characters = false;

                    continue;
                }

                switch (c)
                {
                    case '[':
                        characters = true;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case '|' when depth == 0:
                        parts.Add(pattern.Substring(start, i - start));
                        start = i + 1;
                        break;
                }
            }

            parts.Add(pattern.Substring(start));

            return parts;
        }

        /// <summary>
        /// Takes off a group that spans the whole expression, or returns <c>null</c> where none does.
        /// </summary>
        /// <remarks>
        /// Only a capturing group and a non-capturing one are taken off. A lookaround, an inline flag
        /// and a named group each change what the expression means or what it matches, and a
        /// recogniser that stripped them would be claiming a language it had not read — so they are
        /// refused, which costs a spelling and never mistakes one.
        /// </remarks>
        /// <param name="pattern">The expression.</param>
        /// <returns>The inside of the group, or <c>null</c>.</returns>
        static string? Unwrap(string pattern)
        {
            if (pattern.Length < 2 || pattern[0] != '(' || pattern[pattern.Length - 1] != ')')
                return null;

            var depth = 0;
            var characters = false;

            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];

                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (characters)
                {
                    if (c == ']')
                        characters = false;

                    continue;
                }

                if (c == '[')
                    characters = true;
                else if (c == '(')
                    depth++;
                else if (c == ')' && --depth == 0 && i != pattern.Length - 1)
                    return null;
            }

            if (depth != 0)
                return null;

            var inner = pattern.Substring(1, pattern.Length - 2);

            if (inner.StartsWith("?:", StringComparison.Ordinal))
                return inner.Substring(2);

            return inner.Length > 0 && inner[0] == '?' ? null : inner;
        }

        /// <summary>
        /// Writes a UUID the way a path in this form stores it, or returns <c>null</c> where the form
        /// is not a UUID at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked by a rewrite that has to put a literal into the stored spelling before comparing
        /// against it. Which spelling that is, is the whole of what the UUID forms differ by: the
        /// comparison is exact either way, and a container written in uppercase is addressable on the
        /// same terms as one written in lowercase.
        /// </para>
        /// <para>
        /// <b>A literal outside the confined form's own sign class is refused, and only then.</b> The
        /// confined rows say the lexical order <em>is</em> the engine's order under a signed
        /// comparison, and the argument for that is that each half's sign is constant — across the
        /// container. The literal is not in the container, and a comparison is against one of each: a
        /// path confined to a first digit of <c>0</c>–<c>7</c> holds only values whose high half is
        /// signed-positive, so <c>&gt;</c> against a literal whose high half is signed-negative is
        /// true of every document and lexically true of none. So the literal is asked for the two
        /// bits the confinement pins, and a rewrite is declined rather than answered wrongly.
        /// </para>
        /// <para>
        /// <b>It is asked only where the answer can differ</b>, which is the switch being off. Under
        /// <see cref="UnsignedUuidComparison"/> the lexical order is the engine's order for every pair
        /// of canonical spellings, in the container or out of it, so the class costs nothing and
        /// refusing on it would decline sound rewrites — including the equality ones, which never
        /// needed the sign at all and which push a predicate that correctly selects nothing.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path form.</param>
        /// <param name="value">The value to write.</param>
        /// <returns>The stored spelling, or <c>null</c> where this form does not store a UUID.</returns>
        public static string? RenderUuid(CosmosRepresentation representation, Guid value)
        {
            if (Uuids.TryGetValue(representation.Name, out var spelling) == false)
                return null;

            if (UnsignedUuidComparison == false && spelling.Confined && InSignClass(value) == false)
                return null;

            var text = value.ToString(spelling.Hyphenated ? "D" : "N");

            if (spelling.Upper)
                text = text.ToUpperInvariant();

            return spelling.Braced ? "{" + text + "}" : text;
        }

        /// <summary>
        /// Determines whether a value has the two sign bits the confined forms pin.
        /// </summary>
        /// <remarks>
        /// Read off the hyphenless spelling, where a nibble's index into the text is its own index and
        /// the two the confinement pins are therefore <see cref="HighSignNibble"/> and
        /// <see cref="LowSignNibble"/> exactly.
        /// </remarks>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> where the high half is signed-positive and the low half signed-negative.</returns>
        static bool InSignClass(Guid value)
        {
            var text = value.ToString("N");

            return Within(text.Substring(HighSignNibble, 1), '0', '7') && Within(text.Substring(LowSignNibble, 1), '8', 'f');
        }

        /// <summary>
        /// Determines whether a form spells a UUID, in any of the sixteen ways.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked where the spelling does not matter but the type does — a projection reading the
        /// stored text back as the <c>UUID</c> the plan declared, which needs to know only that the
        /// text is one. That is a claim about the reader rather than about this table, and it is
        /// measured: <c>SqlFunctions.stringToUuid</c> reads all four shapes — hyphenated, hyphenless,
        /// and either of those braced — in either case, so every form here parses back to the value
        /// the container means. It reads neither the <c>urn:uuid:</c> nor the parenthesised spelling,
        /// which is one of the reasons neither is a form here. <c>ShouldReadEverySpellingThisRecognises</c>
        /// is the measurement.
        /// </para>
        /// <para>
        /// Sortability is not consulted and must not be: it says whether the lexical order of the
        /// stored strings is the order Calcite compares the values in, which a projection never asks.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path form.</param>
        /// <returns><c>true</c> where the form stores a UUID.</returns>
        public static bool IsUuid(CosmosRepresentation representation) => Uuids.ContainsKey(representation.Name);

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

            // The UUID forms are decided rather than tabulated, for the reason RecogniseUuid gives:
            // the axes a UUID pattern varies along do not close, so a table reads most of the
            // spellings in the wild as stating nothing at all.
            if (RecogniseUuid(normalised) is CosmosRepresentation uuid)
                return uuid;

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
