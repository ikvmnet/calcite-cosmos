using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Client;

using FluentAssertions;

using org.apache.calcite.sql.type;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What a column typed <c>BIGINT</c>, <c>INTEGER</c>, <c>DOUBLE</c> or <c>BOOLEAN</c> holds, from the JSON the
    /// service returns to the comparison Calcite makes of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The column reader and <c>RETURNING</c> are one IR, and two readers.</b> <c>CosmosPredicateLoader</c> reads a
    /// field typed <c>BIGINT</c> and <c>JSON_VALUE … RETURNING BIGINT</c> alike, as <c>typed</c>. But the field is filled by
    /// <c>CosmosJson.GetValue</c>, which reads a whole number as the type whatever its spelling, where <c>RETURNING</c>
    /// asserts the class Jackson read: it raises over a stored <c>42.0</c> under <c>INTEGER</c> and over an int-sized
    /// number under <c>BIGINT</c>, both of which the column reads. So a cell's answer for <c>typed</c> is the union of
    /// the two, and this pins the column's half.
    /// </para>
    /// <para>
    /// No service: an in-process table whose column holds what the reader made of each value, and a read the reader
    /// refuses is a statement that fails.
    /// </para>
    /// </remarks>
    public class CalciteColumnReadingMeasurementTests
    {

        /// <summary>
        /// Int-sized integers, <c>42.0</c>, a fraction, 2^31, 2^53 + 1, 10^19, a string, the booleans, a null, and an
        /// absent path.
        /// </summary>
        static readonly string?[] Values = { "42", "-7", "42.0", "42.5", "2147483648", "9007199254740993", "10000000000000000000", "\"42\"", "true", "false", "null", null };

        /// <summary>
        /// Each column reads a value as its type, a whole number whatever its spelling, and refuses the rest; and
        /// compares and orders what it read as its type.
        /// </summary>
        /// <remarks>
        /// <c>DOUBLE</c> reads 2^53 + 1 as 2^53, which is the double nearest. The comparisons are asked over the values
        /// the column reads, a letter a value: <c>T</c>, <c>F</c>, or <c>U</c> for unknown.
        /// </remarks>
        [Fact]
        public void EachColumnReadsAndComparesAsItsType()
        {
            Reads(SqlTypeName.BIGINT).Should().Equal("42", "-7", "42", "raises", "2147483648", "9007199254740993", "raises", "raises", "raises", "raises", "null", "null");
            Reads(SqlTypeName.INTEGER).Should().Equal("42", "-7", "42", "raises", "raises", "raises", "raises", "raises", "raises", "raises", "null", "null");
            Reads(SqlTypeName.DOUBLE).Should().Equal("42.0", "-7.0", "42.0", "42.5", "2.147483648E9", "9.007199254740992E15", "1.0E19", "raises", "raises", "raises", "null", "null");
            Reads(SqlTypeName.BOOLEAN).Should().Equal("raises", "raises", "raises", "raises", "raises", "raises", "raises", "raises", "true", "false", "null", "null");

            using var connection = MaterialisedTable.Open();
            var numbers = new[] { "\"v\" = 42", "\"v\" <> 42", "\"v\" < 42", "\"v\" <= 42", "\"v\" > 42", "\"v\" >= 42", "\"v\" = 42.5", "\"v\" < 42.5", "\"v\" IS NULL" };

            // Over 42, -7, 42.0, 2^31, 2^53 + 1, a null and an absent path.
            Readable(SqlTypeName.BIGINT).Ask(connection, numbers).Should().Equal(
                "TFTFFUU", "FTFTTUU", "FTFFFUU", "TTTFFUU", "FFFTTUU", "TFTTTUU", "FFFFFUU", "TTTFFUU", "FFFFFTT");

            // Over 42, -7, 42.0, a null and an absent path.
            Readable(SqlTypeName.INTEGER).Ask(connection, numbers).Should().Equal(
                "TFTUU", "FTFUU", "FTFUU", "TTTUU", "FFFUU", "TFTUU", "FFFUU", "TTTUU", "FFFTT");

            // Over 42, -7, 42.0, 42.5, 2^31, 2^53 + 1, 10^19, a null and an absent path.
            Readable(SqlTypeName.DOUBLE).Ask(connection, numbers).Should().Equal(
                "TFTFFFFUU", "FTFTTTTUU", "FTFFFFFUU", "TTTFFFFUU", "FFFTTTTUU", "TFTTTTTUU", "FFFTFFFUU", "TTTFFFFUU", "FFFFFFFTT");

            // Over true, false, a null and an absent path.
            Readable(SqlTypeName.BOOLEAN).Ask(connection, "\"v\" = TRUE", "\"v\" <> TRUE", "\"v\" < TRUE", "\"v\" <= TRUE", "\"v\" > TRUE", "\"v\" >= TRUE", "\"v\" IS NULL").Should().Equal(
                "TFUU", "FTUU", "FTUU", "TTUU", "FFUU", "TFUU", "FFTT");

            // Where RETURNING raises over what the column reads.
            MaterialisedTable.Of(new[] { "{\"v\":42.0}" }).Failure(connection, "JSON_VALUE(\"d\", '$.v' RETURNING INTEGER) = 42").Should().BeOfType<InvalidCastException>("Jackson reads 42.0 as a Double");
            MaterialisedTable.Of(new[] { "{\"v\":42}" }).Failure(connection, "JSON_VALUE(\"d\", '$.v' RETURNING BIGINT) = 42").Should().BeOfType<InvalidCastException>("Jackson reads 42 as an Integer");
        }

        static string[] Reads(SqlTypeName type) => Values.Select(v =>
        {
            try
            {
                return MaterialisedTable.Read(v, type)?.ToString() ?? "null";
            }
            catch (CosmosMaterializationException)
            {
                return "raises";
            }
        }).ToArray();

        static MaterialisedTable Readable(SqlTypeName type)
        {
            var reads = Reads(type);
            return MaterialisedTable.Of(type, Values.Where((_, i) => reads[i] != "raises"));
        }

    }

}
