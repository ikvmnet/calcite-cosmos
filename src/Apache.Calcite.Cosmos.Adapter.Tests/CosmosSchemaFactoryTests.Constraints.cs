using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    public partial class CosmosSchemaFactoryTests
    {

        /// <summary>
        /// The <c>constraints</c> list on a container entry: what reads, and what is refused rather than
        /// silently dropped. Whether a constraint compiles against its container is asked once the container
        /// is read, and is tested in <c>CosmosConstraintSetTests</c>.
        /// </summary>
        public class Constraints
        {

            static CosmosContainerDeclaration Read(object constraints)
            {
                var entry = new java.util.HashMap();
                entry.put("name", "links");
                entry.put(CosmosSchemaFactory.ConstraintsOperand, constraints);

                var list = new java.util.ArrayList();
                list.add(entry);

                var operand = new java.util.HashMap();
                operand.put(CosmosSchemaFactory.ContainersOperand, list);

                return CosmosSchemaFactory.ReadContainerDeclarations(operand)[0];
            }

            static java.util.ArrayList List(params object[] items)
            {
                var list = new java.util.ArrayList();
                foreach (var item in items)
                    list.add(item);
                return list;
            }

            [Fact]
            public void EachStringIsAConstraint()
            {
                var declared = Read(List(
                    "UNIQUE (JSON_VALUE(DOC, '$.data.guid'))",
                    "UNIQUE (JSON_VALUE(DOC, '$.linkId')) WHERE JSON_VALUE(DOC, '$.type') = 'Link'"));

                declared.Constraints.Should().HaveCount(2);
                declared.Constraints![1].Text.Should().Be("UNIQUE (JSON_VALUE(DOC, '$.linkId')) WHERE JSON_VALUE(DOC, '$.type') = 'Link'");
                declared.Constraints[1].Source.Should().Be(CosmosConstraintSource.Declared);
            }

            [Fact]
            public void AnEntryWithNoConstraintsDeclaresNone()
            {
                var entry = new java.util.HashMap();
                entry.put("name", "links");

                var operand = new java.util.HashMap();
                operand.put(CosmosSchemaFactory.ContainersOperand, List(entry));

                CosmosSchemaFactory.ReadContainerDeclarations(operand)[0].Constraints.Should().BeNull();
            }

            [Fact]
            public void AnObjectIsNotAList()
            {
                var read = () => Read(new java.util.HashMap());
                read.Should().Throw<ArgumentException>();
            }

            [Fact]
            public void ANonStringIsRefused()
            {
                var read = () => Read(List(java.lang.Integer.valueOf(1)));
                read.Should().Throw<ArgumentException>();
            }

            [Fact]
            public void AKindNotReadYetIsRefusedByName()
            {
                var read = () => Read(List("CHECK (JSON_VALUE(DOC, '$.data.id') = JSON_VALUE(DOC, '$.linkId'))"));
                read.Should().Throw<ArgumentException>().WithMessage("*CHECK*not read yet*");
            }

        }

    }

}
