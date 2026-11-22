using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite reads the strings of a declared temporal form as, through every conversion a form is read through:
    /// the parse family with every format the temporal form table lists, and the plain casts, over the strings that
    /// roll a field past its range as well as the canonical ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A form is a language, and a conversion is asked over all of it.</b> Every registered temporal form is a run of
    /// digits per field, so <c>2016-12-31T24:00:00Z</c>, <c>2016-12-31T23:60:00Z</c> and <c>2016-11-61T00:00:00Z</c> are
    /// strings of the seconds form as surely as <c>2017-01-01T00:00:00Z</c> is. A sample of canonical strings says what
    /// a conversion reads where the fields are in range and nothing about the rest, which is where a lowering onto the
    /// stored strings is wrong: the three above read as the fourth's instant, and none of them is equal to it as text.
    /// </para>
    /// <para>
    /// <b>Calcite's own literal is the oracle.</b> Every reading is asserted as an equality with a literal of the
    /// conversion's type, evaluated by Calcite, whose values are days of the proleptic Gregorian calendar: a literal before
    /// 1582-10-15 is the day of that spelling, and one in the gap, <c>1582-10-05</c> to <c>1582-10-14</c>, is refused
    /// at parsing, so a reading there, and one before year one, is written as a number of days before a literal. The
    /// strings are read from documents through <c>JSON_VALUE</c> over a scanned table (<see cref="MaterialisedTable"/>),
    /// so nothing is folded at planning.
    /// </para>
    /// <para>
    /// The cases are those the earlier probes of the leniency found: <c>24:00:00</c>, a minute or second of <c>60</c>, 30 February, day
    /// and month <c>00</c>, <c>0001-01-00</c>, year <c>0000</c>, the 1582 gap and a leap year's 29 February, beside the
    /// boundaries of each form; and two this measurement found, 29 February of 0100 and of 1500, years before 1582 the
    /// Julian calendar holds leap and the Gregorian does not, over which the parse family raises where it rolls that of
    /// 1900, and the years before 1582-10-16, over which <c>CAST … AS TIMESTAMP FORMAT</c> parts from the rest of the
    /// family.
    /// </para>
    /// </remarks>
    public class CalciteTemporalLeniencyMeasurementTests
    {

        /// <summary>The text a conversion reads, each document's <c>s</c>.</summary>
        const string Text = "JSON_VALUE(\"d\", '$.s')";

        /// <summary>The fields a form stores, and so the cases it is asked over.</summary>
        enum Family
        {
            /// <summary>A date and a clock to the second.</summary>
            Instant,

            /// <summary>A date and a clock to the minute.</summary>
            Minutes,

            /// <summary>A calendar date.</summary>
            Date,

            /// <summary>A year and a month.</summary>
            YearMonth,

            /// <summary>A clock to the second.</summary>
            Clock,

            /// <summary>A clock to the minute.</summary>
            ClockMinutes,
        }

        /// <summary>A conversion a form is read through with a format, by the type it answers.</summary>
        enum Conversion
        {
            /// <summary><c>PARSE_DATETIME</c>, a <c>TIMESTAMP</c>.</summary>
            ParseDatetime,

            /// <summary><c>PARSE_DATE</c>, a <c>DATE</c>.</summary>
            ParseDate,

            /// <summary><c>PARSE_TIME</c>, a <c>TIME</c>.</summary>
            ParseTime,

            /// <summary><c>CAST … AS TIMESTAMP(p) FORMAT</c>, at the precision the format reads.</summary>
            CastFormatTimestamp,

            /// <summary><c>CAST … AS DATE FORMAT</c>.</summary>
            CastFormatDate,

            /// <summary><c>CAST … AS TIME(p) FORMAT</c>, at the precision the format reads.</summary>
            CastFormatTime,
        }

        /// <summary>
        /// One registered temporal form the table lists a format for, as a container would declare it.
        /// </summary>
        /// <param name="Pattern">The declared <c>pattern</c>, recognised as <paramref name="Name"/>.</param>
        /// <param name="Name">The form's name, asserted so the row cannot drift.</param>
        /// <param name="Family">The fields it stores.</param>
        /// <param name="Fraction">Whether it stores three fraction digits.</param>
        /// <param name="Zone">How it spells the zero offset, empty for none.</param>
        /// <param name="Basic">Whether it drops the date's separators.</param>
        sealed record Form(string Pattern, string Name, Family Family, bool Fraction = false, string Zone = "", bool Basic = false)
        {

            /// <summary>Writes a case's fields, and a fraction where the form stores one, as a string of the form.</summary>
            public string Write(string fields, string fraction) =>
                (Basic ? fields.Replace("-", string.Empty) : fields) + (Fraction ? "." + fraction : string.Empty) + Zone;

        }

        /// <summary>
        /// One string of a family, written by its fields in the family's extended layout, and what a conversion reads it as.
        /// </summary>
        /// <param name="Fields">The fields, a field past its range among them where the case rolls one.</param>
        /// <param name="Reads">
        /// The instant the parse family reads, <c>yyyy-MM-dd HH:mm:ss</c>, as Calcite's literal spells it; or <c>null</c>
        /// where it raises.
        /// </param>
        /// <param name="DaysBefore">Days before <paramref name="Reads"/>, for a reading no literal spells.</param>
        sealed record Case(string Fields, string? Reads, int DaysBefore = 0)
        {

            /// <summary>Whether the case's string is canonical, one whose fields are all in range and read as written.</summary>
            public bool Canonical { get; init; } = true;

        }

        static Case Alias(string fields, string? reads, int daysBefore = 0) => new(fields, reads, daysBefore) { Canonical = false };

        /// <summary>
        /// Every registered temporal form the table lists a format for, which <see cref="TheFormsAreEveryFormAFormatIsListedFor"/>
        /// holds to the table.
        /// </summary>
        static readonly Form[] Forms =
        [
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", "iso8601-utc-seconds", Family.Instant, Zone: "Z"),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$", "iso8601-utc-milliseconds", Family.Instant, Fraction: true, Zone: "Z"),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\+00:00$", "iso8601-instant-f0-offset", Family.Instant, Zone: "+00:00"),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}\+00:00$", "iso8601-instant-f3-offset", Family.Instant, Fraction: true, Zone: "+00:00"),
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$", "iso8601-instant-f0-local", Family.Instant),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}$", "iso8601-instant-f3-local", Family.Instant, Fraction: true),
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}Z$", "iso8601-instant-minutes-z", Family.Minutes, Zone: "Z"),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}\+00:00$", "iso8601-instant-minutes-offset", Family.Minutes, Zone: "+00:00"),
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}$", "iso8601-instant-minutes-local", Family.Minutes),
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", "iso8601-date", Family.Date),
            new("^[0-9]{8}$", "iso8601-date-basic", Family.Date, Basic: true),
            new("^[0-9]{4}-[0-9]{2}$", "iso8601-year-month", Family.YearMonth),
            new("^[0-9]{2}:[0-9]{2}:[0-9]{2}$", "iso8601-time-f0", Family.Clock),
            new(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}$", "iso8601-time-f3", Family.Clock, Fraction: true),
            new("^[0-9]{2}:[0-9]{2}$", "iso8601-time-minutes", Family.ClockMinutes),
        ];

        /// <summary>
        /// The cases of each family: canonical strings at the boundaries and beside them, and strings that roll a field.
        /// </summary>
        static readonly Dictionary<Family, Case[]> Cases = new()
        {
            [Family.Instant] =
            [
                new("2024-02-29T12:00:00", "2024-02-29 12:00:00"),
                new("2024-01-02T03:04:05", "2024-01-02 03:04:05"),
                new("2017-01-01T00:00:00", "2017-01-01 00:00:00"),
                new("1970-01-01T00:00:00", "1970-01-01 00:00:00"),
                new("1969-12-31T23:59:59", "1969-12-31 23:59:59"),
                new("0001-01-01T00:00:00", "0001-01-01 00:00:00"),
                new("9999-12-31T23:59:59", "9999-12-31 23:59:59"),
                new("1582-10-16T00:00:00", "1582-10-16 00:00:00"),
                new("1582-10-15T00:00:00", "1582-10-15 00:00:00"),
                new("1582-10-04T00:00:00", "1582-10-04 00:00:00"),
                new("1500-01-01T00:00:00", "1500-01-01 00:00:00"),
                new("0100-03-01T00:00:00", "0100-03-01 00:00:00"),
                new("1500-03-01T00:00:00", "1500-03-01 00:00:00"),
                new("1900-03-01T00:00:00", "1900-03-01 00:00:00"),
                Alias("2016-12-31T24:00:00", "2017-01-01 00:00:00"),
                Alias("2016-12-31T23:60:00", "2017-01-01 00:00:00"),
                Alias("2016-12-31T23:59:60", "2017-01-01 00:00:00"),
                Alias("2016-11-61T00:00:00", "2016-12-31 00:00:00"),
                Alias("2023-02-29T12:00:00", "2023-03-01 12:00:00"),
                Alias("1900-02-29T00:00:00", "1900-03-01 00:00:00"),
                Alias("2100-02-29T00:00:00", "2100-03-01 00:00:00"),
                Alias("2023-02-30T00:00:00", "2023-03-02 00:00:00"),
                Alias("2023-13-01T00:00:00", "2024-01-01 00:00:00"),
                Alias("2023-00-15T00:00:00", "2022-12-15 00:00:00"),
                Alias("2024-03-00T00:00:00", "2024-02-29 00:00:00"),
                Alias("2023-02-28T99:99:99", "2023-03-04 04:40:39"),
                Alias("0000-01-01T00:00:00", "0001-01-01 00:00:00"),
                Alias("0001-01-00T00:00:00", "0001-12-31 00:00:00"),
                Alias("1500-02-29T00:00:00", null),
                Alias("0100-02-29T00:00:00", null),
                Alias("1582-10-14T00:00:00", "1582-10-24 00:00:00"),
                Alias("1582-10-05T00:00:00", "1582-10-15 00:00:00"),
            ],
            [Family.Minutes] =
            [
                new("2024-02-29T12:00", "2024-02-29 12:00:00"),
                new("2024-01-02T03:04", "2024-01-02 03:04:00"),
                new("1970-01-01T00:00", "1970-01-01 00:00:00"),
                new("0001-01-01T00:00", "0001-01-01 00:00:00"),
                new("9999-12-31T23:59", "9999-12-31 23:59:00"),
                new("1582-10-16T00:00", "1582-10-16 00:00:00"),
                new("1582-10-15T00:00", "1582-10-15 00:00:00"),
                new("1582-10-04T00:00", "1582-10-04 00:00:00"),
                new("1500-01-01T00:00", "1500-01-01 00:00:00"),
                new("0100-03-01T00:00", "0100-03-01 00:00:00"),
                new("1500-03-01T00:00", "1500-03-01 00:00:00"),
                new("1900-03-01T00:00", "1900-03-01 00:00:00"),
                Alias("2016-12-31T24:00", "2017-01-01 00:00:00"),
                Alias("2016-12-31T23:60", "2017-01-01 00:00:00"),
                Alias("2016-11-61T00:00", "2016-12-31 00:00:00"),
                Alias("2023-02-30T00:00", "2023-03-02 00:00:00"),
                Alias("1900-02-29T00:00", "1900-03-01 00:00:00"),
                Alias("2023-13-01T00:00", "2024-01-01 00:00:00"),
                Alias("2023-00-15T00:00", "2022-12-15 00:00:00"),
                Alias("2024-03-00T00:00", "2024-02-29 00:00:00"),
                Alias("2023-02-28T99:99", "2023-03-04 04:39:00"),
                Alias("0000-01-01T00:00", "0001-01-01 00:00:00"),
                Alias("0001-01-00T00:00", "0001-12-31 00:00:00"),
                Alias("1500-02-29T00:00", null),
                Alias("1582-10-14T00:00", "1582-10-24 00:00:00"),
            ],
            [Family.Date] =
            [
                new("2024-02-29", "2024-02-29 00:00:00"),
                new("2024-01-02", "2024-01-02 00:00:00"),
                new("1970-01-01", "1970-01-01 00:00:00"),
                new("1969-12-31", "1969-12-31 00:00:00"),
                new("0001-01-01", "0001-01-01 00:00:00"),
                new("9999-12-31", "9999-12-31 00:00:00"),
                new("1582-10-16", "1582-10-16 00:00:00"),
                new("1582-10-15", "1582-10-15 00:00:00"),
                new("1582-10-04", "1582-10-04 00:00:00"),
                new("1500-01-01", "1500-01-01 00:00:00"),
                new("0100-03-01", "0100-03-01 00:00:00"),
                new("1500-03-01", "1500-03-01 00:00:00"),
                new("1900-03-01", "1900-03-01 00:00:00"),
                Alias("2016-11-61", "2016-12-31 00:00:00"),
                Alias("2023-02-29", "2023-03-01 00:00:00"),
                Alias("1900-02-29", "1900-03-01 00:00:00"),
                Alias("2100-02-29", "2100-03-01 00:00:00"),
                Alias("2023-02-30", "2023-03-02 00:00:00"),
                Alias("2023-13-01", "2024-01-01 00:00:00"),
                Alias("2023-00-15", "2022-12-15 00:00:00"),
                Alias("2024-03-00", "2024-02-29 00:00:00"),
                Alias("0000-01-01", "0001-01-01 00:00:00"),
                Alias("0001-01-00", "0001-12-31 00:00:00"),
                Alias("1500-02-29", null),
                Alias("0100-02-29", null),
                Alias("1582-10-14", "1582-10-24 00:00:00"),
                Alias("1582-10-05", "1582-10-15 00:00:00"),
            ],
            [Family.YearMonth] =
            [
                new("2024-02", "2024-02-01 00:00:00"),
                new("1970-01", "1970-01-01 00:00:00"),
                new("0001-01", "0001-01-01 00:00:00"),
                new("9999-12", "9999-12-01 00:00:00"),
                new("1582-11", "1582-11-01 00:00:00"),
                new("1582-10", "1582-10-01 00:00:00"),
                new("1500-01", "1500-01-01 00:00:00"),
                Alias("2023-13", "2024-01-01 00:00:00"),
                Alias("2023-00", "2022-12-01 00:00:00"),
                Alias("0000-01", "0001-01-01 00:00:00"),
            ],
            [Family.Clock] =
            [
                new("00:00:00", "1970-01-01 00:00:00"),
                new("23:59:59", "1970-01-01 23:59:59"),
                new("03:04:05", "1970-01-01 03:04:05"),
                new("12:30:00", "1970-01-01 12:30:00"),
                Alias("24:00:00", "1970-01-02 00:00:00"),
                Alias("12:60:00", "1970-01-01 13:00:00"),
                Alias("12:30:60", "1970-01-01 12:31:00"),
                Alias("99:99:99", "1970-01-05 04:40:39"),
            ],
            [Family.ClockMinutes] =
            [
                new("00:00", "1970-01-01 00:00:00"),
                new("23:59", "1970-01-01 23:59:00"),
                new("03:04", "1970-01-01 03:04:00"),
                new("12:30", "1970-01-01 12:30:00"),
                Alias("24:00", "1970-01-02 00:00:00"),
                Alias("12:60", "1970-01-01 13:00:00"),
                Alias("99:99", "1970-01-05 04:39:00"),
            ],
        };

        /// <summary>The fractions a form storing three digits is written with, beside each case's fields.</summary>
        static readonly string[] Fractions = ["000", "999"];

        /// <summary>
        /// Where <c>CAST … AS TIMESTAMP FORMAT</c> parts from the rest of the parse family, by family and fields: what it
        /// reads instead, or <c>null</c> where it raises. It reads every other case as the family does, and
        /// <c>CAST … AS DATE FORMAT</c> and <c>AS TIME FORMAT</c> part from it nowhere.
        /// </summary>
        /// <remarks>
        /// Every entry is before 1582-10-16: year one is read as year two and year zero as year three, 1582-10-15 as the day
        /// ten days before it and a day in the gap as the day it spells, and 1 March of a year the Julian calendar holds
        /// leap and the Gregorian does not as a raise, its 29 February as 1 March.
        /// </remarks>
        static readonly Dictionary<(Family Family, string Fields), (string? Reads, int DaysBefore)> TimestampFormatReadsOtherwise = new()
        {
            [(Family.Instant, "0001-01-01T00:00:00")] = ("0002-01-01 00:00:00", 0),
            [(Family.Instant, "0000-01-01T00:00:00")] = ("0003-01-01 00:00:00", 0),
            [(Family.Instant, "1582-10-15T00:00:00")] = ("1582-10-15 00:00:00", 10),
            [(Family.Instant, "1582-10-05T00:00:00")] = ("1582-10-15 00:00:00", 10),
            [(Family.Instant, "0100-03-01T00:00:00")] = (null, 0),
            [(Family.Instant, "1500-03-01T00:00:00")] = (null, 0),
            [(Family.Instant, "1500-02-29T00:00:00")] = ("1500-03-01 00:00:00", 0),
            [(Family.Instant, "0100-02-29T00:00:00")] = ("0100-03-01 00:00:00", 0),
            [(Family.Minutes, "0001-01-01T00:00")] = ("0002-01-01 00:00:00", 0),
            [(Family.Minutes, "0000-01-01T00:00")] = ("0003-01-01 00:00:00", 0),
            [(Family.Minutes, "1582-10-15T00:00")] = ("1582-10-15 00:00:00", 10),
            [(Family.Minutes, "0100-03-01T00:00")] = (null, 0),
            [(Family.Minutes, "1500-03-01T00:00")] = (null, 0),
            [(Family.Minutes, "1500-02-29T00:00")] = ("1500-03-01 00:00:00", 0),
            [(Family.Date, "0001-01-01")] = ("0002-01-01 00:00:00", 0),
            [(Family.Date, "0000-01-01")] = ("0003-01-01 00:00:00", 0),
            [(Family.Date, "1582-10-15")] = ("1582-10-15 00:00:00", 10),
            [(Family.Date, "1582-10-05")] = ("1582-10-15 00:00:00", 10),
            [(Family.Date, "0100-03-01")] = (null, 0),
            [(Family.Date, "1500-03-01")] = (null, 0),
            [(Family.Date, "1500-02-29")] = ("1500-03-01 00:00:00", 0),
            [(Family.Date, "0100-02-29")] = ("0100-03-01 00:00:00", 0),
            [(Family.YearMonth, "0001-01")] = ("0002-01-01 00:00:00", 0),
            [(Family.YearMonth, "0000-01")] = ("0003-01-01 00:00:00", 0),
        };

        /// <summary>The conversions a family is read through, each the type a parse of its fields is claimed into.</summary>
        static Conversion[] ConversionsOf(Family family) => family switch
        {
            Family.Instant or Family.Minutes => [Conversion.ParseDatetime, Conversion.CastFormatTimestamp],
            Family.Date or Family.YearMonth => [Conversion.ParseDatetime, Conversion.ParseDate, Conversion.CastFormatTimestamp, Conversion.CastFormatDate],
            _ => [Conversion.ParseDatetime, Conversion.ParseTime, Conversion.CastFormatTimestamp, Conversion.CastFormatTime],
        };

        static string TypeOf(Conversion conversion) => conversion switch
        {
            Conversion.ParseDate or Conversion.CastFormatDate => "DATE",
            Conversion.ParseTime or Conversion.CastFormatTime => "TIME",
            _ => "TIMESTAMP",
        };

        /// <summary>The conversion of the document's text with a format, written as a query writes it.</summary>
        static string Expression(Conversion conversion, string format)
        {
            var quoted = "'" + format.Replace("'", "''") + "'";
            var digits = CosmosTemporalForms.FractionDigitsOf(format);

            return conversion switch
            {
                Conversion.ParseDatetime => $"PARSE_DATETIME({quoted}, {Text})",
                Conversion.ParseDate => $"PARSE_DATE({quoted}, {Text})",
                Conversion.ParseTime => $"PARSE_TIME({quoted}, {Text})",
                Conversion.CastFormatTimestamp => $"CAST({Text} AS TIMESTAMP({digits}) FORMAT {quoted})",
                Conversion.CastFormatDate => $"CAST({Text} AS DATE FORMAT {quoted})",
                _ => $"CAST({Text} AS TIME({digits}) FORMAT {quoted})",
            };
        }

        /// <summary>
        /// Calcite's literal of a type for an instant <c>yyyy-MM-dd HH:mm:ss</c> and a fraction, a number of days before it
        /// where no literal spells the reading.
        /// </summary>
        static string Literal(string type, string reads, string fraction, int daysBefore = 0)
        {
            var literal = type switch
            {
                "DATE" => $"DATE '{reads[..10]}'",
                "TIME" => $"TIME '{reads[11..]}.{fraction}'",
                _ => $"TIMESTAMP '{reads}.{fraction}'",
            };

            return daysBefore == 0 ? literal : $"({literal} - INTERVAL '{daysBefore}' DAY)";
        }

        static string Document(string stored) => "{\"s\":\"" + stored + "\"}";

        /// <summary>One string of a form, written from a case and a fraction.</summary>
        sealed record Row(Form Form, Case Case, string Fraction, string Stored);

        /// <summary>
        /// What one conversion with one format answered over one row: <c>T</c> where it read the expected instant,
        /// <c>F</c> or <c>U</c> where it read another, <c>R</c> where it raised; and what it read, written back.
        /// </summary>
        sealed record Answer(Row Row, Conversion Conversion, string Format, string Expected, char Letter, string Read);

        static IEnumerable<Row> RowsOf(Form form) =>
            Cases[form.Family].SelectMany(c => (form.Fraction ? Fractions : new[] { "000" }).Select(f => new Row(form, c, f, form.Write(c.Fields, f))));

        /// <summary>The literal a row is expected to read as through a conversion, or <c>null</c> where it is expected to raise.</summary>
        static string? Expected(Row row, Conversion conversion)
        {
            var type = TypeOf(conversion);

            var (reads, daysBefore) = conversion == Conversion.CastFormatTimestamp && TimestampFormatReadsOtherwise.TryGetValue((row.Form.Family, row.Case.Fields), out var otherwise)
                ? otherwise
                : (row.Case.Reads, row.Case.DaysBefore);

            return reads is null ? null : Literal(type, reads, row.Fraction, daysBefore);
        }

        /// <summary>
        /// Asks every listed format of every registered form through each conversion its fields are claimed into, over
        /// every case: one statement per form and conversion, each format a predicate and each row a document, and a row
        /// expected to raise asked alone.
        /// </summary>
        static readonly Lazy<IReadOnlyList<Answer>> Measured = new(Measure);

        static IReadOnlyList<Answer> Measure()
        {
            var answers = new List<Answer>();
            using var connection = MaterialisedTable.Open("all");

            foreach (var form in Forms)
            {
                var representation = CosmosStoredForms.Recognise(form.Pattern);
                string[] formats = representation is null ? [] : [.. CosmosStoredForms.ParseFormats(representation.Value)];
                var rows = RowsOf(form).ToArray();

                foreach (var conversion in ConversionsOf(form.Family))
                {
                    var reading = rows.Where(r => Expected(r, conversion) is not null).ToArray();
                    var raising = rows.Where(r => Expected(r, conversion) is null).ToArray();

                    answers.AddRange(Ask(connection, reading, conversion, formats));

                    foreach (var row in raising)
                        foreach (var format in formats)
                        {
                            var read = Written(connection, row, conversion, format);
                            answers.Add(new(row, conversion, format, "a raise", read.StartsWith("raises", StringComparison.Ordinal) ? 'R' : 'F', read));
                        }
                }
            }

            return answers;
        }

        /// <summary>
        /// Asks one conversion with each format over rows expected to read an instant, in one statement; where it fails,
        /// each row and format alone, so a raise is the row's own.
        /// </summary>
        static IEnumerable<Answer> Ask(DbConnection connection, Row[] rows, Conversion conversion, string[] formats)
        {
            if (rows.Length == 0)
                return [];

            string Predicate(string format, IEnumerable<Row> asked) =>
                $"({Expression(conversion, format)}) = CASE \"k\" {string.Join(" ", asked.Select((r, k) => $"WHEN {k} THEN {Expected(r, conversion)}"))} END";

            string[] letters;
            try
            {
                letters = MaterialisedTable.Of(rows.Select(r => Document(r.Stored))).Ask(connection, formats.Select(f => Predicate(f, rows)).ToArray());
            }
            catch (Exception)
            {
                letters = formats.Select(f => string.Concat(rows.Select(r =>
                {
                    var one = MaterialisedTable.Of([Document(r.Stored)]);
                    return one.Failure(connection, Predicate(f, [r])) is null ? one.Ask(connection, Predicate(f, [r]))[0][0] : 'R';
                }))).ToArray();
            }

            return formats.SelectMany((format, p) => rows.Select((row, k) => new Answer(row, conversion, format, Expected(row, conversion)!, letters[p][k],
                letters[p][k] == 'T' ? string.Empty : Written(connection, row, conversion, format))));
        }

        /// <summary>What a conversion read a row as, written back as text, or how it raised; for a message.</summary>
        static string Written(DbConnection connection, Row row, Conversion conversion, string format) =>
            MaterialisedTable.Of([Document(row.Stored)]).Values(connection, $"CAST({Expression(conversion, format)} AS VARCHAR)")[0] switch
            {
                null => "null",
                Exception e => "raises: " + e.Message,
                var v => "reads " + v,
            };

        static IEnumerable<string> Disagreements(IEnumerable<Answer> answers) =>
            answers
                .Where(a => a.Letter != (a.Expected == "a raise" ? 'R' : 'T'))
                .Select(a => $"{a.Row.Form.Name} {a.Conversion} '{a.Format}' over '{a.Row.Stored}': expected {a.Expected}, {a.Read}");

        /// <summary>
        /// The forms measured are every registered temporal form the table lists a format for, each recognised from its
        /// pattern, and every string written for one is a string of it.
        /// </summary>
        [Fact]
        public void TheFormsAreEveryFormAFormatIsListedFor()
        {
            foreach (var form in Forms)
            {
                var representation = CosmosStoredForms.Recognise(form.Pattern);
                representation.Should().NotBeNull("the form has to be recognised before anything is measured of it: " + form.Pattern);
                representation!.Value.Name.Should().Be(form.Name, "for " + form.Pattern);

                foreach (var row in RowsOf(form))
                    Regex.IsMatch(row.Stored, form.Pattern).Should().BeTrue($"'{row.Stored}' is written as a string of {form.Name}");
            }

            CosmosTemporalForms.Registered.Where(r => CosmosStoredForms.ParseFormats(r).Count > 0).Select(r => r.Name)
                .Should().BeEquivalentTo(Forms.Select(f => f.Name), "the measurement asks every form a parse is listed for");

            Forms.Sum(f => CosmosStoredForms.ParseFormats(CosmosStoredForms.Recognise(f.Pattern)!.Value).Count * ConversionsOf(f.Family).Length)
                .Should().Be(112, "42 listed formats, each through the conversions its form's fields are claimed into");
        }

        /// <summary>
        /// Every listed format reads the canonical strings of its form as Calcite's literal of the same spelling, through
        /// the parse family; <c>CAST … AS TIMESTAMP FORMAT</c> parts from the literal before 1582-10-16, where
        /// <see cref="TimestampFormatReadsOtherwise"/> says.
        /// </summary>
        [Fact, CosmosPins("Calcite.ParseSpellingsReadTheirShapes")]
        public void EveryListedFormatReadsTheCanonicalStringsOfItsForm()
        {
            var canonical = Measured.Value.Where(a => a.Row.Case.Canonical).ToArray();

            canonical.Should().NotBeEmpty();
            string.Join(Environment.NewLine, Disagreements(canonical)).Should().BeEmpty("a canonical string reads as the literal of its own spelling");
        }

        /// <summary>
        /// Every listed format reads a string that rolls a field past its range as the value its fields make, raising
        /// over none of them, so no format reads its form one for one; before 1582-10-15, <c>0000-01-01</c> is read as
        /// <c>0001-01-01</c> and <c>0001-01-00</c> as <c>0001-12-31</c>, and 29 February of 0100 and of 1500 raises.
        /// </summary>
        [Fact, CosmosPins("Calcite.ParsesReadTheirFieldsLeniently")]
        public void EveryListedFormatRollsAFieldPastItsRange()
        {
            var rolled = Measured.Value.Where(a => a.Row.Case.Canonical == false).ToArray();

            rolled.Should().NotBeEmpty();
            string.Join(Environment.NewLine, Disagreements(rolled)).Should().BeEmpty("a field past its range rolls into the next");
        }

        /// <summary>
        /// Asks a plain cast of the document's text to a type over strings, each expected to read as a literal or, where
        /// it holds none, to raise; and answers each string that did otherwise.
        /// </summary>
        static IEnumerable<string> Plain(DbConnection connection, string type, IEnumerable<(string Stored, string? Literal)> cases)
        {
            var all = cases.ToArray();
            var reading = all.Where(c => c.Literal is not null).ToArray();
            var expression = $"CAST({Text} AS {type})";
            var problems = new List<string>();

            string Read(string stored) =>
                MaterialisedTable.Of([Document(stored)]).Values(connection, $"CAST({expression} AS VARCHAR)")[0] switch
                {
                    null => "null",
                    Exception e => "raises: " + e.Message,
                    var v => "reads " + v,
                };

            if (reading.Length > 0)
            {
                var predicate = $"{expression} = CASE \"k\" {string.Join(" ", reading.Select((c, k) => $"WHEN {k} THEN {c.Literal}"))} END";

                string letters;
                try
                {
                    letters = MaterialisedTable.Of(reading.Select(c => Document(c.Stored))).Ask(connection, predicate)[0];
                }
                catch (Exception)
                {
                    letters = string.Concat(reading.Select(c =>
                    {
                        var one = MaterialisedTable.Of([Document(c.Stored)]);
                        return one.Failure(connection, $"{expression} = {c.Literal}") is null ? one.Ask(connection, $"{expression} = {c.Literal}")[0][0] : 'R';
                    }));
                }

                problems.AddRange(reading.Where((c, k) => letters[k] != 'T').Select(c => $"CAST('{c.Stored}' AS {type}): expected {c.Literal}, {Read(c.Stored)}"));
            }

            foreach (var (stored, _) in all.Where(c => c.Literal is null))
                if (Read(stored) is var read && read.StartsWith("raises", StringComparison.Ordinal) == false)
                    problems.Add($"CAST('{stored}' AS {type}): expected a raise, {read}");

            return problems;
        }

        /// <summary>
        /// The plain casts to <c>DATE</c> and <c>TIMESTAMP</c> read day <c>00</c> of a calendar date as the last day of the
        /// month before, <c>0001-01-00</c> as the day before year one, and every other string whose fields are in range in
        /// the proleptic Gregorian calendar as that day, the ten days of 1582's gap among them; every other field out of
        /// range raises, year <c>0000</c> and 29 February of a year only the Julian calendar holds leap among them.
        /// </summary>
        [Fact, CosmosPins("Calcite.DateCastReadsDayZeroAsTheMonthBefore")]
        public void TheDateCastReadsDayZeroAsTheMonthBeforeAndRaisesPastEveryOtherField()
        {
            CosmosStoredForms.Recognise("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")!.Value.Name.Should().Be("iso8601-date");

            using var connection = MaterialisedTable.Open();

            var reads = new (string Stored, string Reads, int DaysBefore)[]
            {
                ("2024-02-29", "2024-02-29", 0), ("2024-03-01", "2024-03-01", 0), ("1970-01-01", "1970-01-01", 0), ("0001-01-01", "0001-01-01", 0),
                ("9999-12-31", "9999-12-31", 0), ("1582-10-15", "1582-10-15", 0), ("1582-10-04", "1582-10-04", 0), ("1500-01-01", "1500-01-01", 0),
                ("2024-03-00", "2024-02-29", 0), ("2023-03-00", "2023-02-28", 0), ("2024-01-00", "2023-12-31", 0), ("2023-05-00", "2023-04-30", 0),
                ("0001-01-00", "0001-01-01", 1),
                ("1582-10-14", "1582-10-15", 1), ("1582-10-05", "1582-10-15", 10),
            };
            var raises = new[] { "2023-02-29", "2023-02-30", "2024-02-30", "2024-04-31", "2024-01-32", "2023-13-01", "2023-00-15", "2024-00-00", "0000-01-01", "0000-06-15", "0000-12-31", "1500-02-29", "0100-02-29", "1900-02-29" };

            var problems = new List<string>();
            foreach (var type in new[] { "DATE", "TIMESTAMP" })
                problems.AddRange(Plain(connection, type, reads.Select(r => (r.Stored, (string?)Literal(type, r.Reads + " 00:00:00", "000", r.DaysBefore)))
                    .Concat(raises.Select(s => (s, (string?)null)))));

            string.Join(Environment.NewLine, problems).Should().BeEmpty("day 00 is the month before's last day, the gap's days are the days they spell, and every other field out of range raises");

            var dayZero = MaterialisedTable.Of([Document("0001-01-00")]);
            dayZero.Values(connection, $"CAST(CAST({Text} AS DATE) AS VARCHAR)")[0].Should().Be("0000-12-31", "which no string of the form reads as, year 0000 raising");
            dayZero.Ask(connection, $"CAST({Text} AS DATE) < DATE '0001-01-01'", $"CAST({Text} AS TIMESTAMP) < TIMESTAMP '0001-01-01 00:00:00'")
                .Should().Equal(["T", "T"], "it is before year one");

            foreach (var literal in new[] { "DATE '1582-10-05'", "DATE '1582-10-14'", "TIMESTAMP '1582-10-05 00:00:00'" })
                dayZero.Failure(connection, $"{literal} IS NOT NULL").Should().NotBeNull($"Calcite's literal refuses a day of 1582's gap, {literal}, which the cast reads");
            foreach (var literal in new[] { "DATE '1582-10-04'", "DATE '1582-10-15'" })
                dayZero.Failure(connection, $"{literal} IS NOT NULL").Should().BeNull($"and takes the days either side of it, {literal}");
        }

        /// <summary>
        /// The plain cast to <c>TIME</c> reads the two clock forms' canonical strings as the time they spell and adds the
        /// fields of the rest as they stand: a minute or second of 60 is the next, and an hour past 23 a time past every time
        /// of day, written back with its hours modulo 100; none raises.
        /// </summary>
        [Fact, CosmosPins("Calcite.TimeCastRollsAClocksFields")]
        public void TheTimeCastAddsAClocksFieldsAsTheyStand()
        {
            CosmosStoredForms.Recognise("^[0-9]{2}:[0-9]{2}:[0-9]{2}$")!.Value.Name.Should().Be("iso8601-time-f0");
            CosmosStoredForms.Recognise("^[0-9]{2}:[0-9]{2}$")!.Value.Name.Should().Be("iso8601-time-minutes");

            using var connection = MaterialisedTable.Open();

            var reads = new (string Stored, string Reads)[]
            {
                ("00:00:00", "00:00:00"), ("23:59:59", "23:59:59"), ("12:30:00", "12:30:00"), ("03:04:05", "03:04:05"),
                ("00:00", "00:00:00"), ("23:59", "23:59:00"), ("12:30", "12:30:00"),
                ("12:60:00", "13:00:00"), ("12:30:60", "12:31:00"), ("00:59:60", "01:00:00"), ("12:60", "13:00:00"), ("00:99", "01:39:00"),
            };

            Plain(connection, "TIME", reads.Select(r => (r.Stored, (string?)$"TIME '{r.Reads}'")))
                .Should().BeEmpty("a minute or a second of 60 is the next");

            var past = new (string Stored, string Written)[] { ("24:00:00", "24:00:00"), ("23:60:00", "24:00:00"), ("99:99:99", "00:40:39"), ("24:00", "24:00:00"), ("99:99", "00:39:00") };
            var table = MaterialisedTable.Of(past.Select(p => Document(p.Stored)));

            table.Values(connection, $"CAST(CAST({Text} AS TIME) AS VARCHAR)").Should().Equal(past.Select(p => (object)p.Written),
                "an hour past 23 is kept, and written back two digits of it");
            table.Ask(connection, $"CAST({Text} AS TIME) > TIME '23:59:59.999'", $"CAST({Text} AS TIME) = TIME '00:00:00'")
                .Should().Equal([new string('T', past.Length), new string('F', past.Length)], "so it is later than every time of day, and is not midnight");
        }

        /// <summary>
        /// A plain cast to a temporal type reads the canonical strings of three forms as Calcite's literal of the same
        /// spelling, a calendar date into <c>DATE</c> and <c>TIMESTAMP</c> and a time of day at seconds or minutes into
        /// <c>TIME</c>, and raises into each of the three over a sample of every registered form holding a <c>T</c>.
        /// </summary>
        [Fact, CosmosPins("Calcite.TemporalCastReadsFewShapes")]
        public void APlainCastReadsThreeFormsAndRaisesOverEveryInstant()
        {
            using var connection = MaterialisedTable.Open();
            var problems = new List<string>();

            var dates = new[] { "2024-02-29", "2024-01-15", "1970-01-01", "1969-12-31", "0001-01-01", "9999-12-31", "1582-10-15", "1582-10-04", "1500-01-01" };
            problems.AddRange(Plain(connection, "DATE", dates.Select(d => (d, (string?)$"DATE '{d}'"))));
            problems.AddRange(Plain(connection, "TIMESTAMP", dates.Select(d => (d, (string?)$"TIMESTAMP '{d} 00:00:00'"))));
            problems.AddRange(Plain(connection, "TIME", dates.Select(d => (d, (string?)null))));

            var clocks = new[] { "00:00:00", "23:59:59", "12:30:00", "00:00", "23:59", "12:30" };
            problems.AddRange(Plain(connection, "TIME", clocks.Select(c => (c, (string?)$"TIME '{(c.Length == 5 ? c + ":00" : c)}'"))));
            problems.AddRange(Plain(connection, "DATE", clocks.Select(c => (c, (string?)null))));
            problems.AddRange(Plain(connection, "TIMESTAMP", clocks.Select(c => (c, (string?)null))));

            var instants = CosmosTemporalForms.Registered
                .Select(r => CosmosStoredForms.RenderDateTime(r, new DateTime(2024, 1, 15, 12, 30, 0, DateTimeKind.Utc)))
                .OfType<string>()
                .Where(s => s.Contains('T'))
                .ToArray();

            instants.Should().HaveCount(63, "thirty extended and thirty basic forms at ten fraction widths and three zones, and three at the minute");

            foreach (var type in new[] { "TIMESTAMP", "DATE", "TIME" })
                problems.AddRange(Plain(connection, type, instants.Select(s => (s, (string?)null))));

            string.Join(Environment.NewLine, problems).Should().BeEmpty("a calendar date and a time of day are read, and an instant raises");
        }

        /// <summary>
        /// <c>JSON_VALUE(… RETURNING DATE)</c>, <c>RETURNING TIME</c> and <c>RETURNING TIMESTAMP</c> raise over a string of
        /// every registered temporal form, the strings the plain cast reads among them and those that roll a field, and
        /// read a JSON number: of days since the epoch, of milliseconds into the day, of milliseconds since the epoch. Over
        /// an absent path each is null.
        /// </summary>
        /// <remarks>
        /// The clause asserts the extracted type rather than converting to it (<c>Calcite.ReturningAsserts</c>), so no
        /// stored string is a value of it, and an accessor so typed over a declared form has no column the service could
        /// send in its place. The numbers and the absent path are the controls: the raise is the string's, not the
        /// clause's, and it is told apart from a null.
        /// </remarks>
        [Fact, CosmosPins("Calcite.ReturningReadsNoStoredString")]
        public void AReturningClauseRaisesOverEveryStoredString()
        {
            using var connection = MaterialisedTable.Open();

            var samples = CosmosTemporalForms.Registered
                .Select(r => (r.Name, Sample: CosmosStoredForms.RenderDateTime(r, new DateTime(2024, 1, 15, 12, 30, 0, DateTimeKind.Utc))
                    ?? Forms.Where(f => f.Name == r.Name).Select(f => f.Write(Cases[f.Family][0].Fields, "000")).FirstOrDefault()
                    ?? Enumerable.Range(1, 9)
                        .Where(n => CosmosStoredForms.Recognise($@"^[0-9]{{2}}:[0-9]{{2}}:[0-9]{{2}}\.[0-9]{{{n}}}$")?.Name == r.Name)
                        .Select(n => "12:30:00." + new string('5', n))
                        .FirstOrDefault()))
                .ToArray();

            samples.Where(s => s.Sample is null).Select(s => s.Name).Should().BeEmpty("every registered temporal form is asked");

            var strings = samples
                .Select(s => s.Sample!)
                .Concat(["2024-03-00", "2016-12-31T24:00:00Z", "24:00:00", "12:60:00"])
                .Distinct()
                .ToArray();

            strings.Should().Contain(["2024-02-29", "00:00:00", "00:00", "2024-01-15T12:30:00Z"], "the forms the plain cast reads, and an instant, are among them");

            var table = MaterialisedTable.Of(strings.Select(Document));
            var problems = new List<string>();
            foreach (var type in new[] { "DATE", "TIME", "TIMESTAMP" })
                problems.AddRange(table.Values(connection, $"CAST(JSON_VALUE(\"d\", '$.s' RETURNING {type}) AS VARCHAR)")
                    .Select((v, k) => (Stored: strings[k], Read: v))
                    .Where(r => r.Read is not Exception)
                    .Select(r => $"RETURNING {type} over '{r.Stored}': expected a raise, read {r.Read ?? "null"}"));

            string.Join(Environment.NewLine, problems).Should().BeEmpty("RETURNING asserts its type, and a string is none of the three");

            var numbers = MaterialisedTable.Of(["{\"s\":19737}", "{\"s\":45000000}", "{\"s\":1705321800000}"]);
            numbers.Values(connection, "CAST(JSON_VALUE(\"d\", '$.s' RETURNING DATE) AS VARCHAR)")[0].Should().Be("2024-01-15", "a number of days is a date");
            numbers.Values(connection, "CAST(JSON_VALUE(\"d\", '$.s' RETURNING TIME) AS VARCHAR)")[1].Should().Be("12:30:00", "a number of milliseconds into the day a time");
            numbers.Values(connection, "CAST(JSON_VALUE(\"d\", '$.s' RETURNING TIMESTAMP) AS VARCHAR)")[2].Should().Be("2024-01-15 12:30:00", "and of milliseconds since the epoch an instant");

            var absent = MaterialisedTable.Of(["{}"]);
            foreach (var type in new[] { "DATE", "TIME", "TIMESTAMP" })
                absent.Values(connection, $"JSON_VALUE(\"d\", '$.s' RETURNING {type})")[0].Should().BeNull($"RETURNING {type} over an absent path is null, not a raise");
        }

    }

}
