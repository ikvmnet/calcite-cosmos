using System;

using Apache.Calcite.Data;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's own string functions answer over characters outside ASCII: Java's, measured end to end, so that
    /// where the service's differ is on the record rather than rediscovered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other half of every rule a pushed string function is held to</b> (<c>CosmosStringFunctions</c>). The
    /// service counts <c>LENGTH</c> in code points and, on Azure, <c>SUBSTRING</c> in units with a split pair sent as
    /// U+FFFD; it maps case by a simple mapping; and it trims white space
    /// (<see cref="CosmosStringFunctionMeasurementTests"/>). Calcite counts UTF-16 units and keeps a split pair as a lone
    /// surrogate, maps case by Java's full mapping, and trims U+0020 alone. Each pair agrees over ASCII, over a string
    /// with no surrogate, and over one holding nothing the service trims, and that is what the rules ask.
    /// </para>
    /// <para>
    /// No service and no adapter: a <c>CalciteConnection</c> over literals, as
    /// <see cref="CalciteArithmeticMeasurementTests"/> asks. A literal is written <c>U&amp;'…'</c> where it holds a
    /// character outside the default charset, ISO-8859-1, or read out of a JSON literal where an operator will not take
    /// a UTF-16 one.
    /// </para>
    /// </remarks>
    public class CalciteStringFunctionMeasurementTests
    {

        static object? Ask(string expression)
        {
            using var connection = new CalciteConnection(new CalciteConnectionStringBuilder().ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT " + expression;

            using var reader = command.ExecuteReader();
            reader.Read().Should().BeTrue();

            return reader.IsDBNull(0) ? null : reader.GetValue(0);
        }

        static string? Text(string expression) => (string?)Ask(expression);

        static int Number(string expression) => Convert.ToInt32(Ask(expression), System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// <c>CHAR_LENGTH</c> counts UTF-16 units, and <c>SUBSTRING</c>, <c>POSITION</c> and the rest count positions in
        /// them: a window splitting a pair holds a lone surrogate.
        /// </summary>
        [Fact]
        public void TheCountsAreInUnits()
        {
            Number("CHAR_LENGTH(U&'a\\D83D\\DE00b')").Should().Be(4, "U+1F600 is two units");
            Number("CHARACTER_LENGTH(U&'a\\00DFb')").Should().Be(3, "a character of the basic plane is one");
            Text("SUBSTRING(U&'a\\D83D\\DE00b' FROM 2 FOR 1)").Should().Be("\uD83D", "the window holds the lead surrogate alone");
            Text("SUBSTRING(U&'a\\D83D\\DE00b' FROM 2 FOR 2)").Should().Be("\U0001F600");
            Number("POSITION('b' IN U&'a\\D83D\\DE00b')").Should().Be(4);
        }

        /// <summary>
        /// <c>UPPER</c> and <c>LOWER</c> are Java's full mapping, which writes more than one character for some: ß is
        /// <c>SS</c>, U+FB00 <c>FF</c>, U+0130 <c>i</c> and U+0307; U+017F is <c>S</c> and U+212A <c>k</c>. Over ASCII it is
        /// the plain mapping.
        /// </summary>
        [Fact]
        public void CaseMappingIsJavasFullMapping()
        {
            Text("UPPER(U&'a\\00DFb')").Should().Be("ASSB");
            Text("UPPER(U&'a\\FB00b')").Should().Be("AFFB");
            Text("UPPER(U&'a\\017Fb')").Should().Be("ASB");
            Text("LOWER(U&'a\\0130b')").Should().Be("ai\u0307b");
            Text("LOWER(U&'a\\212Ab')").Should().Be("akb");
            Text("UPPER('aBc')").Should().Be("ABC");
            Text("LOWER('aBc')").Should().Be("abc");
        }

        /// <summary>
        /// <c>TRIM</c> with no character named, and so <c>LTRIM</c> and <c>RTRIM</c>, remove U+0020 alone: a tab, U+00A0
        /// and U+3000 stay.
        /// </summary>
        [Fact]
        public void TrimRemovesSpacesAlone()
        {
            // A U& literal is UTF-16 and the space TRIM names is not, and Calcite will not compare the two; a string read
            // out of a document, as the adapter's in-process plan reads one, is any character in the column's charset.
            Text("TRIM(JSON_VALUE('{\"s\":\"\\u0009a\\u0009\"}', '$.s'))").Should().Be("\ta\t");
            Text("TRIM(JSON_VALUE('{\"s\":\"\\u00A0a\\u3000\"}', '$.s'))").Should().Be("\u00A0a\u3000");
            Text("TRIM(LEADING FROM JSON_VALUE('{\"s\":\"\\u3000a\"}', '$.s'))").Should().Be("\u3000a");
            Text("TRIM(JSON_VALUE('{\"s\":\"  a  \"}', '$.s'))").Should().Be("a");
        }

    }

}
