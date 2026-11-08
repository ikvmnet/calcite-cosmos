using System;
using System.Data.Common;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Tests.Sql;

using FluentAssertions;

using org.apache.calcite.sql.type;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What a reading's order holds in process, which S's pair rule hands a comparison of two readings (G3): which readings
    /// read a string as the string, so that two compare as the strings do, and over which two equal values read alike
    /// compare equal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A comparison of two paths' readings is placed by the order the pair's cells part its strings by only where both
    /// readings read a string as itself; and a cell on which the two hold one value only where equal values read alike
    /// compare equal. The second fails for a double: a NaN compares as nothing alike, itself included, so a reading that
    /// may hold one is placed by neither.
    /// </para>
    /// <para>
    /// Over documents in a scanned table, so that nothing is folded at planning, and over typed and raw columns the adapter's
    /// reader fills (<see cref="MaterialisedTable"/>), each value read to a value; what a reading makes of a value it reads
    /// as null or raises on is its null test's, which the pair reads first.
    /// </para>
    /// </remarks>
    public class CalciteReaderOrderMeasurementTests
    {

        static readonly string[] Ops = ["=", "<>", "<", "<=", ">", ">="];

        static string Text(string place) => $"JSON_VALUE(\"d\", '$.{place}')";

        /// <summary>Documents holding each value at both <c>a</c> and <c>b</c>.</summary>
        static MaterialisedTable Twice(params string[] values) => MaterialisedTable.Of(values.Select(v => $"{{\"a\":{v},\"b\":{v}}}").ToArray());

        /// <summary>A row a value, the value in both columns <c>a</c> and <c>b</c> of a type.</summary>
        static MaterialisedTable Columns(SqlTypeName type, params string[] values) =>
            new([("a", type), ("b", type)], values.Select(v => ("{}", new string?[] { v, v })).ToArray());

        /// <summary>A reading's answers to each operator over <c>a</c> and <c>b</c>, a string an operator.</summary>
        static string[] Compared(DbConnection connection, MaterialisedTable table, Func<string, string> reading) =>
            table.Ask(connection, [.. Ops.Select(op => $"{reading("a")} {op} {reading("b")}")]);

        static string[] Each(int rows, params char[] answers) => [.. answers.Select(a => new string(a, rows))];

        /// <summary>
        /// A computed text place and a <c>VARCHAR</c> or <c>CHAR</c> value compare as the strings they hold, by UTF-16 unit as
        /// the accessor's text does (<c>Calcite.TextOrderIsByCodeUnit</c>): U+E000 and U+FFFD above U+1F600, <c>B</c> below
        /// <c>B </c>, nothing padded. Each is null over a stored null and an absent path, as the accessor is, so S reads
        /// it as the accessor over the classes its rows name (G7).
        /// </summary>
        [Fact, CosmosPins("Calcite.TextPlacesCompareAsTheirStrings")]
        public void ATextPlaceComparesAsTheStringItHolds()
        {
            using var connection = MaterialisedTable.Open();
            var documents = MaterialisedTable.Of(new[]
            {
                "{\"a\":\"\\uE000\",\"b\":\"\\uD83D\\uDE00\"}",
                "{\"a\":\"\\uFFFD\",\"b\":\"\\uD83D\\uDE00\"}",
                "{\"a\":\"B\",\"b\":\"B \"}",
                "{\"a\":\"30\",\"b\":\"BIKES\"}",
                "{\"a\":\"BIKES\",\"b\":\"BIKES\"}",
            });

            foreach (var reading in new Func<string, string>[] { Text, p => $"UPPER({Text(p)})", p => $"SUBSTRING({Text(p)} FROM 1)" })
                documents.Ask(connection, $"{reading("a")} < {reading("b")}", $"{reading("a")} = {reading("b")}", $"{reading("a")} > {reading("b")}")
                    .Should().Equal(["FFTTF", "FFFFT", "TTFFF"], reading("v"));

            foreach (var type in new[] { SqlTypeName.CHAR, SqlTypeName.VARCHAR })
                new MaterialisedTable([("a", type), ("b", type)], new[]
                {
                    ("{}", new string?[] { "\"\\uE000\"", "\"\\uD83D\\uDE00\"" }), ("{}", new string?[] { "\"B\"", "\"B \"" }), ("{}", new string?[] { "\"30\"", "\"BIKES\"" }),
                }).Ask(connection, "\"a\" < \"b\"", "\"a\" = \"b\"", "\"a\" > \"b\"").Should().Equal(["FTT", "FFF", "TFF"], type.getName());

            // LIKE matches each as the string it holds, as it does the accessor's text: a run of any units, nothing padded.
            documents.Ask(connection, $"{Text("b")} LIKE 'B%'", $"{Text("b")} LIKE 'B'", $"{Text("a")} LIKE '%0'", $"{Text("b")} LIKE '%'")
                .Should().Equal(["FFTTT", "FFFFF", "FFFTF", "TTTTT"], "the accessor's text");
            foreach (var type in new[] { SqlTypeName.CHAR, SqlTypeName.VARCHAR })
                new MaterialisedTable([("a", type), ("b", type)], new[]
                {
                    ("{}", new string?[] { "\"\\uE000\"", "\"\\uD83D\\uDE00\"" }), ("{}", new string?[] { "\"B\"", "\"B \"" }), ("{}", new string?[] { "\"30\"", "\"BIKES\"" }),
                }).Ask(connection, "\"b\" LIKE 'B%'", "\"b\" LIKE 'B'", "\"a\" LIKE '%0'", "\"b\" LIKE '%'").Should().Equal(["FTT", "FFF", "FFT", "TTT"], type.getName());

            // A stored null and an absent path are null to each: TRUE to IS NULL and UNKNOWN to a comparison.
            var nothing = MaterialisedTable.Of(new[] { "{\"a\":null,\"b\":\"B\"}", "{\"b\":\"B\"}" });
            foreach (var reading in new Func<string, string>[] { p => $"UPPER({Text(p)})", p => $"SUBSTRING({Text(p)} FROM 1)" })
                nothing.Ask(connection, $"{reading("a")} IS NULL", $"{reading("a")} = {reading("b")}", $"{reading("a")} < {reading("b")}").Should().Equal(["TT", "UU", "UU"], reading("v"));

            foreach (var type in new[] { SqlTypeName.CHAR, SqlTypeName.VARCHAR })
                new MaterialisedTable([("a", type), ("b", type)], new[] { ("{}", new string?[] { "null", "\"B\"" }), ("{}", new string?[] { null, "\"B\"" }) })
                    .Ask(connection, "\"a\" IS NULL", "\"a\" = \"b\"", "\"a\" < \"b\"", "\"a\" LIKE '%'").Should().Equal(["TT", "UU", "UU", "UU"], type.getName());
        }

        /// <summary>
        /// Two equal values read alike compare equal wherever the reading reads a value: the accessor's text over every
        /// scalar, a boolean's, an integer type's and <c>DECIMAL</c>'s column, <c>RETURNING BOOLEAN</c> and <c>DECIMAL</c>,
        /// a <c>CHAR</c> column and a computed text place, the text cast to <c>BOOLEAN</c>, <c>INTEGER</c> and
        /// <c>BIGINT</c>, and a raw value cast to <c>VARCHAR</c> through each column, over every value each reads as one.
        /// </summary>
        [Fact, CosmosPins("Calcite.EqualValuesReadAlikeCompareEqual")]
        public void EqualValuesReadAlikeCompareEqual()
        {
            using var connection = MaterialisedTable.Open();
            void Alike(MaterialisedTable table, int rows, Func<string, string> reading) =>
                Compared(connection, table, reading).Should().Equal(Each(rows, 'T', 'F', 'F', 'T', 'F', 'T'), reading("v"));

            Alike(Twice("\"bikes\"", "\"\"", "\"\\uE000\"", "\"NaN\"", "30", "1e30", "2.5", "true", "false"), 9, Text);
            Alike(Twice("true", "false"), 2, p => $"JSON_VALUE(\"d\", '$.{p}' RETURNING BOOLEAN)");
            Alike(Twice("0", "7", "2.5", "1e30"), 4, p => $"JSON_VALUE(\"d\", '$.{p}' RETURNING DECIMAL)");
            Alike(Twice("true", "false", "\"true\"", "\"FALSE\""), 4, p => $"CAST({Text(p)} AS BOOLEAN)");
            Alike(Twice("\"bikes\"", "\"\"", "\"\\uE000\""), 3, p => $"UPPER({Text(p)})");
            Alike(Twice("\"bikes\"", "\"\"", "\"\\uE000\""), 3, p => $"{Text(p)} || 'x'");
            foreach (var target in new[] { "INTEGER", "BIGINT" })
                Alike(Twice("7", "\"7\"", "\"-7\""), 3, p => $"CAST({Text(p)} AS {target})");

            Alike(Columns(SqlTypeName.BOOLEAN, "true", "false"), 2, p => $"\"{p}\"");
            Alike(Columns(SqlTypeName.CHAR, "\"bikes\"", "\"\"", "\"b\""), 3, p => $"\"{p}\"");
            foreach (var type in new[] { SqlTypeName.TINYINT, SqlTypeName.SMALLINT, SqlTypeName.INTEGER, SqlTypeName.BIGINT })
                Alike(Columns(type, "0", "7", "-7", "100"), 4, p => $"\"{p}\"");
            Alike(Columns(SqlTypeName.DECIMAL, "0", "7", "-7", "2.5"), 4, p => $"\"{p}\"");

            Alike(Columns(SqlTypeName.VARIANT, "\"bikes\"", "\"\"", "\"NaN\""), 3, p => $"CAST(\"{p}\" AS VARCHAR)");
            Alike(Columns(SqlTypeName.ANY, "\"bikes\"", "\"\"", "42", "42.0", "true", "{\"a\":1}", "[1]"), 7, p => $"CAST(\"{p}\" AS VARCHAR)");
            Alike(Columns(SqlTypeName.VARCHAR, "\"bikes\"", "\"\"", "\"NaN\""), 3, p => $"CAST(\"{p}\" AS VARCHAR)");
        }

        /// <summary>
        /// A double that is NaN compares as nothing alike, itself included: <c>=</c>, <c>&lt;</c>, <c>&lt;=</c>,
        /// <c>&gt;</c> and <c>&gt;=</c> FALSE and <c>&lt;&gt;</c> TRUE, whether the text <c>'NaN'</c> cast to <c>DOUBLE</c>,
        /// <c>SQRT</c> or <c>POWER</c> of a negative, or <c>0/0</c> over a <c>FLOAT</c>, <c>REAL</c> or <c>DOUBLE</c> column.
        /// </summary>
        [Fact, CosmosPins("Calcite.ANaNComparesAsNothingAlike")]
        public void ANaNComparesAsNothingAlike()
        {
            using var connection = MaterialisedTable.Open();
            void Apart(MaterialisedTable table, Func<string, string> reading) =>
                Compared(connection, table, reading).Should().Equal(Each(1, 'F', 'T', 'F', 'F', 'F', 'F'), reading("v"));

            Apart(Twice("\"NaN\""), p => $"CAST({Text(p)} AS DOUBLE)");
            Apart(Twice("-1"), p => $"POWER(JSON_VALUE(\"d\", '$.{p}' RETURNING DECIMAL), 0.5)");
            foreach (var type in new[] { SqlTypeName.INTEGER, SqlTypeName.BIGINT, SqlTypeName.DECIMAL, SqlTypeName.DOUBLE })
                Apart(Columns(type, "-1"), p => $"SQRT(\"{p}\")");
            foreach (var type in new[] { SqlTypeName.FLOAT, SqlTypeName.REAL, SqlTypeName.DOUBLE })
                Apart(Columns(type, "0"), p => $"(\"{p}\" / \"{p}\")");
        }

    }

}
