using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Which <c>UNIQUE</c> constraints a container states without being told, and how they are asked.
    /// </summary>
    public class CosmosConstraintSetTests
    {

        static CosmosDocumentPath P(string path) => CosmosConstraint.PathOf(path)!;

        static IEnumerable<string> PathsOf(CosmosConstraint constraint) =>
            ((CosmosConstraint.Unique)constraint).Paths.Select(p => p.ToString());

        [Fact]
        public void APartitionedContainerIsUniqueOnItsPartitionKeyWithId()
        {
            var derived = CosmosConstraintSet.FromContainer(new[] { "/linkId" }, Array.Empty<IReadOnlyList<string>>());

            derived.Should().ContainSingle();
            PathsOf(derived[0]).Should().BeEquivalentTo("$.linkId", "$.id");
            derived[0].Source.Should().Be(CosmosConstraintSource.Service);
        }

        [Fact]
        public void AHierarchicalKeyContributesEveryPath()
        {
            var derived = CosmosConstraintSet.FromContainer(new[] { "/tenant", "/user" }, Array.Empty<IReadOnlyList<string>>());

            PathsOf(derived[0]).Should().BeEquivalentTo("$.tenant", "$.user", "$.id");
        }

        [Fact]
        public void AContainerPartitionedOnIdIsUniqueOnId()
        {
            var derived = CosmosConstraintSet.FromContainer(new[] { "/id" }, Array.Empty<IReadOnlyList<string>>());

            PathsOf(derived[0]).Should().Equal("$.id");
        }

        [Fact]
        public void AContainerWithNoPartitionKeyIsUniqueOnId()
        {
            var derived = CosmosConstraintSet.FromContainer(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

            PathsOf(derived[0]).Should().Equal("$.id");
        }

        [Fact]
        public void AUniqueKeyPolicyIsUniqueWithThePartitionKey()
        {
            var derived = CosmosConstraintSet.FromContainer(new[] { "/linkId" }, new[] { new[] { "/data/guid" } });

            derived.Should().HaveCount(2);
            PathsOf(derived[1]).Should().BeEquivalentTo("$.linkId", "$.data.guid");
            derived[1].Source.Should().Be(CosmosConstraintSource.ContainerDefinition);
        }

        [Fact]
        public void TheReaderReadsTheUniqueKeyPolicy()
        {
            var properties = new ContainerProperties("links", "/linkId");
            properties.UniqueKeyPolicy.UniqueKeys.Add(new UniqueKey { Paths = { "/data/guid" } });

            var metadata = CosmosContainerMetadataReader.FromProperties(properties);

            metadata.Constraints.IsUnique(new[] { P("/linkId"), P("/data/guid") }, CosmosFactSet.Empty, CosmosFactSet.Empty).Should().BeTrue();
            metadata.Constraints.IsUnique(new[] { P("/data/guid") }, CosmosFactSet.Empty, CosmosFactSet.Empty).Should().BeFalse();
        }

        [Fact]
        public void ADeclaredConstraintIsAdded()
        {
            var metadata = new CosmosContainerMetadata("links", new[] { "/linkId" })
                .WithConstraints(new[] { CosmosConstraint.Unique.Of(new[] { "/data/guid" }, null, CosmosConstraintSource.Declared)! });

            metadata.Constraints.IsUnique(new[] { P("/data/guid") }, CosmosFactSet.Empty, CosmosFactSet.Empty).Should().BeTrue();
            metadata.Constraints.IsUnique(new[] { P("/linkId"), P("/id") }, CosmosFactSet.Empty, CosmosFactSet.Empty).Should().BeTrue("the service's constraint is still there");
        }

        /// <summary>
        /// A filtered constraint is used only where both sets are proved to satisfy it.
        /// </summary>
        [Fact]
        public void AFilterHasToBeProvedOfBothSides()
        {
            var isLink = new CosmosFact(P("/type"), new CosmosClaim.EqualTo("Link"));
            var set = new CosmosConstraintSet(new[] { CosmosConstraint.Unique.Of(new[] { "/linkId" }, new[] { isLink }, CosmosConstraintSource.Declared)! });

            var links = new CosmosFactTheory(Array.Empty<CosmosFactRule>()).Derive(new[] { isLink });
            var anything = CosmosFactSet.Empty;

            set.IsUnique(new[] { P("/linkId") }, links, links).Should().BeTrue();
            set.IsUnique(new[] { P("/linkId") }, links, anything).Should().BeFalse();
            set.IsUnique(new[] { P("/linkId") }, anything, links).Should().BeFalse();
        }

        [Fact]
        public void NothingEquatedIsNotUnique()
        {
            var set = new CosmosConstraintSet(CosmosConstraintSet.FromContainer(new[] { "/id" }, Array.Empty<IReadOnlyList<string>>()));

            set.IsUnique(Array.Empty<CosmosDocumentPath>(), CosmosFactSet.Empty, CosmosFactSet.Empty).Should().BeFalse();
        }

    }

}
