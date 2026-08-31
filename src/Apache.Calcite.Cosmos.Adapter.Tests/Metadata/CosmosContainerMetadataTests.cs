using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    [TestClass]
    public class CosmosContainerMetadataTests
    {

        static CosmosCompositeIndex Index(params (string Path, bool Descending)[] paths)
        {
            var list = new List<CosmosCompositeIndexPath>();
            foreach (var (path, descending) in paths)
                list.Add(new CosmosCompositeIndexPath(path, descending));

            return new CosmosCompositeIndex(list);
        }

        static CosmosSortKey[] Sort(params (string Path, bool Descending)[] keys)
        {
            var list = new CosmosSortKey[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                list[i] = new CosmosSortKey(keys[i].Path, keys[i].Descending);

            return list;
        }

        static CosmosContainerMetadata WithIndex(params CosmosCompositeIndex[] indexes) =>
            new("products", null, indexes);

        // ── Legality, not cost ────────────────────────────────────────────────────

        [TestMethod]
        public void NoSortIsAlwaysLegal()
        {
            WithIndex().IsSortSupported(Sort()).Should().BeTrue();
        }

        /// <remarks>
        /// A single-property sort on an unindexed path costs more but still runs, so it is legal
        /// regardless of the indexing policy. Only multi-property sorts can be outright rejected.
        /// </remarks>
        [TestMethod]
        public void SingleKeySortIsLegalWithoutAnyCompositeIndex()
        {
            WithIndex().IsSortSupported(Sort(("/name", false))).Should().BeTrue();
        }

        [TestMethod]
        public void MultiKeySortWithoutACompositeIndexIsRejected()
        {
            WithIndex().IsSortSupported(Sort(("/name", false), ("/price", false))).Should().BeFalse();
        }

        [TestMethod]
        public void MultiKeySortWithAMatchingCompositeIndexIsAccepted()
        {
            var container = WithIndex(Index(("/name", false), ("/price", false)));
            container.IsSortSupported(Sort(("/name", false), ("/price", false))).Should().BeTrue();
        }

        [TestMethod]
        public void AnyOfSeveralIndexesMayMatch()
        {
            var container = WithIndex(
                Index(("/a", false), ("/b", false)),
                Index(("/name", false), ("/price", false)));

            container.IsSortSupported(Sort(("/name", false), ("/price", false))).Should().BeTrue();
        }

        // ── Path sequence ─────────────────────────────────────────────────────────

        [TestMethod]
        public void PathOrderMustMatch()
        {
            var container = WithIndex(Index(("/name", false), ("/price", false)));
            container.IsSortSupported(Sort(("/price", false), ("/name", false))).Should().BeFalse();
        }

        /// <remarks>
        /// A composite index on three paths does not serve an ORDER BY over the first two. This is
        /// an easy assumption to get wrong — prefixes do not qualify.
        /// </remarks>
        [TestMethod]
        public void PrefixOfALongerIndexDoesNotQualify()
        {
            var container = WithIndex(Index(("/name", false), ("/price", false), ("/_ts", false)));
            container.IsSortSupported(Sort(("/name", false), ("/price", false))).Should().BeFalse();
        }

        [TestMethod]
        public void SortLongerThanTheIndexDoesNotQualify()
        {
            var container = WithIndex(Index(("/name", false), ("/price", false)));
            container.IsSortSupported(Sort(("/name", false), ("/price", false), ("/_ts", false))).Should().BeFalse();
        }

        // ── Direction ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void FullyInvertedDirectionsQualify()
        {
            var container = WithIndex(Index(("/name", false), ("/price", false)));
            container.IsSortSupported(Sort(("/name", true), ("/price", true))).Should().BeTrue();
        }

        [TestMethod]
        public void PartiallyInvertedDirectionsDoNotQualify()
        {
            var container = WithIndex(Index(("/name", false), ("/price", false)));
            container.IsSortSupported(Sort(("/name", false), ("/price", true))).Should().BeFalse();
        }

        [TestMethod]
        public void MixedIndexDirectionsMatchExactly()
        {
            var container = WithIndex(Index(("/name", false), ("/price", true)));

            container.IsSortSupported(Sort(("/name", false), ("/price", true))).Should().BeTrue();
            container.IsSortSupported(Sort(("/name", true), ("/price", false))).Should().BeTrue();
            container.IsSortSupported(Sort(("/name", false), ("/price", false))).Should().BeFalse();
        }

        // ── Construction ──────────────────────────────────────────────────────────

        [TestMethod]
        public void CompositeIndexRequiresAtLeastTwoPaths()
        {
            var act = () => Index(("/name", false));
            act.Should().Throw<ArgumentException>();
        }

        [TestMethod]
        public void HierarchicalPartitionKeyIsLimitedToThreePaths()
        {
            var act = () => new CosmosContainerMetadata("products", new[] { "/a", "/b", "/c", "/d" });
            act.Should().Throw<ArgumentException>();
        }

        [TestMethod]
        public void PartitionKeyPathsArePreserved()
        {
            var container = new CosmosContainerMetadata("products", new[] { "/tenant", "/user" });
            container.PartitionKeyPaths.Should().Equal("/tenant", "/user");
        }

        // ── Bridge from CosmosPath to policy form ─────────────────────────────────

        [TestMethod]
        public void PolicyPathDropsTheAlias()
        {
            CosmosPath.Root("c").Property("inventory").Property("quantity").ToPolicyPath().Should().Be("/inventory/quantity");
        }

        [TestMethod]
        public void PolicyPathRendersSubscriptsAsTheArrayWildcard()
        {
            CosmosPath.Root("c").Property("distributors").Index(0).Property("name").ToPolicyPath().Should().Be("/distributors/[]/name");
        }

        [TestMethod]
        public void RootPolicyPathIsASlash()
        {
            CosmosPath.Root("c").ToPolicyPath().Should().Be("/");
        }

        [TestMethod]
        public void PolicyPathMatchesACompositeIndexEntry()
        {
            var container = WithIndex(Index(("/name", false), ("/inventory/quantity", false)));

            var keys = Sort(
                (CosmosPath.Root("c").Property("name").ToPolicyPath(), false),
                (CosmosPath.Root("c").Property("inventory").Property("quantity").ToPolicyPath(), false));

            container.IsSortSupported(keys).Should().BeTrue();
        }


        // ── Full text and vector declarations ─────────────────────────────────────

        /// <remarks>
        /// An exact comparison, unlike the included and excluded path patterns: a full text policy
        /// or index cannot name a wildcard, so a declared path is a literal one.
        /// </remarks>
        [TestMethod]
        public void ADeclaredFullTextPathIsSearchable()
        {
            var container = new CosmosContainerMetadata("products", fullTextPaths: new[] { "/name", "/inventory/notes" });

            container.IsPathFullTextSearchable("/name").Should().BeTrue();
            container.IsPathFullTextSearchable("/inventory/notes").Should().BeTrue();
            container.IsPathFullTextSearchable("/inventory").Should().BeFalse();
            container.IsPathFullTextSearchable("/description").Should().BeFalse();
        }

        /// <remarks>
        /// And a container declaring nothing declares nothing. This is the opposite default from
        /// <see cref="CosmosContainerMetadata.IsPathIndexed"/>, where an empty policy means the
        /// service's own, which indexes everything: there is no default full text policy.
        /// </remarks>
        [TestMethod]
        public void AContainerDeclaringNothingSearchesNothing()
        {
            var container = new CosmosContainerMetadata("products");

            container.IsPathFullTextSearchable("/name").Should().BeFalse();
            container.IsPathVectorSearchable("/embedding").Should().BeFalse();
        }

        [TestMethod]
        public void ADeclaredVectorPathIsSearchable()
        {
            var container = new CosmosContainerMetadata("products", vectorPaths: new[] { "/embedding" });

            container.IsPathVectorSearchable("/embedding").Should().BeTrue();
            container.IsPathVectorSearchable("/other").Should().BeFalse();
        }

        /// <remarks>
        /// The two lists are separate. Nothing about a text path says it holds a vector.
        /// </remarks>
        [TestMethod]
        public void TheDeclarationsDoNotCrossOver()
        {
            var container = new CosmosContainerMetadata("products", fullTextPaths: new[] { "/name" }, vectorPaths: new[] { "/embedding" });

            container.IsPathVectorSearchable("/name").Should().BeFalse();
            container.IsPathFullTextSearchable("/embedding").Should().BeFalse();
        }

        /// <remarks>
        /// The declarations survive everything attached after the definition is read — a row count,
        /// a statistics provider, a partition delete probe. They are read once and the planner asks
        /// for them at every one of those stages.
        /// </remarks>
        [TestMethod]
        public void DeclarationsSurviveTheAttachments()
        {
            var container = new CosmosContainerMetadata("products", fullTextPaths: new[] { "/name" }, vectorPaths: new[] { "/embedding" });

            container.WithStatistics(new CosmosContainerStatistics(10, 100, 1)).IsPathFullTextSearchable("/name").Should().BeTrue();
            container.WithStatisticsProvider(() => null).IsPathFullTextSearchable("/name").Should().BeTrue();
            container.WithPartitionKeyDeleteProbe(() => true).IsPathVectorSearchable("/embedding").Should().BeTrue();
        }

    }

}
