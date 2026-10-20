using FluentAssertions;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// How Calcite orders the text <c>JSON_VALUE</c> renders: by UTF-16 code unit, which is not the service's order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service orders a document's strings by code point (<c>Service.StringOrderIsCodePoint</c>), so U+E000 is below
    /// U+1F600 there; by UTF-16 unit U+1F600 is the surrogate U+D83D first, and below U+E000. A text ordering is proved
    /// at the service only where the two agree, and which side of a literal a string falls on in process is this.
    /// </para>
    /// <para>
    /// Over documents in a scanned table, so that nothing is folded at planning: a literal beyond Latin-1 is another
    /// charset to the validator, and two renderings compared are not.
    /// </para>
    /// </remarks>
    public class CalciteTextOrderMeasurementTests
    {

        /// <summary>
        /// Text compares by UTF-16 code unit: U+E000 and U+FFFD above U+1F600, <c>true</c> and <c>false</c> above
        /// <c>bikes</c>, <c>30</c> below.
        /// </summary>
        [Fact]
        public void TextComparesByUtf16Unit()
        {
            using var connection = MaterialisedTable.Open();
            var documents = MaterialisedTable.Of(new[]
            {
                "{\"v\":\"\\uE000\",\"w\":\"\\uD83D\\uDE00\"}",
                "{\"v\":\"\\uFFFD\",\"w\":\"\\uD83D\\uDE00\"}",
                "{\"v\":\"true\",\"w\":\"bikes\"}",
                "{\"v\":\"false\",\"w\":\"bikes\"}",
                "{\"v\":\"30\",\"w\":\"bikes\"}",
                "{\"v\":\"bikes\",\"w\":\"bikes\"}",
            });

            const string v = "JSON_VALUE(\"d\", '$.v')";
            const string w = "JSON_VALUE(\"d\", '$.w')";
            documents.Ask(connection, $"{v} < {w}", $"{v} = {w}", $"{v} > {w}", $"{v} > 'bikes'", $"{w} < 'bikes'").Should().Equal(
                "FFFFTF", "FFFFFT", "TTTTFF", "TTTTFF", "FFFFFF");
        }

    }

}
