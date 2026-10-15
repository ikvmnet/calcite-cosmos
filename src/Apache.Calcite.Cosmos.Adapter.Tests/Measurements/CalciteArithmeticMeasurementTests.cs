using System;

using Apache.Calcite.Data;

using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's own <c>/</c> answers over integers, measured end to end so that the difference
    /// from the service's is on the record rather than rediscovered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The service divides in floating point whatever its operands</b>
    /// (<c>CosmosJsonTypeMeasurementTests.DivisionIsFloatingPointAndUndefinedOverAnythingButANumber</c>),
    /// and Calcite divides two integers as integers, truncating toward zero. So <c>/</c> over two
    /// integer operands is not the same function on both sides: pushed, <c>price / 2 = 2</c> keeps no
    /// stored <c>5</c>, where Calcite keeps it, and a projected <c>price / 3</c> comes back as a
    /// fraction into a column the plan declared an integer. The adapter knew this for <c>AVG</c>
    /// (<c>CosmosAggregate</c>) and not for <c>/</c>.
    /// </para>
    /// <para>
    /// No service and no adapter: a <c>CalciteConnection</c> over literals, as
    /// <see cref="CalciteJsonValueMeasurementTests"/> asks.
    /// </para>
    /// </remarks>
    public class CalciteArithmeticMeasurementTests
    {

        static string? Ask(string expression)
        {
            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT " + expression;

            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();

            return reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Two integers divide as integers, truncating toward zero; a fraction anywhere makes it a
        /// fraction.
        /// </summary>
        [Fact]
        public void IntegerDivisionTruncatesTowardZero()
        {
            Ask("CAST(7 AS INTEGER) / 2").Should().Be("3");
            Ask("CAST(-7 AS INTEGER) / 2").Should().Be("-3", "toward zero, not toward negative infinity");
            Ask("JSON_VALUE('{\"v\":5}', '$.v' RETURNING INTEGER) / 2").Should().Be("2", "and so does an accessor that returns one");
            Ask("CAST(7 AS DOUBLE) / 2").Should().Be("3.5", "while a double divides as one");
        }

    }

}
