using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The temporal forms a declared <c>pattern</c> is recognised as, and what each one licenses.
    /// </summary>
    /// <remarks>
    /// <b>These are tabulated, and the UUID forms are not, because these axes close.</b> What makes a
    /// temporal shape usable is its <em>fixedness</em> rather than which shape it is — a fraction has
    /// a width, a zone has a spelling, a shape stores this much of an instant and no more — so the
    /// product is finite and <see cref="AddTemporal"/> generates it. A pattern outside it is refused
    /// rather than guessed at, which is what keeps a shape that <em>varies</em> from claiming an order
    /// it does not have.
    /// </remarks>
    public static class CosmosTemporalForms
    {

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
        /// How one temporal form writes a value, and which values it can write.
        /// </summary>
        /// <param name="Format">
        /// The spelling, or <c>null</c> where the form holds too little of an instant to be written
        /// one — a time of day carries no date, and rendering into it would drop one silently.
        /// </param>
        /// <param name="Exact">
        /// Whether a value lands on the form's own resolution. A form that cannot hold the value
        /// exactly renders nothing rather than a truncation; see <see cref="Render"/>.
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
        /// Every temporal language this knows, and nothing else. The UUID spellings were in here once,
        /// as eighty generated rows, and a table was the wrong shape for them — see
        /// <see cref="CosmosUuidForms.Recognise"/>, which decides a shape instead. What makes a table
        /// right <em>here</em> is that a temporal form has a width and a zone spelling and nothing
        /// else to vary.
        /// </remarks>
        /// <returns>The table.</returns>
        static Dictionary<string, CosmosRepresentation> Build()
        {
            var known = new Dictionary<string, CosmosRepresentation>(StringComparer.Ordinal);

            void Add(string pattern, CosmosRepresentation representation) => known[CosmosPatternLanguage.Normalise(pattern)!] = representation;

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
        /// Returns the temporal form a normalised pattern is recognised as, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// A lookup and nothing more, which is the whole claim these forms make: a shape is one of the
        /// generated languages or it is not one this knows.
        /// </remarks>
        /// <param name="normalised">The pattern, already normalised.</param>
        /// <returns>The representation, or <c>null</c>.</returns>
        internal static CosmosRepresentation? Recognise(string normalised) =>
            Known.TryGetValue(normalised, out var representation) ? representation : null;

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
        public static string? Render(CosmosRepresentation representation, DateTime value)
        {
            if (Temporal.TryGetValue(representation.Name, out var form) == false || form.Format is null)
                return null;

            return form.Exact(value) ? value.ToString(form.Format, CultureInfo.InvariantCulture) : null;
        }

    }

}
