using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
        /// <param name="Carries">
        /// Which halves of an instant the shape stores. A parse into a type holding less truncates,
        /// and a reader filling a type holding more invents — see <see cref="CosmosTemporalParts"/>.
        /// </param>
        /// <param name="ReadsBack">
        /// Whether <see cref="Client.CosmosJson"/> recovers the value from the stored text, which is
        /// what lets a projection send the path down and put the conversion on the reader. Measured:
        /// .NET's invariant <c>DateTime.TryParse</c> reads every extended ISO-8601 shape here and
        /// refuses the separator-less ones, so the basic forms are recognised and not projected.
        /// </param>
        /// <param name="Parses">
        /// The format strings a parse of this shape may be written with, and nothing else — see
        /// <see cref="ParsesExactly"/> for what membership claims and
        /// <c>CalciteTemporalParseMeasurementTests</c> for the measurement that pins every row.
        /// </param>
        sealed record TemporalForm(string? Format, Func<DateTime, bool> Exact, CosmosTemporalParts Carries, bool ReadsBack, IReadOnlyCollection<string> Parses);

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
            void Register(string pattern, string name, string? format, Func<DateTime, bool> exact, CosmosTemporalParts carries, bool readsBack, params string[] parses)
            {
                Temporal[name] = new TemporalForm(format, exact, carries, readsBack, parses);
                add(pattern, new CosmosRepresentation(name, PreservesEquality: true, PreservesOrder: true));
            }

            // One spelling per dialect and no mixtures. Whichever function was written, the format is
            // read by the same model -- measured -- and that model spells each element two ways, so
            // `%Y-%m-%d` and `YYYY-MM-DD` are both this shape's date. A mixture of the two would be as
            // sound and is not generated: it buys a spelling nobody writes at the price of a cross
            // product, and an unrecognised format costs a pushdown rather than an answer.
            const int Bq = 0;
            const int Pg = 1;

            string[] Cross(string[] left, string[] right)
            {
                var product = new List<string>(left.Length * right.Length);

                foreach (var l in left)
                    foreach (var r in right)
                        product.Add(l + r);

                return product.ToArray();
            }

            // Zero to nine fraction digits. A tick is seven, so a wider form is written by padding and
            // still holds every value exactly; a narrower one has to land on its own resolution, which
            // is what its predicate tests.
            //
            // **Only three digits can be parsed, and that is the whole of why the recommended shape
            // gets no parse.** Every fraction element the model has -- `%E1S` through `%E5S`, `FF1`
            // through `FF5`, `MS` -- lowers to a Java millisecond field, which reads whatever digits
            // are there as milliseconds: measured, `.678901` against any of them is 11 minutes and 18
            // seconds rather than 679 milliseconds. So a fraction of exactly three digits has
            // spellings and every other width has none, the microsecond and tick shapes included.
            var fractions = new (string Pattern, string Format, Func<DateTime, bool> Exact, string[][] Parses)[10];

            for (var n = 0; n < fractions.Length; n++)
            {
                var digits = n < 7 ? n : 7;
                var padding = n > 7 ? new string('0', n - 7) : string.Empty;

                var resolution = 1L;
                for (var i = 0; i < 7 - digits; i++)
                    resolution *= 10;

                var parses = n switch
                {
                    0 => new[] { new[] { string.Empty }, new[] { string.Empty } },
                    3 => new[] { new[] { ".%E3S" }, new[] { ".MS", ".FF3" } },
                    _ => new[] { Array.Empty<string>(), Array.Empty<string>() },
                };

                fractions[n] = (
                    n == 0 ? string.Empty : $@"\.[0-9]{{{n}}}",
                    n == 0 ? string.Empty : "." + new string('f', digits) + (padding.Length > 0 ? "'" + padding + "'" : string.Empty),
                    value => value.Ticks % resolution == 0,
                    parses);
            }

            // How the zero offset is spelled. `Z` and `+00:00` denote the same instant and sort
            // differently against each other, which is why a path may use either and not both.
            //
            // The offset is two spellings in a format because its characters are literal either way:
            // quoted, the model passes `'+00:00'` through as a Java literal; bare, `+`, `0` and `:`
            // are not pattern characters and mean themselves. `Z` has to be quoted, being one.
            var zones = new (string Pattern, string Format, string Name, string[] Parses)[]
            {
                ("Z", "'Z'", "z", new[] { "'Z'" }),
                (@"\+00:00", "'+00:00'", "offset", new[] { "'+00:00'", "+00:00" }),
                (string.Empty, string.Empty, "local", new[] { string.Empty }),
            };

            var dates = new[] { "%Y-%m-%d", "YYYY-MM-DD" };
            var clocks = new[] { "%H:%M:%S", "HH24:MI:SS" };
            var minutes = new[] { "%H:%M", "HH24:MI" };

            foreach (var zone in zones)
            {
                for (var n = 0; n < fractions.Length; n++)
                {
                    var fraction = fractions[n];

                    var spellings = new List<string>();
                    foreach (var dialect in new[] { Bq, Pg })
                        spellings.AddRange(Cross(
                            Cross(new[] { dates[dialect] + "'T'" + clocks[dialect] }, fraction.Parses[dialect]),
                            zone.Parses));

                    Register(
                        $"^[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}T[0-9]{{2}}:[0-9]{{2}}:[0-9]{{2}}{fraction.Pattern}{zone.Pattern}$",
                        InstantName(n, zone.Name),
                        "yyyy-MM-dd'T'HH:mm:ss" + fraction.Format + zone.Format,
                        fraction.Exact,
                        CosmosTemporalParts.Instant,
                        readsBack: true,
                        spellings.ToArray());

                    // The basic format, which drops the separators and is as fixed as the extended one
                    // -- and which no parse reads and no reader reads either. Measured: the model
                    // lowers `%H%M%S` to Java's one-letter fields, which are greedy, so `030405` is
                    // read as hour 0 and minute 945; and .NET's `DateTime.TryParse` refuses
                    // `20240102T030405Z` outright. Recognised, sorted by, and nothing else.
                    Register(
                        $"^[0-9]{{8}}T[0-9]{{6}}{fraction.Pattern}{zone.Pattern}$",
                        $"iso8601-basic-f{n}-{zone.Name}",
                        "yyyyMMdd'T'HHmmss" + fraction.Format + zone.Format,
                        fraction.Exact,
                        CosmosTemporalParts.Instant,
                        readsBack: false);
                }

                // Minute precision, which plenty of feeds write and which is fixed like any other.
                Register(
                    $"^[0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}T[0-9]{{2}}:[0-9]{{2}}{zone.Pattern}$",
                    $"iso8601-instant-minutes-{zone.Name}",
                    "yyyy-MM-dd'T'HH:mm" + zone.Format,
                    value => value.Ticks % TimeSpan.TicksPerMinute == 0,
                    CosmosTemporalParts.Instant,
                    readsBack: true,
                    Cross(new[] { dates[Bq] + "'T'" + minutes[Bq], dates[Pg] + "'T'" + minutes[Pg] }, zone.Parses));
            }

            // Calendar shapes, which store no time of day at all. A value carrying one does not land
            // on them, and is refused rather than truncated.
            //
            // `yyyy-MM-dd` is a spelling here and is one nowhere else, which is the measurement in a
            // line. The model reads it as year, month and day because it matches its elements without
            // regard to case -- and for the same reason it reads the `mm` of
            // `yyyy-MM-dd'T'HH:mm:ss'Z'` as a second *month*, so that shape reads January the 2nd at
            // 03:04:05 as April the 2nd at 03:00:05. A date has no minute to be mistaken for a month.
            Register("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", "iso8601-date", "yyyy-MM-dd", value => value.TimeOfDay == TimeSpan.Zero,
                CosmosTemporalParts.Date, readsBack: true, "%Y-%m-%d", "YYYY-MM-DD", "yyyy-MM-dd");

            Register("^[0-9]{8}$", "iso8601-date-basic", "yyyyMMdd", value => value.TimeOfDay == TimeSpan.Zero,
                CosmosTemporalParts.Date, readsBack: false, "%Y%m%d", "YYYYMMDD");

            Register("^[0-9]{4}-[0-9]{2}$", "iso8601-year-month", "yyyy-MM", value => value.Day == 1 && value.TimeOfDay == TimeSpan.Zero,
                CosmosTemporalParts.Date, readsBack: true, "%Y-%m", "YYYY-MM");

            // A time of day with no date. Recognised, because its lexical order is its chronological
            // order and a sort over one is sound on exactly those terms; not renderable, because
            // writing an instant into it would drop the date rather than refuse.
            for (var n = 0; n < fractions.Length; n++)
                Register($"^[0-9]{{2}}:[0-9]{{2}}:[0-9]{{2}}{fractions[n].Pattern}$", $"iso8601-time-f{n}", null, Never,
                    CosmosTemporalParts.Time, readsBack: true,
                    Cross(new[] { clocks[Bq] }, fractions[n].Parses[Bq]).Concat(Cross(new[] { clocks[Pg] }, fractions[n].Parses[Pg])).ToArray());

            Register("^[0-9]{2}:[0-9]{2}$", "iso8601-time-minutes", null, Never,
                CosmosTemporalParts.Time, readsBack: true, minutes);
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
        /// Determines whether reading this form's stored text with the given format, into a value
        /// holding the given halves of an instant, answers exactly the instant the text denotes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What membership claims, and why a table rather than a reading of the format.</b> A
        /// format string is not a description the adapter can interpret — it is an input to the
        /// engine's own parser, and what that parser does with it is measured rather than derived.
        /// The claim a row makes is the whole of what a rewrite needs: over every string this shape
        /// admits, the parse answers the instant the string denotes, one for one. Injective, because
        /// the shape has one spelling per value and the parse loses none of it; monotone, because the
        /// map is the true one. So the chain <c>PARSE(&lt;format&gt;, &lt;path&gt;)</c> preserves
        /// whatever the shape itself preserves, and the two bits on
        /// <see cref="CosmosRepresentation"/> answer for it unchanged.
        /// </para>
        /// <para>
        /// <b>A format outside the table says nothing, which is not the same as being wrong.</b>
        /// Measured, the engine accepts <c>yyyy-MM-dd'T'HH:mm:ss'Z'</c> over an instant and answers
        /// the wrong instant — it reads the <c>mm</c> as a second month — so a query written that way
        /// is already returning the wrong rows, in process, before any of this. Declining to push it
        /// leaves that answer exactly as it was; pushing it would replace one wrong answer with a
        /// different one.
        /// </para>
        /// <para>
        /// <b>And the type the function answers is half the question.</b> <c>PARSE_DATE</c> over a
        /// path storing a full instant reads the format faithfully and then throws the clock away, so
        /// many stored strings share one value and the equality a rewrite would lower is not the
        /// equality the query asked. That is refused here rather than in the caller, the format alone
        /// not being able to say it.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path's declared form.</param>
        /// <param name="format">The format the query wrote, or <c>null</c>.</param>
        /// <param name="held">The halves of an instant the value the parse answers can hold.</param>
        /// <returns><c>true</c> where the parse is the identity on this shape's values.</returns>
        public static bool ParsesExactly(CosmosRepresentation representation, string? format, CosmosTemporalParts held)
        {
            if (format is null || Temporal.TryGetValue(representation.Name, out var form) == false)
                return false;

            if ((form.Carries & ~held) != CosmosTemporalParts.None)
                return false;

            return form.Parses.Contains(format, StringComparer.Ordinal);
        }

        /// <summary>
        /// Returns every format a parse of this form may be written with.
        /// </summary>
        /// <remarks>
        /// <see cref="ParsesExactly"/> is what a rewrite asks, and this is what a <em>measurement</em>
        /// asks: a claim nothing enumerates is a claim nothing can be run against the engine, and the
        /// rows here are claims about the engine. <c>CalciteTemporalParseMeasurementTests</c> parses a
        /// sample of every shape with every spelling this returns.
        /// </remarks>
        /// <param name="representation">The path's declared form.</param>
        /// <returns>The formats, empty where the form has none and for a form that is not temporal.</returns>
        public static IReadOnlyCollection<string> ParseFormats(CosmosRepresentation representation) =>
            Temporal.TryGetValue(representation.Name, out var form) ? form.Parses : Array.Empty<string>();

        /// <summary>
        /// Determines whether the stored text can be sent down as it stands and converted by the
        /// reader, for a column holding the given halves of an instant.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two ways this fails, and neither is visible in the format.</b> The shape may be one the
        /// reader does not read at all — measured, .NET's invariant <c>DateTime.TryParse</c> refuses
        /// <c>20240102</c> and <c>20240102T030405Z</c>, so the separator-less shapes are out. Or the
        /// column may hold a half the shape does not carry, and then the reader fills it from
        /// somewhere: a time of day read back as a <c>TIMESTAMP</c> acquires <em>today's</em> date,
        /// where the engine's parse gives it the epoch's. A missing clock is not the same case — both
        /// sides put midnight there — so only a missing <em>date</em> is refused.
        /// </para>
        /// <para>
        /// This is the read side of <see cref="ParsesExactly"/> and is asked beside it rather than
        /// folded into it, because a filter needs the first alone: nothing comes back from a
        /// comparison for a reader to get wrong.
        /// </para>
        /// </remarks>
        /// <param name="representation">The path's declared form.</param>
        /// <param name="held">The halves of an instant the projected column holds.</param>
        /// <returns><c>true</c> where the reader answers what the engine's parse answers.</returns>
        public static bool ReadsBackAs(CosmosRepresentation representation, CosmosTemporalParts held)
        {
            if (Temporal.TryGetValue(representation.Name, out var form) == false || form.ReadsBack == false)
                return false;

            return (held & ~form.Carries & CosmosTemporalParts.Date) == CosmosTemporalParts.None;
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
