using System;
using System.Text;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Covers the document a row describes, which is where the row model's two ways of naming the same
    /// thing have to be reconciled.
    /// </summary>
    /// <remarks>
    /// No service, and none needed: what a row means is decided here and the write that follows is a
    /// single SDK call. The rules being pinned are recorded in <c>DESIGN.md</c> under <em>What an insert
    /// writes</em>.
    /// </remarks>
    [TestClass]
    public class CosmosDocumentTests
    {

        static readonly string[] Columns = ["DOC", "id", "_ts", "_etag", "$.category"];

        static string Build(params object?[] values) => Encoding.UTF8.GetString(CosmosDocument.Build(Columns, values));

        /// <summary>
        /// The document column is the document, verbatim.
        /// </summary>
        [TestMethod]
        public void TheDocumentColumnIsTheDocument()
        {
            Build("""{"id":"1","name":"Trail Blazer","price":120}""", null, null, null, null)
                .Should().Be("""{"id":"1","name":"Trail Blazer","price":120}""");
        }

        /// <summary>
        /// Every other column is a projection of the document and contributes nothing, whatever it
        /// carries.
        /// </summary>
        /// <remarks>
        /// They are declared <c>STORED</c>, so a statement cannot name one and the values here are
        /// the old row's, which an update carries alongside the new document. Writing them would
        /// describe the same document twice.
        /// </remarks>
        [TestMethod]
        public void OnlyTheDocumentColumnDescribesTheDocument()
        {
            Build("""{"id":"1"}""", "2", java.lang.Long.valueOf(99), "etag", "bikes")
                .Should().Be("""{"id":"1"}""");
        }

        /// <summary>
        /// No document column, no document.
        /// </summary>
        [TestMethod]
        public void ARowWithoutTheDocumentColumnDescribesNothing()
        {
            Build(null, "1", null, null, "bikes").Should().Be("{}");
        }

        /// <summary>
        /// The service's own bookkeeping is stripped, wherever in the document it appears.
        /// </summary>
        /// <remarks>
        /// It appears whenever the document came from a scan, the document column being the document
        /// as the service returned it. Copying a row would otherwise write another item's identity.
        /// </remarks>
        [TestMethod]
        public void ServicePropertiesAreNotWritten()
        {
            Build("""{"id":"1","_ts":1700000000,"_etag":"abc","_rid":"r","_self":"s","_attachments":"a","name":"Trail Blazer"}""", null, null, null, null)
                .Should().Be("""{"id":"1","name":"Trail Blazer"}""");
        }

        /// <summary>
        /// A document keeps the digits it arrived with.
        /// </summary>
        /// <remarks>
        /// Properties are copied rather than read and rewritten, so a large integer does not lose
        /// precision to a double and an exponential is not reformatted.
        /// </remarks>
        [TestMethod]
        public void TheDocumentKeepsTheDigitsItWasGiven()
        {
            Build("""{"big":9007199254740993,"exp":1e30,"exact":0.1000000000000000055511151231257827}""", null, null, null, null)
                .Should().Be("""{"big":9007199254740993,"exp":1e30,"exact":0.1000000000000000055511151231257827}""");
        }

        /// <summary>
        /// And it keeps the order it arrived in.
        /// </summary>
        [TestMethod]
        public void PropertyOrderIsPreserved()
        {
            Build("""{"z":1,"a":2,"m":3}""", null, null, null, null)
                .Should().Be("""{"z":1,"a":2,"m":3}""");
        }

        [TestMethod]
        public void NestedShapesComeThroughWhole()
        {
            Build("""{"id":"1","inventory":{"sku":"S-1","count":3},"tags":["steel","road"]}""", null, null, null, null)
                .Should().Be("""{"id":"1","inventory":{"sku":"S-1","count":3},"tags":["steel","road"]}""");
        }

        [TestMethod]
        public void ADocumentColumnHoldingSomethingElseIsRefused()
        {
            var act = () => Build(java.lang.Long.valueOf(3), null, null, null, null);

            act.Should().Throw<CosmosExecutionException>().WithMessage("*rather than JSON text*");
        }

        [TestMethod]
        public void ADocumentThatIsNotAnObjectIsRefused()
        {
            var act = () => Build("[1,2,3]", null, null, null, null);

            act.Should().Throw<CosmosExecutionException>().WithMessage("*rather than an object*");
        }

        [TestMethod]
        public void ADocumentThatIsNotWellFormedIsRefused()
        {
            var act = () => Build("{\"id\":", null, null, null, null);

            act.Should().Throw<CosmosExecutionException>().WithMessage("*well-formed JSON*");
        }

        [TestMethod]
        public void ANestedPathIsRead()
        {
            using var document = JsonDocument.Parse("""{"id":"1","inventory":{"sku":"S-1","count":3}}""");

            CosmosDocument.Read(document.RootElement, "/inventory/sku").Should().Be("S-1");
            CosmosDocument.Read(document.RootElement, "/inventory/count").Should().Be(3d);
            CosmosDocument.Read(document.RootElement, "/inventory/missing").Should().BeNull();
        }

        /// <summary>
        /// Absence and null are told apart, because Cosmos tells them apart.
        /// </summary>
        /// <remarks>
        /// A document with no value at the partition key path lives in the "none" logical partition,
        /// which is a different place from the one a null key routes to. Reading both as <c>null</c>
        /// would put documents in the wrong partition rather than fail.
        /// </remarks>
    }

}
