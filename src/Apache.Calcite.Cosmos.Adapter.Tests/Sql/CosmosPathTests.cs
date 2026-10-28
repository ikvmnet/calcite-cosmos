using System;
using System.Text;

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

        [Fact]
        public void AppendingNoSegmentsReturnsThePath()
        {
            var path = CosmosPath.Root("c").Property("a");
            path.Append(Array.Empty<CosmosPathSegment>()).Should().BeSameAs(path);
        }

        [Fact]
        public void AppendedSegmentsFollowThoseAlreadyThere()
        {
            CosmosPath.Root("c").Property("a")
                .Append(new[] { CosmosPathSegment.Index(0), CosmosPathSegment.Property("b") })
                .Should().Be(CosmosPath.Root("c").Property("a").Index(0).Property("b"));
        }

        [Fact]
        public void AJsonPathOfNoStepsIsItsBaseAndOfOneStepThatStep()
        {
            var root = CosmosPath.Root("c");

            CosmosRexTranslator.TryExtendByJsonPath(root, "$", out var none).Should().BeTrue();
            none.Should().BeSameAs(root);
            CosmosRexTranslator.TryExtendByJsonPath(root, "$.a", out var dotted).Should().BeTrue();
            dotted.Should().Be(root.Property("a"));
            CosmosRexTranslator.TryExtendByJsonPath(root, "$['a b']", out var quoted).Should().BeTrue();
            quoted.Should().Be(root.Property("a b"));
            CosmosRexTranslator.TryExtendByJsonPath(root, "$[\"a b\"]", out var doubled).Should().BeTrue();
            doubled.Should().Be(root.Property("a b"));
            CosmosRexTranslator.TryExtendByJsonPath(root, "$[2]", out var indexed).Should().BeTrue();
            indexed.Should().Be(root.Index(2));
        }

        [Fact]
        public void AJsonPathRefusedPartwayGivesNoPath()
        {
            CosmosRexTranslator.TryExtendByJsonPath(CosmosPath.Root("c"), "$.a['b'][0]..c", out var path).Should().BeFalse();
            path.Should().BeNull();
        }

        /// <remarks>
        /// The parser gathers a path's steps and appends them once, rather than copying the path at each; a
        /// path of a thousand steps in every spelling must still be the path its steps make appended one by one.
        /// </remarks>
        [Fact]
        public void AJsonPathIsItsStepsAppendedOneByOne()
        {
            var text = new StringBuilder("$");
            var expected = CosmosPath.Root("c");
            for (var i = 0; i < 1000; i++)
            {
                switch (i % 4)
                {
                    case 0:
                        text.Append(".a").Append(i);
                        expected = expected.Property("a" + i);
                        break;
                    case 1:
                        text.Append("['x ").Append(i).Append("']");
                        expected = expected.Property("x " + i);
                        break;
                    case 2:
                        text.Append("[\"y ").Append(i).Append("\"]");
                        expected = expected.Property("y " + i);
                        break;
                    default:
                        text.Append('[').Append(i).Append(']');
                        expected = expected.Index(i);
                        break;
                }
            }

            CosmosRexTranslator.TryExtendByJsonPath(CosmosPath.Root("c"), text.ToString(), out var path).Should().BeTrue();
            path!.Segments.Should().Equal(expected.Segments);
            path.Should().Be(expected);
        }

    }

}
