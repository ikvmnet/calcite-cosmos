using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// A filter Calcite fails on is not answered with no rows because what it keeps contradicts the declaration: it is
    /// planned as any filter is, and where every document it is asked of raises it stays in Calcite, which fails as it
    /// would. A statement whose <c>WHERE</c> the declaration rules out is pushed as any other, and sends nothing only where
    /// nothing in that <c>WHERE</c> can raise.
    /// </summary>
    /// <remarks>
    /// The two cases the facts graph reduced when it read a raise as a row neither truth value keeps, so that a filter
    /// Calcite fails on every document the declaration admits was read as one that keeps none. The question is asked of
    /// the request, once, when the plan is implemented: every row false, and nothing raising.
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
        /// nowhere, and over a required string is pushed and sends nothing.
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

        /// <summary>
        /// A filter written over a filter whose cast of the text <c>body</c> to <c>INTEGER</c> may raise: Calcite evaluates
        /// the one beneath on every document and fails on a string that is no number, whatever the one above keeps. The one
        /// above is contradicted, but the cast stays in Calcite beneath it, so the request reads every document, a
        /// <c>COUNT</c> over them too, and Calcite fails as it does. Where the one beneath raises nowhere both are one
        /// <c>WHERE</c>, which is pushed and sends nothing.
        /// </summary>
        [Fact]
        public void AFilterOverAFilterThatMayRaiseIsNotReduced()
        {
            var container = Declaring(Listed);
            string Over(string selected, string beneath) => $"""SELECT {selected} FROM (SELECT c."id", c."DOC" FROM items AS c WHERE {beneath}) AS x WHERE JSON_VALUE(x."DOC", '$.kind') = 'C'""";

            foreach (var sql in new[] { Over("x.\"id\"", $"{AsInteger("body")} = 1"), Over("COUNT(*) AS n", $"{AsInteger("body")} = 1") })
            {
                var plan = PlanToCosmos(sql, container, out _);
                PlanText(plan).Should().NotContain(KeepsNothing, "the filter beneath raises where body is no number: " + PlanText(plan));
                PlanText(plan).Should().Contain("ClrCursorFilter(condition=[=(CAST(JSON_VALUE($0, '$.body')):INTEGER, 1)])", PlanText(plan));
                Query(FindCosmos(plan), container).Sql.Should().Be("SELECT VALUE c FROM items c", PlanText(plan));
            }

            var safe = PlanToCosmos(Over("x.\"id\"", $"{Read("body")} = 'q'"), container, out _);
            PlanText(safe).Should().Contain(KeepsNothing, "nothing beneath raises, and no document is of kind C: " + PlanText(safe));
        }

        /// <summary>
        /// Three filters stacked whose lower two contradict each other. Each plans, declared or not, as plain pushdown: the
        /// lower two are one <c>WHERE</c>, which no document makes true and nothing in which can raise, so the statement
        /// sends nothing, and the filter above, the cast of the text <c>body</c> among them, reads no row. Calcite's own
        /// simplifier reads the merged stack as <c>FALSE</c> and answers no rows too. Two empty relations once merged the
        /// sets of the filters they answered, and the stack overflowed; nothing in the plan stands for an empty answer now.
        /// </summary>
        [Fact]
        public void AStackWhoseLowerFiltersContradictPlans()
        {
            static string Stack(string bottom, string middle, string top) =>
                $"""SELECT y."id" FROM (SELECT x."id", x."DOC" FROM (SELECT c."id", c."DOC" FROM items AS c WHERE {bottom}) AS x WHERE {middle}) AS y WHERE {top}""";

            static string Kind(string alias) => $"""JSON_VALUE({alias}."DOC", '$.kind')""";

            foreach (var (declared, container) in new[] { ("declared", Declaring(Listed)), ("undeclared", TestReasoning.Bind(new CosmosContainerMetadata("items", new[] { "/ref" }))) })
                foreach (var (name, sql) in new[]
                {
                    ("t15", Stack($"{Kind("c")} = 'A'", $"{Kind("x")} = 'B'", """CAST(JSON_VALUE(y."DOC", '$.body') AS INTEGER) = 1""")),
                    ("plain", Stack($"{Kind("c")} = 'A'", $"{Kind("x")} = 'B'", """JSON_VALUE(y."DOC", '$.n') = '1'""")),
                    ("apart", Stack($"{Kind("c")} = 'C'", """x."id" <> 'zz'""", $"{Kind("y")} = 'D'")),
                })
                {
                    var plan = PlanToCosmos(sql, container, out _);
                    PlanText(plan).Should().Contain("CosmosToClrCursorConverter", $"{name} {declared}: pushed as any filter is: " + PlanText(plan));
                    PlanText(plan).Should().Contain(KeepsNothing, $"{name} {declared}: the WHERE sent keeps nothing and raises nowhere: " + PlanText(plan));
                }
        }

        /// <summary>
        /// A window is computed over every row beneath it before a filter above reads one, and no statement holds one, so a
        /// filter above a window is no clause of the statement beneath it and is never reduced, however the declaration
        /// contradicts it: with the rule every host adds, the window is computed in process over every document, its cast of
        /// the text <c>body</c> to <c>INTEGER</c> with it, which fails on a string that is no number as Calcite's does.
        /// Without that rule nothing plans a window, so each fails to plan, as it does with the filter satisfiable, rather
        /// than answering no rows.
        /// </summary>
        [Fact]
        public void AFilterAboveAWindowIsNeverReduced()
        {
            var container = Declaring(Listed);

            foreach (var window in new[] { $"ROW_NUMBER() OVER (ORDER BY {AsInteger("body")})", $"SUM({AsInteger("body")}) OVER ()" })
                foreach (var (kind, read) in new[] { ("C", ", x.r"), ("C", ""), ("A", ", x.r") })
                {
                    var sql = $"""SELECT x."id"{read} FROM (SELECT c."id", {Read("kind")} AS k, {window} AS r FROM items AS c) AS x WHERE x.k = '{kind}'""";
                    FluentActions.Invoking(() => PlanToCosmos(sql, container, out _)).Should().Throw<System.Exception>(sql)
                        .Which.GetType().Name.Should().Be("CannotPlanException", sql);

                    var hosted = PlanToCosmos(sql, container, out _, windows: true);
                    PlanText(hosted).Should().NotContain(KeepsNothing, "the window's cast raises where body is no number: " + PlanText(hosted));
                    PlanText(hosted).Should().Contain("Window").And.Contain("CAST(", "the window and its cast are Calcite's: " + PlanText(hosted));
                }
        }

    }

}
