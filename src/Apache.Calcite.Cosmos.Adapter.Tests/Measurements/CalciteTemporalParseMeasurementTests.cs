using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's <c>PARSE_</c> functions do with a format string, measured and asserted so that a
    /// change upstream is reported here rather than discovered in a pushed comparison.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the fact every spelling in <see cref="CosmosTemporalForms"/> rests on, and unlike
    /// the shapes beside it there was no deriving it.</b> A stored form is read out of a declared
    /// pattern by reasoning — the pattern <em>is</em> the language, and what its strings look like
    /// follows. A format string is not like that: it is an input to a parser the adapter does not own,
    /// and what that parser makes of it can only be asked. So every row the table carries is run here
    /// against a sample of the shape it claims, and a row that stops being exact fails a test rather
    /// than returning wrong rows.
    /// </para>
    /// <para>
    /// <b>And the first thing asking turned up was that the obvious spelling is silently wrong.</b>
    /// Calcite reads the format with its own format model and lowers it to a Java
    /// <c>SimpleDateFormat</c> pattern, and the model matches its elements without regard to case — so
    /// the <c>mm</c> of <c>yyyy-MM-dd'T'HH:mm:ss'Z'</c> is read as a second <em>month</em>, the pattern
    /// becomes <c>yyyy-MM-dd'T'HH:MM:s'Z'</c>, and January the 2nd at 03:04:05 comes back as April the
    /// 2nd at 03:00:05. No error, no null: a wrong instant. <c>DESIGN.md</c> recorded the opposite,
    /// having measured only that the call did not raise.
    /// </para>
    /// <para>
    /// <b>Measured at the runtime function and controlled through SQL.</b> The matrix calls
    /// <c>SqlFunctions.DateParseFunction</c> directly, which answers the epoch milliseconds the plan
    /// would carry and leaves no layer to misread; the control runs the same parses through Calcite's
    /// own JDBC driver, so that the function measured is the function a query reaches.
    /// </para>
    /// </remarks>
    public class CalciteTemporalParseMeasurementTests
    {

        /// <summary>
        /// One stored shape, as a container would declare it, with values conforming to it.
        /// </summary>
        /// <param name="Pattern">The declared <c>pattern</c>.</param>
        /// <param name="Name">The form the pattern is recognised as, asserted so the row cannot drift.</param>
        /// <param name="Samples">Stored spellings and the instants they denote.</param>
        sealed record Shape(string Pattern, string Name, (string Stored, string Denotes)[] Samples);

        /// <summary>
        /// Every shape a parse is claimed for, and the tick and microsecond shapes beside them, which
        /// are claimed for none.
        /// </summary>
        /// <remarks>
        /// The instants are chosen to separate the fields that get confused: a month that is not the
        /// minute, a minute that is not the second, a leap day, and a value on either side of the
        /// epoch.
        /// </remarks>
        static readonly Shape[] Shapes =
        {
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", "iso8601-utc-seconds", new[]
            {
                ("2024-01-02T03:04:05Z", "2024-01-02T03:04:05.000"),
                ("1969-12-31T23:59:59Z", "1969-12-31T23:59:59.000"),
                ("2024-02-29T12:00:00Z", "2024-02-29T12:00:00.000"),
            }),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$", "iso8601-utc-milliseconds", new[]
            {
                ("2024-01-02T03:04:05.678Z", "2024-01-02T03:04:05.678"),
                ("2024-01-02T03:04:05.007Z", "2024-01-02T03:04:05.007"),
            }),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\+00:00$", "iso8601-instant-f0-offset", new[]
            {
                ("2024-01-02T03:04:05+00:00", "2024-01-02T03:04:05.000"),
            }),
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$", "iso8601-instant-f0-local", new[]
            {
                ("2024-01-02T03:04:05", "2024-01-02T03:04:05.000"),
            }),
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}Z$", "iso8601-instant-minutes-z", new[]
            {
                ("2024-01-02T03:04Z", "2024-01-02T03:04:00.000"),
            }),
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", "iso8601-date", new[]
            {
                ("2024-01-02", "2024-01-02T00:00:00.000"),
                ("1969-12-31", "1969-12-31T00:00:00.000"),
            }),
            new("^[0-9]{8}$", "iso8601-date-basic", new[]
            {
                ("20240102", "2024-01-02T00:00:00.000"),
            }),
            new("^[0-9]{4}-[0-9]{2}$", "iso8601-year-month", new[]
            {
                ("2024-01", "2024-01-01T00:00:00.000"),
            }),
            new("^[0-9]{2}:[0-9]{2}:[0-9]{2}$", "iso8601-time-f0", new[]
            {
                ("03:04:05", "1970-01-01T03:04:05.000"),
            }),
            new("^[0-9]{2}:[0-9]{2}$", "iso8601-time-minutes", new[]
            {
                ("03:04", "1970-01-01T03:04:00.000"),
            }),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z$", "iso8601-utc-microseconds", new[]
            {
                ("2024-01-02T03:04:05.678901Z", "2024-01-02T03:04:05.678901"),
            }),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$", "iso8601-utc-ticks", new[]
            {
                ("2024-01-02T03:04:05.6789012Z", "2024-01-02T03:04:05.6789012"),
            }),
            new("^[0-9]{8}T[0-9]{6}Z$", "iso8601-basic-f0-z", new[]
            {
                ("20240102T030405Z", "2024-01-02T03:04:05.000"),
            }),
        };

        /// <summary>
        /// The shapes with no parse spelling at all, and the plausible format each would be written
        /// with if one existed.
        /// </summary>
        /// <remarks>
        /// The refusal is asserted <em>and</em> measured. Saying that the table lists nothing here
        /// proves only that nothing was written down; running the format the caller would reach for is
        /// what says the refusal is a fact about the engine.
        /// </remarks>
        static readonly (string Name, string Format)[] Unspellable =
        {
            ("iso8601-utc-microseconds", @"%Y-%m-%d'T'%H:%M:%S.%E6S'Z'"),
            ("iso8601-utc-ticks", @"YYYY-MM-DD'T'HH24:MI:SS.FF7'Z'"),
            ("iso8601-basic-f0-z", @"%Y%m%d'T'%H%M%S'Z'"),
        };

        static CosmosRepresentation Form(string pattern)
        {
            var representation = CosmosStoredForms.Recognise(pattern);
            representation.Should().NotBeNull("the shape has to be recognised before anything is claimed about parsing it: " + pattern);
            return representation!.Value;
        }

        /// <summary>
        /// Calls <c>SqlFunctions.DateParseFunction.parseDatetime</c>, the function every
        /// <c>PARSE_</c> spelling is a truncation of.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <param name="text">The stored text.</param>
        /// <returns>The instant, or <c>null</c> where the parse raised.</returns>
        static DateTime? Parse(string format, string text)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "calcite.core")
                .GetType("org.apache.calcite.runtime.SqlFunctions+DateParseFunction");

            type.Should().NotBeNull("the function the PARSE_ family is implemented by has moved");

            var instance = Activator.CreateInstance(type!, nonPublic: true)!;
            var method = type!.GetMethod("parseDatetime", new[] { typeof(string), typeof(string) })!;

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)method.Invoke(instance, new object[] { format, text })!).UtcDateTime;
            }
            catch (Exception)
            {
                // Every failure mode is one: an unreadable format raises IllegalArgumentException out
                // of SimpleDateFormat, and text the format does not fit raises the engine's own
                // "Invalid format". Neither is a value, which is all this has to report.
                return null;
            }
        }

        static DateTime Instant(string text) =>
            DateTime.ParseExact(text, new[] { "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.ffffff", "yyyy-MM-ddTHH:mm:ss.fffffff" },
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        /// <summary>
        /// Every spelling the table claims reads its shape exactly.
        /// </summary>
        /// <remarks>
        /// The soundness direction, and the one a wrong answer would come through. A spelling listed
        /// for a form is a promise that parsing any string the form admits answers the instant that
        /// string denotes — which is what makes the chain preserve whatever the form preserves, and
        /// what lets a comparison be lowered onto the stored strings.
        /// </remarks>
        [Fact]
        public void EverySpellingTheTableClaimsReadsItsShapeExactly()
        {
            var claimed = 0;

            foreach (var shape in Shapes)
            {
                var representation = Form(shape.Pattern);
                representation.Name.Should().Be(shape.Name, "for " + shape.Pattern);

                foreach (var format in CosmosStoredForms.ParseFormats(representation))
                {
                    foreach (var (stored, denotes) in shape.Samples)
                        Parse(format, stored).Should().Be(Instant(denotes),
                            $"'{format}' is listed for {shape.Name}, so it has to read '{stored}'");

                    claimed++;
                }
            }

            claimed.Should().Be(24, "the shapes above carry exactly these spellings between them, and a row appearing or "
                + "vanishing without this number moving would be a claim nothing measured");
        }

        /// <summary>
        /// A shape with no spelling has none because none works.
        /// </summary>
        [Fact]
        public void AShapeWithNoSpellingHasNoneBecauseNoneWorks()
        {
            foreach (var (name, format) in Unspellable)
            {
                var shape = Shapes.Single(s => s.Name == name);
                var representation = Form(shape.Pattern);

                CosmosStoredForms.ParseFormats(representation).Should().BeEmpty("nothing is claimed for " + name);

                foreach (var (stored, denotes) in shape.Samples)
                    Parse(format, stored).Should().NotBe(Instant(denotes),
                        $"and '{format}' is what a caller would write for {name}, which does not read it");
            }
        }

        /// <summary>
        /// The Java spelling reads an instant as the wrong instant, which is the whole reason the
        /// table exists.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>yyyy-MM-dd'T'HH:mm:ss'Z'</c> is the format a caller writes, and it does not raise. It
        /// answers April where January was stored, because the model reads its <c>mm</c> as a second
        /// month and leaves the minute unset. The same spelling over a <em>date</em> is correct, there
        /// being no minute in a date to be mistaken for a month — and that is why
        /// <c>iso8601-date</c> lists it and nothing else does.
        /// </para>
        /// <para>
        /// The consequence for the adapter is a refusal rather than a fix: a query written this way is
        /// already returning the wrong rows in process, and declining to push it leaves that answer
        /// exactly where it is.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheJavaSpellingOfAnInstantIsReadAsTheWrongInstant()
        {
            const string Java = @"yyyy-MM-dd'T'HH:mm:ss'Z'";

            Parse(Java, "2024-01-02T03:04:05Z").Should().Be(Instant("2024-04-02T03:00:05.000"),
                "the mm is read as a second month and the minute is never set");

            var seconds = Form("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$");
            CosmosStoredForms.ParsesExactly(seconds, Java, CosmosTemporalParts.Instant).Should().BeFalse(
                "so the chain is not an equivalence and nothing is lowered onto it");

            var date = Form("^[0-9]{4}-[0-9]{2}-[0-9]{2}$");
            Parse("yyyy-MM-dd", "2024-01-02").Should().Be(Instant("2024-01-02T00:00:00.000"));
            CosmosStoredForms.ParsesExactly(date, "yyyy-MM-dd", CosmosTemporalParts.Date).Should().BeTrue(
                "while over a date the same spelling is exact, a date having no minute");
        }

        /// <summary>
        /// A parse into a type that holds less than the shape carries is refused, however faithfully
        /// the format reads it.
        /// </summary>
        /// <remarks>
        /// <c>PARSE_DATE</c> over a path storing a full instant reads the format exactly and then
        /// throws the clock away, so every instant on a day shares one value. Lowering an equality
        /// onto the stored strings would then ask a finer question than the query did. Measured: the
        /// engine's own <c>parseDate</c> answers the day and loses the rest.
        /// </remarks>
        [Fact]
        public void AParseIntoATypeHoldingLessIsRefused()
        {
            var seconds = Form("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$");
            const string Format = @"%Y-%m-%d'T'%H:%M:%S'Z'";

            CosmosStoredForms.ParsesExactly(seconds, Format, CosmosTemporalParts.Instant).Should().BeTrue(
                "the format reads the shape");

            CosmosStoredForms.ParsesExactly(seconds, Format, CosmosTemporalParts.Date).Should().BeFalse(
                "and a DATE cannot hold what it read");

            Jdbc($"PARSE_DATE('{Format.Replace("'", "''")}', '2024-01-02T03:04:05Z')")
                .Should().Be("2024-01-02", "which the engine confirms by answering the day alone");
        }

        /// <summary>
        /// Where the table says a shape reads back, the reader answers what the engine's parse
        /// answers; where it says otherwise, it does not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what licenses <em>projecting</em> a parse. The column goes down as the stored text
        /// and comes back through <see cref="CosmosJson"/>, so the value the query sees is that
        /// conversion rather than the engine's parse — and the two are different functions that have
        /// to agree. Both ways they can disagree are exercised here rather than reasoned about: a
        /// shape .NET's parser refuses outright, and a column holding a half the shape does not carry,
        /// where the reader fills the date from <em>today</em> and the engine from the epoch.
        /// </para>
        /// <para>
        /// The internal encodings are Calcite's: a <c>DATE</c> is a day count, a <c>TIME</c> a
        /// millisecond-of-day, a <c>TIMESTAMP</c> a millisecond count — which is what the reader
        /// answers and what a plan carries.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheReaderAnswersWhatTheParseAnswersWhereTheTableSaysSo()
        {
            var agreed = 0;
            var refused = 0;

            foreach (var shape in Shapes)
            {
                var representation = Form(shape.Pattern);
                var formats = CosmosStoredForms.ParseFormats(representation);

                if (formats.Count == 0)
                    continue;

                // Any of them; the reader is asked about the stored text and knows nothing of the
                // format. Which formats read the shape is the matrix above.
                var format = formats.First();

                foreach (var (held, type) in new[]
                {
                    (CosmosTemporalParts.Date, org.apache.calcite.sql.type.SqlTypeName.DATE),
                    (CosmosTemporalParts.Time, org.apache.calcite.sql.type.SqlTypeName.TIME),
                    (CosmosTemporalParts.Instant, org.apache.calcite.sql.type.SqlTypeName.TIMESTAMP),
                })
                {
                    if (CosmosStoredForms.ParsesExactly(representation, format, held) == false)
                        continue;

                    var reads = CosmosStoredForms.ReadsBackAs(representation, held);

                    foreach (var (stored, _) in shape.Samples)
                    {
                        var parsed = Parse(format, stored);
                        parsed.Should().NotBeNull($"'{format}' is listed for {shape.Name}");

                        // Rendered rather than boxed, the reader's integral type per column being
                        // Calcite's business rather than this measurement's.
                        var expected = Internal(parsed!.Value, type).ToString();
                        var read = ReadBack(stored, type)?.ToString();

                        if (reads)
                        {
                            read.Should().Be(expected,
                                $"{shape.Name} reads back as {type}, so '{stored}' has to answer the parse's value");
                            agreed++;
                        }
                        else
                        {
                            read.Should().NotBe(expected,
                                $"{shape.Name} does not read back as {type}, and the table would be claiming nothing if it did");
                            refused++;
                        }
                    }
                }
            }

            agreed.Should().BeGreaterThan(0);
            refused.Should().BeGreaterThan(0, "the basic date and the time of day read as a TIMESTAMP are the two that disagree");
        }

        /// <summary>
        /// Calcite's internal encoding of an instant, per type.
        /// </summary>
        static object Internal(DateTime value, org.apache.calcite.sql.type.SqlTypeName type)
        {
            if (type == org.apache.calcite.sql.type.SqlTypeName.DATE)
                return DateOnly.FromDateTime(value).DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

            if (type == org.apache.calcite.sql.type.SqlTypeName.TIME)
                return (int)value.TimeOfDay.TotalMilliseconds;

            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Reads a stored string back the way a projected column is read, or <c>null</c> where the
        /// reader refuses it.
        /// </summary>
        static object? ReadBack(string stored, org.apache.calcite.sql.type.SqlTypeName type)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(stored));

            try
            {
                return CosmosJson.GetValue(document.RootElement, type);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The control: the same parses through Calcite's own SQL, so that the function measured above
        /// is the function a query reaches.
        /// </summary>
        /// <remarks>
        /// <c>PARSE_DATETIME</c> and <c>PARSE_TIMESTAMP</c> are measured together because the second
        /// is the one a caller reaches for and the one the adapter declines — its
        /// <c>TIMESTAMP WITH LOCAL TIME ZONE</c> type is the reason, not its parse, and this is what
        /// says the parse is the same.
        /// </remarks>
        [Fact]
        public void TheSqlPathReadsWhatTheRuntimeFunctionReads()
        {
            const string Format = @"%Y-%m-%d'T'%H:%M:%S'Z'";
            var escaped = Format.Replace("'", "''");

            Jdbc($"PARSE_DATETIME('{escaped}', '2024-01-02T03:04:05Z')").Should().Be("2024-01-02 03:04:05.0");
            Jdbc($"PARSE_TIMESTAMP('{escaped}', '2024-01-02T03:04:05Z')").Should().Be("2024-01-02 03:04:05.0");
            Jdbc(@"PARSE_DATETIME('yyyy-MM-dd''T''HH:mm:ss''Z''', '2024-01-02T03:04:05Z')").Should().Be("2024-04-02 03:00:05.0",
                "including the wrong answer, which is the engine's and not a wrapper's");
        }

        /// <summary>
        /// Evaluates an expression through Calcite's own JDBC driver with every function library on.
        /// </summary>
        /// <param name="expression">The expression.</param>
        /// <returns>The value, rendered.</returns>
        static string? Jdbc(string expression)
        {
            java.lang.Class.forName("org.apache.calcite.jdbc.Driver");

            var properties = new java.util.Properties();
            properties.setProperty("fun", "all");

            var connection = java.sql.DriverManager.getConnection("jdbc:calcite:", properties);

            try
            {
                // Over a column rather than over the bare literal, so the answer is the one the
                // generated plan computes rather than one the planner folded.
                var results = connection.createStatement().executeQuery(
                    "SELECT " + expression + " AS v FROM (VALUES (1)) AS t(x) WHERE x = 1");

                return results.next() ? results.getObject(1)?.ToString() : null;
            }
            finally
            {
                connection.close();
            }
        }

    }

}
