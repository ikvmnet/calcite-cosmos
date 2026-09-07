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

        static readonly string[] Columns = ["_MAP", "id", "_ts", "_etag", "category"];

        static string Build(params object?[] values) => Encoding.UTF8.GetString(CosmosDocument.Build(Columns, values));

        static java.util.Map Map(params object?[] pairs)
        {
            var map = new java.util.LinkedHashMap();

            for (var i = 0; i + 1 < pairs.Length; i += 2)
                map.put(pairs[i], pairs[i + 1]);

            return map;
        }

        [TestMethod]
        public void TheMapColumnIsTheDocument()
        {
            Build(Map("id", "1", "name", "Trail Blazer", "price", java.lang.Long.valueOf(120)), null, null, null, null)
                .Should().Be("""{"id":"1","name":"Trail Blazer","price":120}""");
        }

        [TestMethod]
        public void PromotedColumnsAloneDescribeADocument()
        {
            Build(null, "1", null, null, "books")
                .Should().Be("""{"id":"1","category":"books"}""");
        }

        /// <summary>
        /// A promoted column supplied alongside the map replaces that property, in place.
        /// </summary>
        /// <remarks>
        /// In place rather than appended, so that the document keeps the order it arrived in — which
        /// costs nothing and makes the result of a copy readable next to its source.
        /// </remarks>
        [TestMethod]
        public void APromotedColumnOverridesTheMapsProperty()
        {
            Build(Map("id", "1", "name", "Trail Blazer"), "2", null, null, "bikes")
                .Should().Be("""{"id":"2","name":"Trail Blazer","category":"bikes"}""");
        }

        /// <summary>
        /// The rule the whole design turns on: a null promoted column says nothing.
        /// </summary>
        /// <remarks>
        /// An unmentioned column arrives as SQL null — <c>CosmosDmlPlanningTests</c> shows the planner
        /// doing exactly that — so without this an insert of the map column alone would overwrite the
        /// document it was handed with a row of nulls.
        /// </remarks>
        [TestMethod]
        public void ANullPromotedColumnContributesNothing()
        {
            Build(Map("id", "1", "category", "bikes"), null, null, null, null)
                .Should().Be("""{"id":"1","category":"bikes"}""");
        }

        /// <summary>
        /// A null inside the map is a JSON null, which is the distinction promotion loses.
        /// </summary>
        [TestMethod]
        public void ANullInsideTheMapIsWritten()
        {
            Build(Map("id", "1", "discount", null), null, null, null, null)
                .Should().Be("""{"id":"1","discount":null}""");
        }

        /// <summary>
        /// The service's own properties are dropped wherever they come from.
        /// </summary>
        /// <remarks>
        /// They cannot be named in an insert — the validator refuses — but they arrive inside the map
        /// column whenever one document is copied to another, which is the obvious use of
        /// <c>INSERT … SELECT "_MAP" FROM …</c>. The service's bookkeeping is not part of what is being
        /// copied.
        /// </remarks>
        [TestMethod]
        public void ServicePropertiesAreNotWritten()
        {
            Build(Map("id", "1", "_ts", java.lang.Long.valueOf(1), "_etag", "e", "_rid", "r", "_self", "s", "_attachments", "a", "name", "x"), null, null, null, null)
                .Should().Be("""{"id":"1","name":"x"}""");
        }

        /// <summary>
        /// Both representations of every JSON type are written, and they must be.
        /// </summary>
        /// <remarks>
        /// A value read from a container is a Java box, because everything above the reader is compiled
        /// Java; a value compiled from a literal is a CLR primitive. An insert can carry either, and an
        /// <c>INSERT … SELECT</c> from one container into another carries both in the same row.
        /// </remarks>
        [TestMethod]
        public void JavaBoxesAndClrPrimitivesBothWrite()
        {
            Build(Map(
                "jlong", java.lang.Long.valueOf(1),
                "jint", java.lang.Integer.valueOf(2),
                "jdouble", java.lang.Double.valueOf(1.5),
                "jbool", java.lang.Boolean.TRUE,
                "jdecimal", new java.math.BigDecimal("1.250"),
                "clong", 3L,
                "cint", 4,
                "cdouble", 2.5d,
                "cbool", false,
                "cstring", "s"), null, null, null, null)
                .Should().Be("""{"jlong":1,"jint":2,"jdouble":1.5,"jbool":true,"jdecimal":1.250,"clong":3,"cint":4,"cdouble":2.5,"cbool":false,"cstring":"s"}""");
        }

        [TestMethod]
        public void NestedObjectsAndArraysAreWritten()
        {
            var tags = new java.util.ArrayList();
            tags.add("outdoor");
            tags.add(java.lang.Long.valueOf(7));

            Build(Map("id", "1", "metadata", Map("sku", "S-1"), "tags", tags), null, null, null, null)
                .Should().Be("""{"id":"1","metadata":{"sku":"S-1"},"tags":["outdoor",7]}""");
        }

        /// <summary>
        /// A value with no JSON counterpart is refused rather than rendered as text.
        /// </summary>
        [TestMethod]
        public void AValueWithNoJsonFormIsRefused()
        {
            var act = () => Build(Map("id", "1", "when", Guid.NewGuid()), null, null, null, null);

            act.Should().Throw<CosmosExecutionException>().WithMessage("*has no Cosmos JSON representation*");
        }

        [TestMethod]
        public void AMapColumnHoldingSomethingElseIsRefused()
        {
            var act = () => Build("not a map", null, null, null, null);

            act.Should().Throw<CosmosExecutionException>().WithMessage("*does not describe a document*");
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
        // ── The JSON column on the write path ──────────────────────────────────────────
        //
        // The same document as the map column, in the encoding the service sent and Calcite's SQL/JSON
        // functions address. It is last in the row type and the map is first, so what is being pinned
        // here is that position in the row does not decide the order the two are applied in.

        /// <summary>
        /// The row type as a container with promoted columns actually produces it: the map first, the
        /// promoted columns next, the JSON column last.
        /// </summary>
        static readonly string[] BothColumns = ["_MAP", "id", "_ts", "_etag", "category", "DOC"];

        static string BuildBoth(params object?[] values) => Encoding.UTF8.GetString(CosmosDocument.Build(BothColumns, values));

        [TestMethod]
        public void TheJsonColumnIsTheDocument()
        {
            BuildBoth(null, null, null, null, null, """{"id":"1","name":"Trail Blazer","price":120}""")
                .Should().Be("""{"id":"1","name":"Trail Blazer","price":120}""");
        }

        /// <summary>
        /// A promoted column overrides the JSON column's property in place, exactly as it overrides
        /// the map's.
        /// </summary>
        /// <remarks>
        /// The test that says the two document columns are applied before the promoted ones rather
        /// than in the order the row type lists them. The JSON column is <em>last</em>, so a single
        /// pass over the row would let it overwrite the promoted column instead.
        /// </remarks>
        [TestMethod]
        public void APromotedColumnOverridesTheJsonsProperty()
        {
            BuildBoth(null, "2", null, null, "bikes", """{"id":"1","name":"Trail Blazer"}""")
                .Should().Be("""{"id":"2","name":"Trail Blazer","category":"bikes"}""");
        }

        /// <summary>
        /// Neither document column is a property of the document it describes.
        /// </summary>
        /// <remarks>
        /// Worth its own test because the JSON column is not one of the service properties that get
        /// stripped by name — it is excluded by being a document column, and nothing else would
        /// have stopped a row from writing a property literally called <c>DOC</c> holding the
        /// document's own text.
        /// </remarks>
        [TestMethod]
        public void TheJsonColumnIsNotItselfAProperty()
        {
            BuildBoth(null, "1", null, null, null, """{"name":"Trail Blazer"}""")
                .Should().NotContain("DOC")
                .And.Be("""{"name":"Trail Blazer","id":"1"}""");
        }

        /// <summary>
        /// Both document columns together are refused rather than resolved by precedence.
        /// </summary>
        /// <remarks>
        /// They are two descriptions of one document. A rule picking a winner would silently discard
        /// whichever it did not pick, and the row that reaches here on an update never carries both:
        /// <c>CosmosSequences.ApplySets</c> withholds the one that is not being set.
        /// </remarks>
        [TestMethod]
        public void BothDocumentColumnsTogetherAreRefused()
        {
            var act = () => BuildBoth(Map("id", "1"), null, null, null, null, """{"id":"2"}""");

            act.Should().Throw<CosmosExecutionException>().WithMessage("*two descriptions of the same document*");
        }

        [TestMethod]
        public void AJsonColumnHoldingSomethingElseIsRefused()
        {
            var act = () => BuildBoth(null, null, null, null, null, java.lang.Long.valueOf(3));

            act.Should().Throw<CosmosExecutionException>().WithMessage("*rather than JSON text*");
        }

        [TestMethod]
        public void AJsonColumnThatIsNotAnObjectIsRefused()
        {
            var act = () => BuildBoth(null, null, null, null, null, "[1,2,3]");

            act.Should().Throw<CosmosExecutionException>().WithMessage("*rather than an object*");
        }

        [TestMethod]
        public void AJsonColumnThatIsNotWellFormedIsRefused()
        {
            var act = () => BuildBoth(null, null, null, null, null, "{\"id\":");

            act.Should().Throw<CosmosExecutionException>().WithMessage("*well-formed JSON*");
        }

        /// <summary>
        /// A number written through the JSON column keeps the digits it arrived with.
        /// </summary>
        /// <remarks>
        /// The reason the value is written from the <c>JsonElement</c> rather than converted to a CLR
        /// number first: a large integer does not survive a double, and an exponential does not
        /// survive being reformatted. Nothing about the document is reinterpreted on the way through.
        /// </remarks>
        [TestMethod]
        public void TheJsonColumnKeepsTheDigitsItWasGiven()
        {
            BuildBoth(null, null, null, null, null, """{"big":9007199254740993,"exp":1e30,"exact":0.1000000000000000055511151231257827}""")
                .Should().Be("""{"big":9007199254740993,"exp":1e30,"exact":0.1000000000000000055511151231257827}""");
        }

        /// <summary>
        /// The service's own bookkeeping is stripped out of the JSON column too.
        /// </summary>
        /// <remarks>
        /// Which matters more here than for the map: the JSON column is the document <em>as the
        /// service returned it</em>, so it carries <c>_ts</c>, <c>_etag</c> and <c>_rid</c> whenever
        /// it came from a scan. Copying a row would otherwise write another item's identity.
        /// </remarks>
        [TestMethod]
        public void ServicePropertiesInsideTheJsonAreNotWritten()
        {
            BuildBoth(null, null, null, null, null, """{"id":"1","_ts":1700000000,"_etag":"abc","_rid":"r","_self":"s","_attachments":"a","name":"Trail Blazer"}""")
                .Should().Be("""{"id":"1","name":"Trail Blazer"}""");
        }

        /// <summary>
        /// Nested objects and arrays come through the JSON column whole.
        /// </summary>
        [TestMethod]
        public void NestedShapesComeThroughTheJsonColumn()
        {
            BuildBoth(null, null, null, null, null, """{"id":"1","inventory":{"sku":"S-1","count":3},"tags":["steel","road"]}""")
                .Should().Be("""{"id":"1","inventory":{"sku":"S-1","count":3},"tags":["steel","road"]}""");
        }

        [TestMethod]
        public void AbsenceIsDistinguishedFromNull()
        {
            using var document = JsonDocument.Parse("""{"id":"1","category":null}""");

            CosmosDocument.Contains(document.RootElement, "/category").Should().BeTrue();
            CosmosDocument.Read(document.RootElement, "/category").Should().BeNull();

            CosmosDocument.Contains(document.RootElement, "/missing").Should().BeFalse();
        }

    }

}
