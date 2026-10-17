using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.Azure.Cosmos;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    public partial class CosmosContainerMetadataReaderTests
    {

        /// <summary>
        /// Whether a path is covered by the container's index. For a filter this bears on cost only —
        /// an unindexed path still queries, it just scans — and for an <c>ORDER BY</c> on legality:
        /// Azure refuses one over a path the policy excludes.
        /// </summary>
        public class IndexingPolicy
        {

            static CosmosContainerMetadata Policy(string[] included, string[] excluded) =>
                new("products", new[] { "/category" }, null, included, excluded);

            [Fact]
            public void DefaultPolicyIndexesEverything()
            {
                var container = new CosmosContainerMetadata("products");

                container.IsPathIndexed("/name").Should().BeTrue();
                container.IsPathIndexed("/inventory/quantity").Should().BeTrue();
            }

            /// <remarks>
            /// Everything but <c>_etag</c>, which the service excludes from every policy it hands back —
            /// <c>/"_etag"/?</c>, measured on the probe account — and an <c>ORDER BY</c> over which is a
            /// 400 there.
            /// </remarks>
            [Fact]
            public void TheDefaultPolicyExcludesTheEntityTag()
            {
                new CosmosContainerMetadata("products").IsPathIndexed("/_etag").Should().BeFalse();
                Policy(new[] { "/*" }, new[] { "/\"_etag\"/?" }).IsPathIndexed("/_etag").Should().BeFalse("a quoted segment is the segment");
            }

            /// <remarks>
            /// A segment the policy quotes is the segment unquoted: the service quotes <c>_etag</c> in the
            /// policy it returns, and may quote any name.
            /// </remarks>
            [Fact]
            public void AQuotedSegmentIsTheSegment()
            {
                var container = Policy(new[] { "/*" }, new[] { "/\"notes\"/?", "/\"blob\"/*" });

                container.IsPathIndexed("/notes").Should().BeFalse();
                container.IsPathIndexed("/blob/inner").Should().BeFalse();
                container.IsPathIndexed("/name").Should().BeTrue();
            }

            /// <remarks>
            /// A computed property is reached by no wildcard: measured on the probe account, one the policy
            /// does not name orders as a 400 and one it includes orders as its values do.
            /// </remarks>
            [Fact]
            public void AComputedPropertyIsIndexedOnlyWhereAnIncludedPathNamesIt()
            {
                var container = new CosmosContainerMetadata("products", new[] { "/category" }, null, new[] { "/*", "/cp_lower/?" }, new string[0], computedProperties: new[] { "cp_lower", "cp_neg" });

                container.IsPathIndexed("/cp_lower").Should().BeTrue();
                container.IsPathIndexed("/cp_neg").Should().BeFalse();
                container.IsPathIndexed("/name").Should().BeTrue();

                new CosmosContainerMetadata("products", computedProperties: new[] { "cp_lower" }).IsPathIndexed("/cp_lower").Should().BeFalse("the default policy names none");
            }

            [Fact]
            public void ComputedPropertiesAreReadFromTheContainerDefinition()
            {
                var properties = new ContainerProperties("products", "/pk");
                properties.ComputedProperties.Add(new ComputedProperty { Name = "cp_lower", Query = "SELECT VALUE LOWER(c.name) FROM c" });

                var container = CosmosContainerMetadataReader.FromProperties(properties);

                container.ComputedProperties.Should().Equal("cp_lower");
                container.IsPathIndexed("/cp_lower").Should().BeFalse();
            }

            [Fact]
            public void RootInclusionIndexesEverything()
            {
                var container = Policy(new[] { "/*" }, new string[0]);

                container.IsPathIndexed("/name").Should().BeTrue();
                container.IsPathIndexed("/deeply/nested/path").Should().BeTrue();
            }

            [Fact]
            public void ExplicitlyExcludedPathIsNotIndexed()
            {
                var container = Policy(new[] { "/*" }, new[] { "/notes/?" });

                container.IsPathIndexed("/notes").Should().BeFalse();
                container.IsPathIndexed("/name").Should().BeTrue();
            }

            [Fact]
            public void ExcludedSubtreeCoversPathsBeneathIt()
            {
                var container = Policy(new[] { "/*" }, new[] { "/blob/*" });

                container.IsPathIndexed("/blob").Should().BeFalse();
                container.IsPathIndexed("/blob/inner").Should().BeFalse();
                container.IsPathIndexed("/other").Should().BeTrue();
            }

            /// <remarks>
            /// The documented precedence: a deeper inclusion overrides a shallower exclusion.
            /// </remarks>
            [Fact]
            public void DeeperInclusionOverridesShallowerExclusion()
            {
                var container = Policy(new[] { "/model/manufacturer/*" }, new[] { "/model/*" });

                container.IsPathIndexed("/model/manufacturer").Should().BeTrue();
                container.IsPathIndexed("/model/manufacturer/name").Should().BeTrue();
                container.IsPathIndexed("/model/other").Should().BeFalse();
            }

            /// <remarks>
            /// At equal depth <c>/?</c> is more precise than <c>/*</c>.
            /// </remarks>
            [Fact]
            public void ExactPatternOutranksSubtreeAtTheSameDepth()
            {
                Policy(new[] { "/a/?" }, new[] { "/a/*" }).IsPathIndexed("/a").Should().BeTrue();
                Policy(new[] { "/a/*" }, new[] { "/a/?" }).IsPathIndexed("/a").Should().BeFalse();
            }

            [Fact]
            public void PathMatchingNoInclusionIsNotIndexed()
            {
                var container = Policy(new[] { "/name/?" }, new string[0]);

                container.IsPathIndexed("/name").Should().BeTrue();
                container.IsPathIndexed("/other").Should().BeFalse();
            }

            /// <remarks>
            /// The service always indexes these and does not permit excluding them.
            /// </remarks>
            [Fact]
            public void IdAndTimestampAreAlwaysIndexed()
            {
                var container = Policy(new string[0], new[] { "/*" });

                container.IsPathIndexed("/id").Should().BeTrue();
                container.IsPathIndexed("/_ts").Should().BeTrue();
                container.IsPathIndexed("/_etag").Should().BeFalse();
            }

            [Fact]
            public void PolicyPathsAreReadFromTheContainerDefinition()
            {
                var properties = new ContainerProperties("products", "/pk");
                properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
                properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/blob/*" });

                var container = CosmosContainerMetadataReader.FromProperties(properties);

                container.IncludedPaths.Should().Contain("/*");
                container.ExcludedPaths.Should().Contain("/blob/*");
                container.IsPathIndexed("/blob/inner").Should().BeFalse();
                container.IsPathIndexed("/name").Should().BeTrue();
            }

        }

    }

}
