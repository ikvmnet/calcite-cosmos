using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// The constraint DDL, which constraints a container states without being told, and what compiles.
    /// </summary>
    public class CosmosConstraintSetTests
    {

        static IEnumerable<string> Texts(IEnumerable<CosmosConstraint> constraints) => constraints.Select(c => c.Text);

        [Fact]
        public void APartitionedContainerIsUniqueOnItsPartitionKeyWithId()
        {
            var derived = CosmosConstraintSet.FromContainer(new[] { "/linkId" }, Array.Empty<IReadOnlyList<string>>());

            Texts(derived).Should().Equal("UNIQUE (JSON_VALUE(DOC, '$.linkId'), JSON_VALUE(DOC, '$.id'))");
            derived[0].Source.Should().Be(CosmosConstraintSource.Service);
        }

        [Fact]
        public void AHierarchicalKeyContributesEveryPath()
        {
            var derived = CosmosConstraintSet.FromContainer(new[] { "/tenant", "/user/name" }, Array.Empty<IReadOnlyList<string>>());

            Texts(derived).Should().Equal("UNIQUE (JSON_VALUE(DOC, '$.tenant'), JSON_VALUE(DOC, '$.user.name'), JSON_VALUE(DOC, '$.id'))");
        }

        [Fact]
        public void AContainerPartitionedOnIdIsUniqueOnId()
        {
            Texts(CosmosConstraintSet.FromContainer(new[] { "/id" }, Array.Empty<IReadOnlyList<string>>()))
                .Should().Equal("UNIQUE (JSON_VALUE(DOC, '$.id'))");
        }

        [Fact]
        public void AContainerWithNoPartitionKeyIsUniqueOnId()
        {
            Texts(CosmosConstraintSet.FromContainer(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>()))
                .Should().Equal("UNIQUE (JSON_VALUE(DOC, '$.id'))");
        }

        [Fact]
        public void AUniqueKeyPolicyIsUniqueWithThePartitionKey()
        {
            var derived = CosmosConstraintSet.FromContainer(new[] { "/linkId" }, new[] { new[] { "/data/guid" } });

            Texts(derived).Should().Contain("UNIQUE (JSON_VALUE(DOC, '$.linkId'), JSON_VALUE(DOC, '$.data.guid'))");
            derived[1].Source.Should().Be(CosmosConstraintSource.ContainerDefinition);
        }

        [Fact]
        public void APathThatIsNotAnIdentifierIsBracketed()
        {
            CosmosConstraint.AccessorOf("/odd name/x").Should().Be("JSON_VALUE(DOC, '$[''odd name''].x')");
        }

        [Fact]
        public void TheReaderReadsTheUniqueKeyPolicy()
        {
            var properties = new ContainerProperties("links", "/linkId");
            properties.UniqueKeyPolicy.UniqueKeys.Add(new UniqueKey { Paths = { "/data/guid" } });

            Texts(CosmosContainerMetadataReader.FromProperties(properties).Constraints.Constraints)
                .Should().Contain("UNIQUE (JSON_VALUE(DOC, '$.linkId'), JSON_VALUE(DOC, '$.data.guid'))");
        }

        [Fact]
        public void ADeclaredConstraintIsAddedBesideTheDerivedOnes()
        {
            var metadata = new CosmosContainerMetadata("links", new[] { "/linkId" })
                .WithConstraints(new[] { CosmosConstraint.Parse("UNIQUE (JSON_VALUE(DOC, '$.data.guid'))", CosmosConstraintSource.Declared) });

            metadata.Constraints.Constraints.Should().HaveCount(2);
            metadata.Constraints.Constraints.Select(c => c.Source).Should().Contain(new[] { CosmosConstraintSource.Service, CosmosConstraintSource.Declared });
        }

        [Theory]
        [InlineData("UNIQUE (a)", "a", null)]
        [InlineData("  unique(a, b)  ", "a, b", null)]
        [InlineData("UNIQUE (JSON_VALUE(DOC, '$.x')) WHERE JSON_VALUE(DOC, '$.type') = 'L'", "JSON_VALUE(DOC, '$.x')", "JSON_VALUE(DOC, '$.type') = 'L'")]
        [InlineData("UNIQUE (JSON_VALUE(DOC, '$['')'']')) where x = 1", "JSON_VALUE(DOC, '$['')'']')", "x = 1")]
        [InlineData("UNIQUE (\"a)\")", "\"a)\"", null)]
        public void TheGrammarReadsTheKeysAndThePredicate(string text, string keys, string? filter)
        {
            var unique = (CosmosConstraint.Unique)CosmosConstraint.Parse(text, CosmosConstraintSource.Declared);

            unique.Keys.Should().Be(keys);
            unique.Filter.Should().Be(filter);
        }

        /// <summary>
        /// What is not read is refused by name, because a constraint that is not read is not in force.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("CHECK (x = y)")]
        [InlineData("PRIMARY KEY (x)")]
        [InlineData("UNIQUEX (x)")]
        [InlineData("UNIQUE x")]
        [InlineData("UNIQUE ()")]
        [InlineData("UNIQUE (x")]
        [InlineData("UNIQUE (x) AND y")]
        [InlineData("UNIQUE (x) WHERE")]
        public void WhatIsNotReadIsRefused(string text)
        {
            var parse = () => CosmosConstraint.Parse(text, CosmosConstraintSource.Declared);
            parse.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void ADeclaredConstraintCompilesAgainstTheContainer()
        {
            var metadata = new CosmosContainerMetadata("links", new[] { "/linkId" })
                .WithConstraints(new[]
                {
                    CosmosConstraint.Parse("UNIQUE (LOWER(JSON_VALUE(DOC, '$.email'))) WHERE JSON_VALUE(DOC, '$.type') IN ('User', 'Admin')", CosmosConstraintSource.Declared),
                    CosmosConstraint.Parse("UNIQUE (\"id\", \"$.linkId\")", CosmosConstraintSource.Declared),
                });

            var validate = () => CosmosConstraintCompiler.Validate(metadata);
            validate.Should().NotThrow();
        }

        [Theory]
        [InlineData("UNIQUE (JSON_VALUE(DOC, '$.x') +)")]
        [InlineData("UNIQUE (NOSUCHCOLUMN)")]
        [InlineData("UNIQUE (NO_SUCH_FUNCTION(DOC))")]
        [InlineData("UNIQUE (RAND())")]
        public void AConstraintThatDoesNotCompileIsRefused(string text)
        {
            var metadata = new CosmosContainerMetadata("links", new[] { "/linkId" })
                .WithConstraints(new[] { CosmosConstraint.Parse(text, CosmosConstraintSource.Declared) });

            var validate = () => CosmosConstraintCompiler.Validate(metadata);
            validate.Should().Throw<ArgumentException>();
        }

    }

}
