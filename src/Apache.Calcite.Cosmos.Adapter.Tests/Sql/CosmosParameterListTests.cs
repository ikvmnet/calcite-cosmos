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

    }

}
