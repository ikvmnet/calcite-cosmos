using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// Which stored shapes Calcite's own conversions read, measured and asserted against the table
    /// that decides what may be pushed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the condition the temporal rewrites were missing.</b> A declared form says the
    /// stored strings compare the way the values do. It says nothing about whether the engine can
    /// produce the values at all — and for an ISO-8601 instant it cannot, so a rewrite that dropped
    /// the conversion answered rows for a query that raises. That is not a faster answer, it is a
    /// different query.
    /// </para>
    /// <para>
    /// <b>Two shapes are worse than a refusal, and this is where that is on the record.</b>
    /// <c>CAST('20240115' AS TIME)</c> answers a value, and the value is wrong;
    /// <c>CAST('12:30:00.123' AS TIME)</c> drops the fraction. Neither raises, so neither would have
    /// been caught by asking only whether the conversion succeeds.
    /// </para>
    /// <para>
    /// Measured through Calcite's own JDBC driver, over a column rather than a folded literal —
    /// constant reduction and the runtime disagree about the fraction of a space-separated instant,
    /// which is a good reason not to measure the folded path.
    /// </para>
    /// </remarks>
    public class CalciteTemporalReadingMeasurementTests
    {

        /// <param name="Pattern">The declared pattern, so the row is tied to a recognised form.</param>
        /// <param name="Sample">A conforming stored value.</param>
        /// <param name="Reads">What the engine answers per target, or <c>null</c> where it raises.</param>
        sealed record Shape(string Pattern, string Sample, (CosmosTemporalParts Target, string? Reads)[] Reads);

        static readonly Shape[] Shapes =
        {
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", "2024-01-15T12:30:00Z", new[]
            {
                (CosmosTemporalParts.Instant, (string?)null),
                (CosmosTemporalParts.Date, null),
                (CosmosTemporalParts.Time, null),
            }),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$", "2024-01-15T12:30:00.123Z", new[]
            {
                (CosmosTemporalParts.Instant, (string?)null),
                (CosmosTemporalParts.Date, null),
                (CosmosTemporalParts.Time, null),
            }),
            new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$", "2024-01-15T12:30:00.1234567Z", new[]
            {
                (CosmosTemporalParts.Instant, (string?)null),
                (CosmosTemporalParts.Date, null),
                (CosmosTemporalParts.Time, null),
            }),
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$", "2024-01-15T12:30:00", new[]
            {
                (CosmosTemporalParts.Instant, (string?)null),
                (CosmosTemporalParts.Date, null),
                (CosmosTemporalParts.Time, null),
            }),
            // The three the engine does read, which is the whole of what may be pushed through a cast.
            new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", "2024-01-15", new[]
            {
                (CosmosTemporalParts.Instant, (string?)"2024-01-15 00:00:00.0"),
                (CosmosTemporalParts.Date, "2024-01-15"),
                (CosmosTemporalParts.Time, null),
            }),
            new("^[0-9]{2}:[0-9]{2}:[0-9]{2}$", "12:30:00", new[]
            {
                (CosmosTemporalParts.Instant, (string?)null),
                (CosmosTemporalParts.Date, null),
                (CosmosTemporalParts.Time, "12:30:00"),
            }),
            new("^[0-9]{2}:[0-9]{2}$", "12:30", new[]
            {
                (CosmosTemporalParts.Instant, (string?)null),
                (CosmosTemporalParts.Date, null),
                (CosmosTemporalParts.Time, "12:30:00"),
            }),
        };

        static string TypeOf(CosmosTemporalParts target) => target switch
        {
            CosmosTemporalParts.Date => "DATE",
            CosmosTemporalParts.Time => "TIME",
            _ => "TIMESTAMP",
        };

        /// <summary>
        /// Casts a stored string through Calcite's own driver, over a column so nothing is folded.
        /// </summary>
        /// <returns>The value rendered, or <c>null</c> where the engine raised.</returns>
        static string? Cast(string sample, string type)
        {
            java.lang.Class.forName("org.apache.calcite.jdbc.Driver");
            var connection = java.sql.DriverManager.getConnection("jdbc:calcite:", new java.util.Properties());

            try
            {
                var results = connection.createStatement().executeQuery(
                    $"SELECT CAST(s AS {type}) AS v FROM (VALUES ('{sample}')) AS t(s)");

                return results.next() ? results.getObject(1)?.ToString() : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                connection.close();
            }
        }

        static CosmosRepresentation Form(string pattern)
        {
            var representation = CosmosStoredForms.Recognise(pattern);
            representation.Should().NotBeNull("the shape has to be recognised: " + pattern);
            return representation!.Value;
        }

        /// <summary>
        /// The table says exactly what the engine does, in both directions.
        /// </summary>
        [Fact]
        public void TheTableSaysWhatTheEngineDoes()
        {
            foreach (var shape in Shapes)
            {
                var representation = Form(shape.Pattern);

                foreach (var (target, reads) in shape.Reads)
                {
                    var measured = Cast(shape.Sample, TypeOf(target));

                    measured.Should().Be(reads,
                        $"'{shape.Sample}' AS {TypeOf(target)} is what it is, whatever the table says");

                    CosmosStoredForms.EngineReads(representation, target).Should().Be(reads is not null,
                        $"the table has to agree with the engine for {representation.Name} AS {TypeOf(target)}");
                }
            }
        }

        /// <summary>
        /// Two shapes answer a value and the value is wrong, which is why succeeding is not the test.
        /// </summary>
        /// <remarks>
        /// A conversion that loses or invents is no more usable than one that raises, and it is less
        /// visible. Both of these are absent from the table for that reason rather than for raising.
        /// </remarks>
        [Fact]
        public void AConversionThatSucceedsIsNotThereforeUsable()
        {
            Cast("20240115", "TIME").Should().NotBeNull("the basic date does convert to a TIME");
            Cast("20240115", "TIME").Should().NotBe("00:00:00", "and the value it converts to is not the shape's");

            Cast("12:30:00.123", "TIME").Should().Be("12:30:00", "a fractional time loses its fraction");

            CosmosStoredForms.EngineReads(Form("^[0-9]{8}$"), CosmosTemporalParts.Time).Should().BeFalse();
            CosmosStoredForms.EngineReads(Form(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}$"), CosmosTemporalParts.Time).Should().BeFalse();
        }

        /// <summary>
        /// <c>RETURNING TIMESTAMP</c> is not a parse: it wants a JSON number of epoch milliseconds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The measurement that withdrew a spelling. It raises for every string, including the
        /// space-separated one the cast accepts, and answers correctly for a number — so the clause
        /// asserts the extracted type rather than converting to it, which is the reading this
        /// codebase already arrived at for <c>RETURNING INTEGER</c>.
        /// </para>
        /// <para>
        /// The <c>RETURNING INTEGER</c> row is the control: without it, a failure here would be as
        /// consistent with the clause being broken in general as with it meaning something else.
        /// </para>
        /// </remarks>
        [Fact]
        public void AReturningClauseAssertsTheTypeRatherThanConvertingToIt()
        {
            const string Document = """{"iso":"2024-01-15T12:30:00Z","plain":"2024-01-15 12:30:00","millis":1705321800000,"n":7}""";

            Returning(Document, "$.iso", "TIMESTAMP").Should().BeNull("an ISO string is not a number");
            Returning(Document, "$.plain", "TIMESTAMP").Should().BeNull("and neither is the shape the cast reads");
            Returning(Document, "$.iso", "DATE").Should().BeNull();
            Returning(Document, "$.millis", "TIMESTAMP").Should().Be("2024-01-15 12:30:00.0", "a number is what it wants");
            Returning(Document, "$.n", "INTEGER").Should().Be("7", "and the clause itself works, which is the control");
        }

        static string? Returning(string document, string path, string type)
        {
            java.lang.Class.forName("org.apache.calcite.jdbc.Driver");
            var connection = java.sql.DriverManager.getConnection("jdbc:calcite:", new java.util.Properties());

            try
            {
                var results = connection.createStatement().executeQuery(
                    $"SELECT JSON_VALUE(d, '{path}' RETURNING {type}) AS v FROM (VALUES ('{document.Replace("'", "''")}')) AS t(d)");

                return results.next() ? results.getObject(1)?.ToString() : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                connection.close();
            }
        }

    }

}
