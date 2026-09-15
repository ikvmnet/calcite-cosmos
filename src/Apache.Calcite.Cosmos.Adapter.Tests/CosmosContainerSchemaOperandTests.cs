using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// Covers the <c>containers</c> operand now that an entry may carry a schema as well as a name.
    /// </summary>
    /// <remarks>
    /// The shape has to stay backwards compatible in both spellings a model delivers — a list of names
    /// and one comma-separated string — because a schema is something a caller adds for the container
    /// it matters for, not something every container grows.
    /// </remarks>
    [TestClass]
    public class CosmosContainerSchemaOperandTests
    {

        const string ParksSchema = """
        {
          "oneOf": [
            { "properties": { "type": { "const": "Park" } } },
            { "properties": { "type": { "const": "ParkMap" },
                              "data": { "properties": { "parkId": { "type": "string",
                                        "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } } } }
          ]
        }
        """;

        static java.util.HashMap Operand(object containers)
        {
            var operand = new java.util.HashMap();
            operand.put(CosmosSchemaFactory.ContainersOperand, containers);
            return operand;
        }

        static java.util.ArrayList List(params object[] entries)
        {
            var list = new java.util.ArrayList();
            foreach (var entry in entries)
                list.add(entry);

            return list;
        }

        /// <summary>
        /// Builds the nested map a model parser hands the factory for an inline schema object.
        /// </summary>
        static java.util.Map AsMap(string json) =>
            (java.util.Map)new com.fasterxml.jackson.databind.ObjectMapper().readValue(json, (java.lang.Class)typeof(java.util.Map));

        static java.util.HashMap Entry(string name, string? schema = null)
        {
            var entry = new java.util.HashMap();
            entry.put("name", name);
            if (schema is not null)
                entry.put(CosmosSchemaFactory.SchemaOperand, AsMap(schema));

            return entry;
        }

        [TestMethod]
        public void PlainNamesStillRead()
        {
            var declared = CosmosSchemaFactory.ReadContainerDeclarations(Operand(List("products", "orders")));

            declared.Should().HaveCount(2);
            declared[0].Name.Should().Be("products");
            declared[0].Facts.IsEmpty.Should().BeTrue("naming a container declares nothing about it");
            declared[1].Name.Should().Be("orders");
        }

        [TestMethod]
        public void OneCommaSeparatedStringStillReads()
        {
            var declared = CosmosSchemaFactory.ReadContainerDeclarations(Operand("products, orders"));

            declared.Should().HaveCount(2);
            declared[0].Name.Should().Be("products");
            declared[1].Name.Should().Be("orders");
        }

        [TestMethod]
        public void NamesAndDeclarationsMixInOneList()
        {
            var declared = CosmosSchemaFactory.ReadContainerDeclarations(Operand(List("products", Entry("parks", ParksSchema))));

            declared.Should().HaveCount(2);
            declared[0].Facts.IsEmpty.Should().BeTrue();
            declared[1].Name.Should().Be("parks");
            declared[1].Facts.IsEmpty.Should().BeFalse();
        }

        [TestMethod]
        public void ADeclaredSchemaCompilesToTheFactsAPushdownWouldAsk()
        {
            var declared = CosmosSchemaFactory.ReadContainerDeclarations(Operand(List(Entry("parks", ParksSchema))));
            var parkId = CosmosDocumentPath.Root.Property("data").Property("parkId");
            var type = CosmosDocumentPath.Root.Property("type");

            var facts = declared[0].Facts;

            facts.Derive(null).RepresentationOf(parkId).Should().BeNull();
            facts.Derive(new[] { new CosmosFact(type, new CosmosClaim.EqualTo("ParkMap")) }).RepresentationOf(parkId)
                .Should().Be(CosmosStoredForms.UuidCanonicalLower);
        }

        [TestMethod]
        public void AnEntryWithNoSchemaDeclaresNothing()
        {
            CosmosSchemaFactory.ReadContainerDeclarations(Operand(List(Entry("parks")))).Should()
                .ContainSingle().Which.Facts.IsEmpty.Should().BeTrue();
        }

        [TestMethod]
        public void AnEntryWithNoNameIsAModelError()
        {
            var entry = new java.util.HashMap();
            entry.put(CosmosSchemaFactory.SchemaOperand, AsMap(ParksSchema));

            var read = () => CosmosSchemaFactory.ReadContainerDeclarations(Operand(List(entry)));
            read.Should().Throw<ArgumentException>().WithMessage("*must carry a 'name'*");
        }

        [TestMethod]
        public void ASchemaThatIsNotAnObjectIsAModelError()
        {
            var entry = new java.util.HashMap();
            entry.put("name", "parks");
            entry.put(CosmosSchemaFactory.SchemaOperand, "./parks.schema.json");

            var read = () => CosmosSchemaFactory.ReadContainerDeclarations(Operand(List(entry)));
            read.Should().Throw<ArgumentException>().WithMessage("*must be a JSON Schema object*",
                "a string there is a path or a document and nothing has decided which, so it is a mistake rather than a guess");
        }

        [TestMethod]
        public void AnAbsentOperandDeclaresNoContainers()
        {
            CosmosSchemaFactory.ReadContainerDeclarations(new java.util.HashMap()).Should().BeEmpty(
                "which is what asks the database to discover them");
        }

        [TestMethod]
        public void ASchemaWithNothingRecognisableIsNotAnError()
        {
            var declared = CosmosSchemaFactory.ReadContainerDeclarations(
                Operand(List(Entry("parks", """{ "properties": { "a": { "minimum": 3 } } }"""))));

            declared[0].Facts.IsEmpty.Should().BeTrue(
                "an unreadable schema loses pushdowns; it must never be a reason a container stops working");
        }

        [TestMethod]
        public void TheCompiledFactsReachTheContainerMetadata()
        {
            var declared = CosmosSchemaFactory.ReadContainerDeclarations(Operand(List(Entry("parks", ParksSchema))));
            var metadata = new CosmosContainerMetadata("parks", new[] { "/data/parkId" }).WithDeclaredFacts(declared[0].Facts);

            metadata.DeclaredFacts.IsEmpty.Should().BeFalse();
            metadata.PartitionKeyPaths.Should().ContainSingle("attaching facts keeps everything the definition said");

            new CosmosContainerMetadata("parks").DeclaredFacts.IsEmpty.Should().BeTrue(
                "a container that declares nothing carries an empty theory rather than a null one");
        }

    }

}
