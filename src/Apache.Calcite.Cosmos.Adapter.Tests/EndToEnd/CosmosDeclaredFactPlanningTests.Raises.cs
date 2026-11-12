using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// A filter Calcite fails on is not answered with no rows because what it keeps contradicts the declaration: it is
    /// planned as any filter is, and where every document it is asked of raises it stays in Calcite, which fails as it
    /// would.
    /// </summary>
    /// <remarks>
    /// The two cases the facts graph reduced when it read a raise as a row neither truth value keeps, so that a filter
    /// Calcite fails on every document the declaration admits was read as one that keeps none. The reduce now asks every
    /// row FALSE and none raising, so neither is reduced.
    /// </remarks>
    public partial class CosmosDeclaredFactPlanningTests
    {

        const string Booleans = """SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS BOOLEAN) = TRUE""";

        /// <summary>
        /// The text of a path declared an integer cast to <c>BOOLEAN</c> raises on every number, so no row is kept and no
        /// row is answered: Calcite fails. Where <c>n</c> may be missing, a document without it answers UNKNOWN beside the
        /// numbers' raise, so the comparison is sent and the service finds nothing; where it is required every document
        /// raises, nothing is sent, and the comparison stays in Calcite.
        /// </summary>
        [Fact]
        public void ATextCastToBooleanOverADeclaredIntegerFailsAsCalciteDoes()
        {
            var optional = Declaring("""{ "type": "object", "properties": { "n": { "type": "integer" } } }""");
            var sent = PlanToCosmos(Booleans, optional, out _);
            PlanText(sent).Should().NotContain(KeepsNothing, "the cast fails on a stored integer: " + PlanText(sent));
            PlanText(sent).Should().NotContain("ClrCursorFilter", PlanText(sent));
            Query(FindCosmos(sent), optional).Sql.Should().EndWith("WHERE (c.n = @p0)", "a document may lack n, and the service finds nothing");

            var required = Declaring("""{ "type": "object", "required": ["n"], "properties": { "n": { "type": "integer" } } }""");
            var kept = PlanToCosmos(Booleans, required, out _);
            PlanText(kept).Should().NotContain(KeepsNothing, "the cast fails on every document: " + PlanText(kept));
            PlanText(kept).Should().Contain("ClrCursorFilter", "and the comparison is Calcite's, which fails as it does: " + PlanText(kept));
            Query(FindCosmos(kept), required).Sql.Should().Be("SELECT VALUE c FROM items c", "nothing of it is sent");
        }

        /// <summary>
        /// A presence test over the text cast to <c>VARCHAR</c> raises on every document (<c>Calcite.TypeTestsAreTheirBodies</c>),
        /// so its negation over a required string, which keeps only the documents without the path, keeps none and answers
        /// none: Calcite fails. It stays in Calcite, the path required or not. The same test of the text itself raises
        /// nowhere, and over a required string is the empty relation.
        /// </summary>
        [Fact]
        public void APresenceTestOverATextCastToVarcharFailsAsCalciteDoes()
        {
            const string Absent = """SELECT c."id" FROM items AS c WHERE NOT IS_DEFINED(CAST(JSON_VALUE(c."DOC", '$.k') AS VARCHAR))""";

            foreach (var schema in new[]
            {
                """{ "type": "object", "required": ["k"], "properties": { "k": { "type": "string" } } }""",
                """{ "type": "object", "properties": { "k": { "type": "string" } } }""",
            })
            {
                var container = Declaring(schema);
                var kept = PlanToCosmos(Absent, container, out _);
                PlanText(kept).Should().NotContain(KeepsNothing, "the test raises on every document: " + PlanText(kept));
                PlanText(kept).Should().Contain("ClrCursorFilter", "and is Calcite's, which fails as it does: " + PlanText(kept));
                Query(FindCosmos(kept), container).Sql.Should().Be("SELECT VALUE c FROM items c", "nothing of it is sent");
            }

            var required = Declaring("""{ "type": "object", "required": ["k"], "properties": { "k": { "type": "string" } } }""");
            var text = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE NOT IS_DEFINED(JSON_VALUE(c."DOC", '$.k'))""", required, out _);
            PlanText(text).Should().Contain(KeepsNothing, "the text's test raises nowhere, and every document holds k: " + PlanText(text));
        }

    }

}
