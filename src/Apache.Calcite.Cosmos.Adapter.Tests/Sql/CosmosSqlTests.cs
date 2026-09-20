using System;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    public class CosmosSqlTests
    {

        static string Property(string name)
        {
            var builder = new StringBuilder();
            CosmosSql.WritePropertyAccess(builder, name);
            return builder.ToString();
        }

        static string Literal(object? value)
        {
            var builder = new StringBuilder();
            CosmosSql.WriteLiteral(builder, value);
            return builder.ToString();
        }

        [Theory]
        [InlineData("name")]
        [InlineData("_ts")]
        [InlineData("_etag")]
        [InlineData("a1")]
        [InlineData("A_B_9")]
        public void BareIdentifiersAreAccepted(string name)
        {
            CosmosSql.IsBareIdentifier(name).Should().BeTrue();
        }

        [Theory]
        [InlineData("")]
        [InlineData("odd name")]
        [InlineData("0abc")]
        [InlineData("has-dash")]
        [InlineData("has.dot")]
        [InlineData("café")]
        public void NonBareIdentifiersAreRejected(string name)
        {
            CosmosSql.IsBareIdentifier(name).Should().BeFalse();
        }

        [Theory]
        [InlineData("TOP")]
        [InlineData("top")]
        [InlineData("VALUE")]
        [InlineData("order")]
        public void ReservedWordsAreNotBareIdentifiers(string name)
        {
            CosmosSql.IsBareIdentifier(name).Should().BeFalse();
        }

        [Fact]
        public void BarePropertyUsesDotNotation()
        {
            Property("name").Should().Be(".name");
        }

        [Fact]
        public void NonBarePropertyUsesBracketNotation()
        {
            Property("odd name").Should().Be("[\"odd name\"]");
        }

        [Fact]
        public void ReservedPropertyUsesBracketNotation()
        {
            Property("VALUE").Should().Be("[\"VALUE\"]");
        }

        [Fact]
        public void StringLiteralEscapesQuotesAndBackslashes()
        {
            Literal("say \"hi\"\\").Should().Be("\"say \\\"hi\\\"\\\\\"");
        }

        [Fact]
        public void StringLiteralEscapesControlCharacters()
        {
            Literal("a\r\n\tb\u0001").Should().Be("\"a\\r\\n\\tb\\u0001\"");
        }

        [Fact]
        public void PropertyNameWithQuoteIsEscapedInBrackets()
        {
            Property("a\"b").Should().Be("[\"a\\\"b\"]");
        }

        [Fact]
        public void NullRendersAsJsonNull()
        {
            Literal(null).Should().Be("null");
        }

        [Theory]
        [InlineData(true, "true")]
        [InlineData(false, "false")]
        public void BooleanRendersAsJsonBoolean(bool value, string expected)
        {
            Literal(value).Should().Be(expected);
        }

        [Fact]
        public void IntegersRenderWithoutSeparators()
        {
            Literal(1234567).Should().Be("1234567");
            Literal(-42L).Should().Be("-42");
        }

        [Fact]
        public void DecimalRendersInvariant()
        {
            Literal(1.5m).Should().Be("1.5");
        }

        [Fact]
        public void DoubleRoundTrips()
        {
            Literal(0.1d).Should().Be("0.1");
            Literal(1e20d).Should().Be("1E+20");
        }

        [Fact]
        public void NonFiniteDoubleIsRejected()
        {
            var act = () => Literal(double.NaN);
            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void UnsupportedTypeIsRejected()
        {
            var act = () => Literal(new object());
            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void NegativeIndexIsRejected()
        {
            var act = () => CosmosSql.WriteIndexAccess(new StringBuilder(), -1);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

    }

}
