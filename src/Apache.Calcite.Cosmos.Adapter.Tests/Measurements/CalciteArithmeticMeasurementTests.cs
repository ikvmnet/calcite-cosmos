using System;

using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
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

        /// <summary>The documents' <c>a</c> and <c>b</c> read as a type, so nothing is folded at planning.</summary>
        static string Read(string member, string type) => $"CAST(JSON_VALUE(\"d\", '$.{member}') AS {type})";

        /// <summary>
        /// <c>+</c>, <c>-</c> and <c>*</c> over two <c>INTEGER</c>s or two <c>BIGINT</c>s wrap at the type's bounds, as Java's
        /// do: 2147483647 + 1 is -2147483648, and 9223372036854775807 + 1 is -9223372036854775808; within the bounds they are
        /// exact, past 2^53 too.
        /// </summary>
        [Fact, CosmosPins("Calcite.IntegerArithmeticWraps")]
        public void IntegerArithmeticWraps()
        {
            using var connection = MaterialisedTable.Open();
            var ints = MaterialisedTable.Of(["{\"a\":2147483647,\"b\":1}", "{\"a\":-2147483648,\"b\":1}", "{\"a\":65536,\"b\":65536}"]);
            ints.Ask(connection, $"{Read("a", "INTEGER")} + {Read("b", "INTEGER")} = -2147483648", $"{Read("a", "INTEGER")} - {Read("b", "INTEGER")} = 2147483647",
                $"{Read("a", "INTEGER")} * {Read("b", "INTEGER")} = 0").Should().Equal("TFF", "FTF", "FFT");

            var longs = MaterialisedTable.Of(["{\"a\":9223372036854775807,\"b\":1}", "{\"a\":9007199254740992,\"b\":1}"]);
            longs.Ask(connection, $"{Read("a", "BIGINT")} + {Read("b", "BIGINT")} = -9223372036854775808", $"{Read("a", "BIGINT")} + {Read("b", "BIGINT")} = 9007199254740993").Should().Equal("TF", "FT");
        }

        /// <summary>
        /// <c>+</c>, <c>-</c> and <c>*</c> over <c>DECIMAL</c> are exact: 0.1 + 0.2 is 0.3, and 9007199254740993 + 0 is itself,
        /// where a double has neither.
        /// </summary>
        [Fact, CosmosPins("Calcite.DecimalArithmeticIsExact")]
        public void DecimalArithmeticIsExact()
        {
            using var connection = MaterialisedTable.Open();
            string Decimal(string member) => $"CAST(JSON_VALUE(\"d\", '$.{member}') AS DECIMAL(19, 2))";
            var values = MaterialisedTable.Of(["{\"a\":\"0.1\",\"b\":\"0.2\"}", "{\"a\":\"9007199254740993\",\"b\":\"0\"}"]);
            values.Ask(connection, $"{Decimal("a")} + {Decimal("b")} = 0.3", $"{Decimal("a")} + {Decimal("b")} = 9007199254740993", $"{Decimal("a")} * 3 - {Decimal("b")} * 3 = -0.3").Should().Equal("TF", "FT", "TF");
            values.Ask(connection, $"CAST({Decimal("a")} AS DOUBLE) + CAST({Decimal("b")} AS DOUBLE) = 0.3").Should().Equal("FF");
        }

    }

}
