using System;

using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// A JSON scalar compares as JSON does, whatever CLR value it was made from. #198.
    /// </summary>
    public class JsonScalarTests
    {

        /// <summary>
        /// One number is one value however it was boxed: a schema's double, a query's exact decimal, an
        /// integer either side read.
        /// </summary>
        [Theory]
        [InlineData(1.5d, "1.5")]
        [InlineData(1d, "1.0")]
        [InlineData(1d, "1")]
        [InlineData(-0.25d, "-0.250")]
        public void ANumberIsOneValueHoweverItWasBoxed(double approximate, string exact)
        {
            var fromDouble = JsonScalar.From(approximate);
            var fromDecimal = JsonScalar.From(decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture));

            fromDouble.Should().Be(fromDecimal);
            fromDouble.GetHashCode().Should().Be(fromDecimal.GetHashCode(), "equal values hash alike, or a domain's set equality breaks");
        }

        [Fact]
        public void AnIntegerIsTheSameNumberAsItsDecimalSpelling()
        {
            JsonScalar.From(1L).Should().Be(JsonScalar.From(1.0m));
            JsonScalar.TryFrom(7, out var fromInt).Should().BeTrue();
            JsonScalar.TryFrom(7.0m, out var fromDecimal).Should().BeTrue();
            fromInt.Should().Be(fromDecimal);
        }

        /// <summary>
        /// Two integers a double would round together stay two values: a fact about one is no fact about
        /// the other.
        /// </summary>
        [Fact]
        public void LargeIntegersStayDistinct()
        {
            JsonScalar.From(9_007_199_254_740_993L).Should().NotBe(JsonScalar.From(9_007_199_254_740_992L));
        }

        /// <summary>
        /// A number a decimal cannot hold is still a number, equal only to itself.
        /// </summary>
        [Fact]
        public void ANumberBeyondADecimalIsStillANumber()
        {
            var huge = JsonScalar.From(1e300);

            huge.Should().Be(JsonScalar.From(1e300));
            huge.Should().NotBe(JsonScalar.From(1e299));
            huge.Type.Should().Be(JsonType.Integer);
            JsonScalar.From(1e-300).Type.Should().Be(JsonType.Number, "too small for a decimal, and not zero");
        }

        [Fact]
        public void TheTypeIsJsons()
        {
            JsonScalar.Null.Type.Should().Be(JsonType.Null);
            ((JsonScalar)true).Type.Should().Be(JsonType.Boolean);
            ((JsonScalar)"a").Type.Should().Be(JsonType.String);
            ((JsonScalar)2L).Type.Should().Be(JsonType.Integer);
            ((JsonScalar)2.0m).Type.Should().Be(JsonType.Integer, "a whole number is an integer, as JSON Schema says");
            ((JsonScalar)2.5).Type.Should().Be(JsonType.Number);
        }

        [Fact]
        public void TheDefaultIsTheNull()
        {
            default(JsonScalar).Should().Be(JsonScalar.Null);
            default(JsonScalar).IsNull.Should().BeTrue();
            ((JsonScalar)(string?)null).Should().Be(JsonScalar.Null);
        }

        /// <summary>
        /// Kinds never meet: the string "1" is not the number 1, nor the string "true" the boolean.
        /// </summary>
        [Fact]
        public void KindsAreDistinct()
        {
            ((JsonScalar)"1").Should().NotBe((JsonScalar)1L);
            ((JsonScalar)"true").Should().NotBe((JsonScalar)true);
            ((JsonScalar)0L).Should().NotBe(JsonScalar.Null);
            ((JsonScalar)"a").Should().NotBe((JsonScalar)"A", "strings compare ordinally");
        }

        /// <summary>
        /// A CLR value that is no JSON scalar is refused rather than read as one.
        /// </summary>
        [Fact]
        public void WhatIsNoScalarIsRefused()
        {
            JsonScalar.TryFrom(Guid.NewGuid(), out _).Should().BeFalse();
            JsonScalar.TryFrom(new object(), out _).Should().BeFalse();
            JsonScalar.TryFrom(null, out var none).Should().BeTrue();
            none.Should().Be(JsonScalar.Null);
        }

        [Fact]
        public void TheClrValueIsWhatAPartitionKeyTakes()
        {
            JsonScalar.From(1.5).ToClrValue().Should().Be(1.5m);
            JsonScalar.From("a").ToClrValue().Should().Be("a");
            JsonScalar.From(true).ToClrValue().Should().Be(true);
            JsonScalar.Null.ToClrValue().Should().BeNull();
        }

    }

}
