using System;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    public class CosmosPathTests
    {

        [Fact]
        public void RootRendersAsAlias()
        {
            CosmosPath.Root("c").ToString().Should().Be("c");
            CosmosPath.Root("c").IsRoot.Should().BeTrue();
        }

        [Fact]
        public void NestedPropertiesUseDotNotation()
        {
            CosmosPath.Root("c").Property("address").Property("city").ToString().Should().Be("c.address.city");
        }

        [Fact]
        public void AwkwardPropertyNamesUseBracketNotation()
        {
            CosmosPath.Root("c").Property("odd name").ToString().Should().Be("c[\"odd name\"]");
        }

        [Fact]
        public void MixedNotationComposes()
        {
            CosmosPath.Root("p")
                .Property("metadata")
                .Property("odd name")
                .Property("sku")
                .ToString()
                .Should().Be("p.metadata[\"odd name\"].sku");
        }

        [Fact]
        public void ArrayIndexRenders()
        {
            CosmosPath.Root("p").Property("tags").Index(0).Property("key").ToString().Should().Be("p.tags[0].key");
        }

        /// <remarks>
        /// "value" is reserved, so it must be bracketed even mid-path. This matches the form the
        /// Cosmos documentation itself uses — <c>p.tags[0]["value"]</c>.
        /// </remarks>
        [Fact]
        public void ReservedPropertyIsBracketedMidPath()
        {
            CosmosPath.Root("p").Property("tags").Index(0).Property("value").ToString().Should().Be("p.tags[0][\"value\"]");
        }

        [Fact]
        public void SystemPropertiesRenderBare()
        {
            CosmosPath.Root("c").Property("_ts").ToString().Should().Be("c._ts");
        }

        [Fact]
        public void ExtendingDoesNotMutateTheOriginal()
        {
            var root = CosmosPath.Root("c");
            var extended = root.Property("a");

            root.ToString().Should().Be("c");
            extended.ToString().Should().Be("c.a");
            root.Segments.Should().BeEmpty();
        }

        [Fact]
        public void EqualPathsCompareEqual()
        {
            var a = CosmosPath.Root("c").Property("x").Index(2);
            var b = CosmosPath.Root("c").Property("x").Index(2);

            a.Should().Be(b);
            a.GetHashCode().Should().Be(b.GetHashCode());
        }

        [Fact]
        public void DifferentPathsCompareUnequal()
        {
            CosmosPath.Root("c").Property("x").Should().NotBe(CosmosPath.Root("c").Property("y"));
            CosmosPath.Root("c").Property("x").Should().NotBe(CosmosPath.Root("d").Property("x"));
            CosmosPath.Root("c").Property("x").Should().NotBe(CosmosPath.Root("c").Property("x").Index(0));
        }

        [Fact]
        public void PathsAreCaseSensitive()
        {
            CosmosPath.Root("c").Property("Name").Should().NotBe(CosmosPath.Root("c").Property("name"));
        }

        [Fact]
        public void EmptyAliasIsRejected()
        {
            var act = () => CosmosPath.Root("");
            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void NegativeIndexIsRejected()
        {
            var act = () => CosmosPath.Root("c").Index(-1);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

    }

}
