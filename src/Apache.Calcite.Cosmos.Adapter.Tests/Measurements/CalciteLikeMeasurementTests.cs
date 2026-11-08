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

    }

}
