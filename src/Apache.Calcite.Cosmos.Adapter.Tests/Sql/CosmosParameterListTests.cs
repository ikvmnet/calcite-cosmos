using System;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    public class CosmosParameterListTests
    {

        [Fact]
        public void NamesAreAssignedInOrder()
        {
            var p = new CosmosParameterList();

            p.Add("a").Should().Be("@p0");
            p.Add(2).Should().Be("@p1");
            p.Add(null).Should().Be("@p2");
            p.Count.Should().Be(3);
        }

        [Fact]
        public void ValuesArePreservedInOrder()
        {
            var p = new CosmosParameterList();
            p.Add("a");
            p.Add(2);

            p.Parameters.Should().SatisfyRespectively(
                x => { x.Name.Should().Be("@p0"); x.Value.Should().Be("a"); },
                x => { x.Name.Should().Be("@p1"); x.Value.Should().Be(2); });
        }

        [Fact]
        public void EqualValuesAreBoundSeparately()
        {
            var p = new CosmosParameterList();

            p.Add("a").Should().Be("@p0");
            p.Add("a").Should().Be("@p1");
        }

        [Fact]
        public void PrefixIsHonored()
        {
            var p = new CosmosParameterList("@arg");
            p.Add(1).Should().Be("@arg0");
        }

        [Theory]
        [InlineData("")]
        [InlineData("p")]
        public void InvalidPrefixIsRejected(string prefix)
        {
            var act = () => new CosmosParameterList(prefix);
            act.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// A value the SDK cannot send as itself is refused when it is bound, each by its own cause, at any depth, so the
        /// expression it stood in is computed in process: a string holding a lone surrogate, which the SDK's UTF-8 encoder
        /// refuses, and a NaN or an infinity, which it writes as a JSON string. A finite double and a pair are bound.
        /// </summary>
        [Fact]
        public void AValueTheSdkCannotSendAsItselfIsRefusedByItsCause()
        {
            var p = new CosmosParameterList();

            foreach (var value in new object[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, new object[] { 1.5, double.NaN } })
                FluentActions.Invoking(() => p.Add(value)).Should().Throw<CosmosTranslationException>()
                    .WithMessage("A value holding a NaN or an infinity is written by the SDK as a JSON string rather than a number, so it is computed in process.");

            foreach (var value in new object[] { "a\uD83D", new object[] { "a", "\uDE00" } })
                FluentActions.Invoking(() => p.Add(value)).Should().Throw<CosmosTranslationException>()
                    .WithMessage("A value holding a lone surrogate is refused by the SDK's UTF-8 encoder*");

            p.Add(1.5).Should().Be("@p0");
            p.Add(-0.0).Should().Be("@p1");
            p.Add("a😀").Should().Be("@p2");
            p.Count.Should().Be(3, "nothing refused was bound");
        }

    }

}
