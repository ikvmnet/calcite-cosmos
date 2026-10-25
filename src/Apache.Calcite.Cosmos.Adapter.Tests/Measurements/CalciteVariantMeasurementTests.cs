using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using org.apache.calcite.sql.type;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What a raw reading compares as: the value the service holds, in each of the three columns the IR reads as
    /// <c>raw</c>, against a string, a number and a boolean.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One reading, three columns.</b> <c>CosmosPredicateLoader</c> reads a promoted path's <c>VARIANT</c>, an
    /// <c>ANY</c> and a field typed <c>VARCHAR</c> alike, as the value the service holds. Each is filled by its own
    /// reader — <c>GetVariant</c>, <c>GetNatural</c>, <c>GetString</c> — and Calcite compares each by its own rules, so a
    /// raw reading's answer on a cell is the union of three.
    /// </para>
    /// <para>
    /// No service: an in-process table whose column holds what the reader made of each value. The values are every JSON
    /// type, strings that differ from <c>bikes</c> by case or a trailing space, and U+E000, U+FFFD and U+1F600, where the
    /// two string orders part.
    /// </para>
    /// </remarks>
    public class CalciteVariantMeasurementTests
    {

        /// <summary>
        /// The values, by index: seven strings, 0–6; 42 and 42.0, 7–8; the booleans, 9–10; an object, an array, a null
        /// and an absent path, 11–14.
        /// </summary>
        static readonly string?[] Values =
        {
            "\"bikes\"", "\"Bikes\"", "\"bikes \"", "\"\"", "\"\\uE000\"", "\"\\uFFFD\"", "\"\\uD83D\\uDE00\"",
            "42", "42.0", "true", "false", "{\"a\":1}", "[1]", "null", null,
        };

        /// <summary>The strings, a null and an absent path: what a <c>VARCHAR</c> column reads.</summary>
        static readonly string?[] Strings = Values.Take(7).Append("null").Append(null).ToArray();

        /// <summary>The column of the table the raw cells read (<see cref="RawColumnsTable"/>), which these tests hold.</summary>
        static RawColumnsTable.Column Column(string name) => RawColumnsTable.Rows.Single(c => c.Name == name);

        /// <summary>A value of each kind but a string answered alike: what the table says a column makes of each.</summary>
        static Dictionary<string, RawColumnsTable.Answer> Kinds(RawColumnsTable.Answer text, RawColumnsTable.Answer others, RawColumnsTable.Answer? structure = null) => new()
        {
            [RawColumnsTable.String] = text, [RawColumnsTable.Number] = others, [RawColumnsTable.Boolean] = others, [RawColumnsTable.Structure] = structure ?? others,
        };

        /// <summary>
        /// A raw value is equal to a string where it is the string with the same UTF-16 units, and to nothing else: not
        /// another case, not a trailing space, not a number or a boolean.
        /// </summary>
        [Fact]
        public void ARawValueIsEqualToTheStringOfItsUnitsAlone()
        {
            using var connection = MaterialisedTable.Open();

            foreach (var type in new[] { SqlTypeName.VARIANT, SqlTypeName.ANY })
                MaterialisedTable.Of(type, Values).Ask(connection, "\"v\" = 'bikes'", "\"v\" <> 'bikes'", "\"v\" = U&'\\E000'").Should().Equal(
                    new[] { "TFFFFFFFFFFFFUU", "FTTTTTTTTTTTTUU", "FFFFTFFFFFFFFUU" }, type.getName());

            var varchar = MaterialisedTable.Of(SqlTypeName.VARCHAR, Strings);
            varchar.Ask(connection, "\"v\" = 'bikes'", "\"v\" <> 'bikes'").Should().Equal("TFFFFFFUU", "FTTTTTTUU");
            (varchar.Failure(connection, "\"v\" = U&'\\E000'")?.Message).Should().Contain("different charsets", "a VARCHAR column is Latin-1, and a literal beyond it is another");
        }

        /// <summary>
        /// A null and an absent path are SQL null in each column. A value of another type is unequal; an ordering across
        /// types raises; a <c>VARCHAR</c> column reads a string alone; and a <c>VARIANT</c> has no ordering, and is equal
        /// to a number boxed with its own SQL type alone. Within a type <c>ANY</c> orders as the type, and a number in its
        /// box: a long by its value, any other number by its double's.
        /// </summary>
        /// <remarks>
        /// <c>GetVariant</c> boxes an integer as <c>BIGINT</c> and any other number as <c>DOUBLE</c>, and the validator
        /// takes no bare number or boolean beside a <c>VARIANT</c>. Boxed, <c>42</c> is an <c>INTEGER</c>, equal to
        /// nothing a document holds.
        /// </remarks>
        [Fact]
        public void ARawNullIsSqlNullAndAnotherTypeIsUnequal()
        {
            using var connection = MaterialisedTable.Open();
            var variant = MaterialisedTable.Of(SqlTypeName.VARIANT, Values);
            var any = MaterialisedTable.Of(SqlTypeName.ANY, Values);
            var varchar = MaterialisedTable.Of(SqlTypeName.VARCHAR, Strings);

            variant.Ask(connection, "\"v\" IS NULL", "\"v\" = CAST(42 AS VARIANT)", "\"v\" = CAST(CAST(42 AS BIGINT) AS VARIANT)", "\"v\" = CAST(CAST(42 AS DOUBLE) AS VARIANT)", "\"v\" = CAST(TRUE AS VARIANT)").Should().Equal(
                "FFFFFFFFFFFFFTT", "FFFFFFFFFFFFFUU", "FFFFFFFTFFFFFUU", "FFFFFFFFTFFFFUU", "FFFFFFFFFTFFFUU");
            (variant.Failure(connection, "\"v\" = 42")?.Message).Should().Contain("Cannot apply '='", "a number is not compared with a VARIANT unboxed");
            (variant.Failure(connection, "\"v\" = TRUE")?.Message).Should().Contain("Cannot apply '='", "nor is a boolean");

            var none = MaterialisedTable.Of(SqlTypeName.VARIANT, new string?[0]);
            foreach (var op in new[] { "<", "<=", ">", ">=" })
                (none.Failure(connection, $"\"v\" {op} 'bikes'")?.Message).Should().Contain("VariantValue", $"{op} over a VARIANT has no method to call, whatever the rows hold");
            (Column("VARIANT").Orders, Column("ANY").Orders, Column("VARCHAR").Orders).Should().Be((false, true, true));

            any.Ask(connection, "\"v\" IS NULL", "\"v\" = 42", "\"v\" = TRUE").Should().Equal("FFFFFFFFFFFFFTT", "FFFFFFFTTFFFFUU", "FFFFFFFFFTFFFUU");

            // Within a type ANY orders as the type, a string by its UTF-16 units; across types it raises.
            MaterialisedTable.Of(SqlTypeName.ANY, Strings).Ask(connection, "\"v\" < 'bikes'", "\"v\" < U&'\\E000'").Should().Equal("FTFTFFFUU", "TTTTFFTUU");
            MaterialisedTable.Of(SqlTypeName.ANY, new[] { "42", "42.0", "null", null }).Ask(connection, "\"v\" < 42.5", "\"v\" > 42.5").Should().Equal("TTUU", "FFUU");
            MaterialisedTable.Of(SqlTypeName.ANY, new[] { "true", "false", "null", null }).Ask(connection, "\"v\" < TRUE").Should().Equal("FTUU");

            // A number in its box: a long by its value, and any other by its double's, 2^53 + 1 written with a point being 2^53.
            MaterialisedTable.Of(SqlTypeName.ANY, ["9007199254740993", "9007199254740993.0", "0.1"]).Ask(connection, "\"v\" = 9007199254740993", "\"v\" < 9007199254740993",
                "\"v\" = 0.1", "\"v\" > 0.1").Should().Equal("TFF", "FTT", "FFF", "TTT");

            // Two doubles compare by Double.compareTo, which puts a stored -0.0 below a double 0; mixed boxes by their values.
            MaterialisedTable.Of(SqlTypeName.ANY, ["-0.0", "0.0", "0"]).Ask(connection, "\"v\" < 0E0", "\"v\" = 0E0", "\"v\" < 0", "\"v\" = 0").Should().Equal("TFF", "FTT", "FFF", "TTT");
            var (text, numbers, booleans, structures) = (Values.Take(1), Values.Skip(7).Take(2), Values.Skip(9).Take(2), Values.Skip(11).Take(2));
            foreach (var (literal, others) in new[] { ("'bikes'", numbers.Concat(booleans)), ("42", text.Concat(booleans)), ("TRUE", text.Concat(numbers)) })
                foreach (var value in others.Concat(structures))
                    foreach (var op in new[] { "<", "<=", ">", ">=" })
                        (MaterialisedTable.Of(SqlTypeName.ANY, new[] { value }).Failure(connection, $"\"v\" {op} {literal}")?.Message).Should().Contain("Invalid types for comparison", $"{value} {op} {literal} orders across types");

            varchar.Ask(connection, "\"v\" IS NULL", "\"v\" <> 'bikes'", "\"v\" < 'bikes'").Should().Equal("FFFFFFFTT", "FTTTTTTUU", "FTFTFFFUU");
            foreach (var value in Values.Skip(7).Take(6))
                FluentActions.Invoking(() => MaterialisedTable.Read(value, SqlTypeName.VARCHAR)).Should().Throw<CosmosMaterializationException>($"a VARCHAR column reads {value} as no string");
            Column("VARCHAR").Read.Should().Equal(Kinds(RawColumnsTable.Answer.Itself, RawColumnsTable.Answer.Raises));
            foreach (var name in new[] { "VARIANT", "ANY" })
                Column(name).Read.Should().Equal(Kinds(RawColumnsTable.Answer.Itself, RawColumnsTable.Answer.Itself), $"{name} answers for a value of every kind above");
        }

        /// <summary>
        /// A cast of a raw value to text is each column's own: a string is itself and a null null in each; a <c>VARIANT</c>
        /// casts a number and a boolean to null and raises over a structure; an <c>ANY</c> renders each as Java renders the
        /// reader's box, a long in its digits and any other number as its double; and a <c>VARCHAR</c> column reads a string
        /// alone.
        /// </summary>
        [Fact]
        public void ACastOfARawValueToTextIsEachColumnsOwn()
        {
            using var connection = MaterialisedTable.Open();
            var scalars = Values.Take(11).Concat(Values.Skip(13)).Append("99999999999999999999").Append("9223372036854775807").ToArray();
            string[] asked = [.. new[] { "IS NULL", "= 'bikes'", "= '42'", "= '42.0'", "= 'true'", "= '1.0E20'", "= '9223372036854775807'", "< 'a'" }.Select(t => "CAST(\"v\" AS VARCHAR) " + t)];

            MaterialisedTable.Of(SqlTypeName.VARIANT, scalars).Ask(connection, asked).Should().Equal(
                "FFFFFFFTTTTTTTT", "TFFFFFFUUUUUUUU", "FFFFFFFUUUUUUUU", "FFFFFFFUUUUUUUU", "FFFFFFFUUUUUUUU", "FFFFFFFUUUUUUUU", "FFFFFFFUUUUUUUU", "FTFTFFFUUUUUUUU");
            MaterialisedTable.Of(SqlTypeName.ANY, scalars).Ask(connection, asked).Should().Equal(
                "FFFFFFFFFFFTTFF", "TFFFFFFFFFFUUFF", "FFFFFFFTFFFUUFF", "FFFFFFFFTFFUUFF", "FFFFFFFFFTFUUFF", "FFFFFFFFFFFUUTF", "FFFFFFFFFFFUUFT", "FTFTFFFTTFFUUTT");
            MaterialisedTable.Of(SqlTypeName.ANY, Values.Skip(11).Take(2)).Ask(connection, "CAST(\"v\" AS VARCHAR) = '{a=1}'", "CAST(\"v\" AS VARCHAR) = '[1]'").Should().Equal("TF", "FT");
            foreach (var structure in Values.Skip(11).Take(2))
                (MaterialisedTable.Of(SqlTypeName.VARIANT, [structure]).Failure(connection, "CAST(\"v\" AS VARCHAR) IS NULL")?.Message).Should().Contain("VariantValue", $"a VARIANT casts no {structure}");

            MaterialisedTable.Of(SqlTypeName.VARCHAR, Strings).Ask(connection, asked[0], asked[1], asked[^1]).Should().Equal("FFFFFFFTT", "TFFFFFFUU", "FTFTFFFUU");
            MaterialisedTable.Of(SqlTypeName.VARCHAR, ["42"]).Failure(connection, asked[0]).Should().BeOfType<CosmosMaterializationException>("a VARCHAR column reads no number");

            // The table the cells read is what this measures.
            Column("VARIANT").AsText.Should().Equal(Kinds(RawColumnsTable.Answer.Itself, RawColumnsTable.Answer.Null, RawColumnsTable.Answer.Raises));
            Column("ANY").AsText.Should().Equal(Kinds(RawColumnsTable.Answer.Itself, RawColumnsTable.Answer.Itself));
            Column("VARCHAR").AsText.Should().Equal(Kinds(RawColumnsTable.Answer.Itself, RawColumnsTable.Answer.Raises));
        }

        /// <summary>
        /// A numeric cast of a raw value is each column's own, which no row states, so the rendering check cuts no cells for
        /// one (<c>ReaderEdgesTable</c>): an <c>ANY</c> converts the Java number the reader built, wrapping a long beyond an
        /// int and holding a double at the limit, and parses a string; a <c>VARIANT</c> raises beyond the type and casts a
        /// string to null; a <c>VARCHAR</c> column refuses a number.
        /// </summary>
        [Fact]
        public void ACastOfARawNumberIsEachColumnsOwn()
        {
            using var connection = MaterialisedTable.Open();
            MaterialisedTable.Of(SqlTypeName.ANY, ["4294967297", "1e30", "2.5", "\"7\""]).Ask(connection, "CAST(\"v\" AS INTEGER) = 1", "CAST(\"v\" AS INTEGER) = 2147483647",
                "CAST(\"v\" AS INTEGER) = 2", "CAST(\"v\" AS INTEGER) = 7").Should().Equal("TFFF", "FTFF", "FFTF", "FFFT");
            MaterialisedTable.Of(SqlTypeName.VARIANT, ["4294967296"]).Failure(connection, "CAST(\"v\" AS INTEGER) > 0").Should().BeOfType<java.lang.ArithmeticException>();
            MaterialisedTable.Of(SqlTypeName.VARIANT, ["\"7\""]).Ask(connection, "CAST(\"v\" AS INTEGER) IS NULL").Should().Equal("T");
            MaterialisedTable.Of(SqlTypeName.VARCHAR, ["30"]).Failure(connection, "CAST(\"v\" AS INTEGER) > 0").Should().BeOfType<CosmosMaterializationException>();
        }

    }

}
