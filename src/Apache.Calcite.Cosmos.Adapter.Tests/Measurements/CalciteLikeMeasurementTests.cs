using Apache.Calcite.Cosmos.Adapter.Tests.Sql;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's <c>LIKE</c> reads each unit of its pattern as, which a cell and the facts graph decide a match by (G7):
    /// <c>%</c> any run of characters, line terminators among them; <c>_</c> one code point, a surrogate pair one, that is no
    /// Java line terminator; and every other unit the character itself, a backslash, a bracket and a dot among them, there
    /// being no escape unless one is written.
    /// </summary>
    /// <remarks>
    /// Over documents in a scanned table, so that nothing is folded at planning: the accessor's text of each, matched
    /// against patterns that part each reading from the others a unit could have (an escape, a range, a regular
    /// expression's dot, a UTF-16 unit).
    /// </remarks>
    public class CalciteLikeMeasurementTests
    {

        /// <summary>
        /// <c>%</c> is any run, <c>_</c> one code point that is no line terminator, and any other unit itself.
        /// </summary>
        [Fact, CosmosPins("Calcite.LikeReadsItsPatternsUnits")]
        public void LikeReadsItsPatternsUnits()
        {
            using var connection = MaterialisedTable.Open();
            var documents = MaterialisedTable.Of(new[]
            {
                "{\"v\":\"axb\"}", "{\"v\":\"a\\nb\"}", "{\"v\":\"a\\rb\"}", "{\"v\":\"a\\u0085b\"}", "{\"v\":\"a\\u2028b\"}", "{\"v\":\"a\\u2029b\"}",
                "{\"v\":\"a\\uD83D\\uDE00b\"}", "{\"v\":\"a\\\\b\"}", "{\"v\":\"a_b\"}", "{\"v\":\"[a]\"}", "{\"v\":\"a.c\"}", "{\"v\":\"abc\"}",
            });

            const string v = "JSON_VALUE(\"d\", '$.v')";
            documents.Ask(connection, $"{v} LIKE 'a_b'", $"{v} LIKE 'a__b'", $"{v} LIKE 'a%b'").Should().Equal(
                ["TFFFFFTTTFFF", "FFFFFFFFFFFF", "TTTTTTTTTFFF"],
                "_ is one code point, a surrogate pair one, and no line terminator; % any run, line terminators among them");
            documents.Ask(connection, $"{v} LIKE 'a\\b'", $"{v} LIKE 'a\\%'", $"{v} LIKE '[a]'", $"{v} LIKE 'a.c'").Should().Equal(
                ["FFFFFFFTFFFF", "FFFFFFFTFFFF", "FFFFFFFFFTFF", "FFFFFFFFFFTF"],
                "a backslash is itself, escaping nothing, a bracket no range and a dot no wildcard");
        }

        /// <summary>
        /// U+0000 is the escape where no <c>ESCAPE</c> is written (<c>Like.sqlToRegexLike</c>, its escape character 0): before
        /// <c>_</c>, <c>%</c> or another U+0000 it reads that unit as itself, and before any other unit or at the pattern's end
        /// it raises. So no row reads it as itself, and a pattern holding it is unread (<c>Calcite.LikeReadsItsPatternsUnits</c>).
        /// </summary>
        [Fact, CosmosPins("Calcite.LikeEscapesByNul")]
        public void LikeEscapesByNul()
        {
            using var connection = MaterialisedTable.Open();
            var documents = MaterialisedTable.Of(new[]
            {
                "{\"v\":\"a_\"}", "{\"v\":\"a\\u0000x\"}", "{\"v\":\"a\\u0000_\"}", "{\"v\":\"a%\"}",
                "{\"v\":\"a\\u0000\"}", "{\"v\":\"a\\u0000\\u0000\"}", "{\"v\":\"ab\"}", "{\"v\":\"a\\u0000b\"}",
            });

            const string v = "JSON_VALUE(\"d\", '$.v')";
            documents.Ask(connection, $"{v} LIKE 'a\0_'", $"{v} LIKE 'a\0%'", $"{v} LIKE 'a\0\0'", $"{v} LIKE '%\0%'", $"{v} NOT LIKE 'a\0_'").Should().Equal(
                ["TFFFFFFF", "FFFTFFFF", "FFFFTFFF", "FFFTFFFF", "FTTTTTTT"],
                "U+0000 before _, % or U+0000 reads that unit as itself, matching it alone");
            documents.Ask(connection, $"{v} LIKE 'a_'").Should().Equal(["TFFTTFTF"], "unescaped, _ is one code point, U+0000 among them");
            foreach (var pattern in new[] { "a\0x", "a\0b", "a\0", "B\0" })
            {
                var failure = documents.Failure(connection, $"{v} LIKE '{pattern}'");
                failure.Should().NotBeNull("U+0000 before another unit, or at the end, escapes nothing");
                failure!.Message.Should().StartWith("Invalid escape sequence");
            }
        }

    }

}
