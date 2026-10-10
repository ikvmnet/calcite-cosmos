using System;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// How an instant is written into a temporal form when it arrives with the execution rather than
    /// with the plan, and so cannot be refused for not landing on the form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal that does not land on a form keeps its comparison in process. A parameter cannot — its
    /// statement is written before its value exists — so a value between two stored spellings is
    /// rounded onto the form, the way the comparison it is bound into does not see. These pin the
    /// grid each kind of form rounds onto, which is where an off-by-one-step would hide.
    /// </para>
    /// </remarks>
    public class CosmosTemporalFormsTests
    {

        static CosmosRepresentation Form(string pattern) =>
            CosmosStoredForms.Recognise(pattern) ?? throw new InvalidOperationException($"'{pattern}' is not a recognised form.");

        static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
            new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc);

        const string Seconds = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$";

        const string Milliseconds = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$";

        const string Ticks = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$";

        const string Minutes = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}Z$";

        const string Date = "^[0-9]{4}-[0-9]{2}-[0-9]{2}$";

        const string YearMonth = "^[0-9]{4}-[0-9]{2}$";

        /// <summary>
        /// A form at millisecond precision or finer holds every value Calcite's runtime can hand over,
        /// so the rounding is never consulted there.
        /// </summary>
        [Theory]
        [InlineData(Milliseconds, "2026-08-02T12:00:00.123Z")]
        [InlineData(Ticks, "2026-08-02T12:00:00.1230000Z")]
        public void AFormFineEnoughWritesEveryValueExactly(string pattern, string written)
        {
            foreach (var rounding in new[] { CosmosTemporalRounding.None, CosmosTemporalRounding.Down, CosmosTemporalRounding.Up })
                CosmosStoredForms.RenderDateTime(Form(pattern), Utc(2026, 8, 2, 12, 0, 0, 123), rounding).Should().Be(written);
        }

        /// <summary>
        /// Between two spellings the value goes down to the one before it or up to the one after it, by
        /// a whole step of the form, and a value already on one goes nowhere.
        /// </summary>
        [Theory]
        [InlineData(Seconds, "2026-08-02T12:00:00Z", "2026-08-02T12:00:01Z")]
        [InlineData(Minutes, "2026-08-02T12:00Z", "2026-08-02T12:01Z")]
        [InlineData(Date, "2026-08-02", "2026-08-03")]
        [InlineData(YearMonth, "2026-08", "2026-09")]
        public void BetweenTwoSpellingsTheValueGoesToOneOfThem(string pattern, string down, string up)
        {
            var form = Form(pattern);
            var between = Utc(2026, 8, 2, 12, 0, 0, 500);

            CosmosStoredForms.RenderDateTime(form, between, CosmosTemporalRounding.Down).Should().Be(down);
            CosmosStoredForms.RenderDateTime(form, between, CosmosTemporalRounding.Up).Should().Be(up);

            // The literal's rendering refuses the same value, which is the difference the rounding is for.
            CosmosStoredForms.RenderDateTime(form, between).Should().BeNull();
        }

        /// <summary>
        /// A value on a spelling is written as it is, whichever way it would have been rounded.
        /// </summary>
        [Fact]
        public void AValueOnASpellingIsNotRounded()
        {
            CosmosStoredForms.RenderDateTime(Form(Date), Utc(2026, 8, 2), CosmosTemporalRounding.Up).Should().Be("2026-08-02");
            CosmosStoredForms.RenderDateTime(Form(YearMonth), Utc(2026, 8, 1), CosmosTemporalRounding.Up).Should().Be("2026-08");
        }

        /// <summary>
        /// A month is not a fixed number of ticks, and the step past its last day is the next month —
        /// the next year, at the end of one.
        /// </summary>
        [Fact]
        public void AMonthStepsToTheNextMonth()
        {
            CosmosStoredForms.RenderDateTime(Form(YearMonth), Utc(2024, 1, 31, 10), CosmosTemporalRounding.Up).Should().Be("2024-02");
            CosmosStoredForms.RenderDateTime(Form(YearMonth), Utc(2024, 12, 15), CosmosTemporalRounding.Up).Should().Be("2025-01");
        }

        /// <summary>
        /// For an equality and an inequality the value between two spellings is written out in full,
        /// which no spelling of the form equals.
        /// </summary>
        [Theory]
        [InlineData(Seconds)]
        [InlineData(Minutes)]
        [InlineData(Date)]
        [InlineData(YearMonth)]
        public void UnroundedTheValueEqualsNoSpelling(string pattern)
        {
            var written = CosmosStoredForms.RenderDateTime(Form(pattern), Utc(2026, 8, 2, 12, 0, 0, 500), CosmosTemporalRounding.None);

            written.Should().Be("2026-08-02T12:00:00.5000000Z");
            Regex.IsMatch(written!, pattern).Should().BeFalse("it is outside the form's language, so nothing stored equals it");
        }

        /// <summary>
        /// A time of day is no form an instant is written into, rounded or not: it would drop the date.
        /// </summary>
        [Fact]
        public void ATimeOfDayIsWrittenIntoNeither()
        {
            CosmosStoredForms.RenderDateTime(Form("^[0-9]{2}:[0-9]{2}:[0-9]{2}$"), Utc(2026, 8, 2, 12), CosmosTemporalRounding.Down).Should().BeNull();
        }

    }

}
