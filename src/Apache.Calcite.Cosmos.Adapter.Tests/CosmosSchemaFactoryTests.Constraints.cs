using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    public partial class CosmosSchemaFactoryTests
    {

        /// <summary>
        /// The <c>constraints</c> object on a container entry: what reads, and what is refused rather than
        /// silently dropped.
        /// </summary>
        public class Constraints
        {

            static java.util.Map AsMap(string json) =>
                (java.util.Map)new com.fasterxml.jackson.databind.ObjectMapper().readValue(json, (java.lang.Class)typeof(java.util.Map));

            static CosmosContainerDeclaration Read(string constraints)
            {
                var entry = new java.util.HashMap();
                entry.put("name", "links");
                entry.put(CosmosSchemaFactory.ConstraintsOperand, AsMap(constraints));

                var list = new java.util.ArrayList();
                list.add(entry);

                var operand = new java.util.HashMap();
                operand.put(CosmosSchemaFactory.ContainersOperand, list);

                return CosmosSchemaFactory.ReadContainerDeclarations(operand)[0];
            }

            [Fact]
            public void AUniqueConstraintReads()
            {
                var declared = Read("""{ "unique": [ { "paths": ["/data/guid"] } ] }""");

                var unique = declared.Constraints.Should().ContainSingle().Which.Should().BeOfType<CosmosConstraint.Unique>().Subject;
                unique.Paths.Should().Equal(CosmosConstraint.PathOf("/data/guid"));
                unique.Filter.Should().BeEmpty();
                unique.Source.Should().Be(CosmosConstraintSource.Declared);
            }

            [Fact]
            public void AFilterReadsAsTheFactsItNames()
            {
                var declared = Read("""{ "unique": [ { "paths": ["/linkId"], "filter": { "/type": "Link", "/data/type": ["park", "map"] } } ] }""");

                var unique = (CosmosConstraint.Unique)declared.Constraints![0];
                unique.Filter.Should().HaveCount(2);
                unique.Filter.Should().Contain(new CosmosFact(CosmosConstraint.PathOf("/type")!, new CosmosClaim.EqualTo("Link")));
                unique.Filter.Should().Contain(new CosmosFact(CosmosConstraint.PathOf("/data/type")!, new CosmosClaim.OneOf(new object?[] { "park", "map" })));
            }

            [Fact]
            public void AnEntryWithNoConstraintsDeclaresNone()
            {
                var entry = new java.util.HashMap();
                entry.put("name", "links");

                var list = new java.util.ArrayList();
                list.add(entry);

                var operand = new java.util.HashMap();
                operand.put(CosmosSchemaFactory.ContainersOperand, list);

                CosmosSchemaFactory.ReadContainerDeclarations(operand)[0].Constraints.Should().BeNull();
            }

            /// <summary>
            /// A constraint this does not read is one the caller believes is in force, so it is refused
            /// rather than ignored the way an unknown schema keyword is.
            /// </summary>
            [Theory]
            [InlineData("""{ "check": [] }""")]
            [InlineData("""{ "unique": [ { "paths": ["/a"], "where": { "/type": "Link" } } ] }""")]
            [InlineData("""{ "unique": [ ["/a"] ] }""")]
            [InlineData("""{ "unique": [ { "paths": ["/tags/[]"] } ] }""")]
            [InlineData("""{ "unique": [ { "paths": ["/a"], "filter": { "/type": { "x": 1 } } } ] }""")]
            [InlineData("""{ "unique": [ { "paths": [] } ] }""")]
            public void WhatIsNotReadIsRefused(string constraints)
            {
                var read = () => Read(constraints);
                read.Should().Throw<ArgumentException>();
            }

        }

    }

}
