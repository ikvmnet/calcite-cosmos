using Apache.Calcite.Cosmos.Adapter.Metadata;

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

        /// <summary>
        /// A filter written over a filter whose cast of the text <c>body</c> to <c>INTEGER</c> may raise: Calcite evaluates
        /// the one beneath on every document and fails on a string that is no number, whatever the one above keeps. The one
        /// above is contradicted, but its reduce is asked of every filter beneath it too, so neither is reduced, a
        /// <c>COUNT</c> over them neither, and the cast stays in Calcite. Where the one beneath raises nowhere the one above
        /// is still the empty relation.
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
        /// A stack of filters whose conjunction Calcite's own simplifier reads as <c>FALSE</c>: Calcite merges a filter
        /// with the one beneath it, the one beneath first, and simplifies the conjunction before evaluating any of it, so
        /// the cast of the text <c>body</c> beneath is never evaluated and Calcite answers no rows. The reduce reads the
        /// filters beneath as Calcite evaluates them, so each is the empty relation with no request, and a <c>COUNT</c>
        /// over it is the host's aggregate over none. Beside them a filter over the cast that Calcite still evaluates is
        /// not reduced, and fails as Calcite fails.
        /// </summary>
        [Fact]
        public void AStackCalciteSimplifiesToFalseIsTheEmptyRelation()
        {
            var container = Declaring(Listed);
            static string Kind(string alias) => $"""JSON_VALUE({alias}."DOC", '$.kind')""";
            string Stack(string selected, string middle, string top) =>
                $"""SELECT {selected} FROM (SELECT x."id", x."DOC" FROM (SELECT c."id", c."DOC" FROM items AS c WHERE {AsInteger("body")} = 1) AS x WHERE {Kind("x")} {middle}) AS y WHERE {Kind("y")} {top}""";

            foreach (var (name, sql) in new[]
            {
                ("dm7", Stack("y.\"id\"", "IN ('A', 'B')", "= 'C'")),
                ("dm7i", Stack("COUNT(*) AS n", "IN ('A', 'B')", "= 'C'")),
                ("dm7m", Stack("y.\"id\"", "IS NOT NULL", "IS NULL")),
                ("dm7p", Stack("y.\"id\"", "<> 'C'", "= 'C'")),
            })
            {
                var plan = PlanToCosmos(sql, container, out _);
                PlanText(plan).Should().Contain(KeepsNothing, name + ": Calcite simplifies the stack to FALSE before evaluating the cast: " + PlanText(plan));
                PlanText(plan).Should().NotContain("CosmosToClrCursorConverter", name + ": and nothing is sent: " + PlanText(plan));
            }

            var counted = PlanToCosmos(Stack("COUNT(*) AS n", "IN ('A', 'B')", "= 'C'"), container, out _);
            PlanText(counted).Should().Contain("ClrCursorAggregate(group=[{}], n=[COUNT()])", "the host counts none: " + PlanText(counted));

            var ff = PlanToCosmos($"""SELECT x."id" FROM (SELECT c."id", c."DOC" FROM items AS c WHERE {AsInteger("body")} = 1) AS x WHERE {Kind("x")} = 'C'""", container, out _);
            PlanText(ff).Should().NotContain(KeepsNothing, "Calcite evaluates the cast beneath on every document: " + PlanText(ff));
            PlanText(ff).Should().Contain("ClrCursorFilter(condition=[=(CAST(JSON_VALUE($0, '$.body')):INTEGER, 1)])", PlanText(ff));
            Query(FindCosmos(ff), container).Sql.Should().Be("SELECT VALUE c FROM items c", PlanText(ff));
        }

        /// <summary>
        /// Three filters stacked whose lower two contradict each other, so that the filter above each is reduced as well
        /// as the one it stands on. The two empty relations were once one, which merged the sets of the filters they
        /// answered: the merged set held a filter over itself, beneath which the planner's transposes and merges built new
        /// filters until the stack overflowed. Each now plans, declared or not, to the empty relation with no request,
        /// Calcite's answer, since its simplifier reads the merged stack as <c>FALSE</c> before evaluating the cast above.
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
                    PlanText(plan).Should().Contain(KeepsNothing, $"{name} {declared}: Calcite simplifies the stack to FALSE: " + PlanText(plan));
                    PlanText(plan).Should().NotContain("CosmosToClrCursorConverter", $"{name} {declared}: and nothing is sent: " + PlanText(plan));
                }
        }

        /// <summary>
        /// A stack of filters on a grouping's keys over the cast of the text <c>body</c> to <c>INTEGER</c>: Calcite moves
        /// each beneath the grouping (<c>FILTER_AGGREGATE_TRANSPOSE</c>) and merges it with the filter there, so where the
        /// conjunction is <c>FALSE</c> to its simplifier nothing of it is evaluated and Calcite answers no rows. The reduce
        /// reads the run through the grouping's keys, a <c>DISTINCT</c>'s or a <c>GROUP BY</c>'s, so each is the empty
        /// relation with no request. A filter on the keys over the cast alone, which Calcite merges with it and evaluates,
        /// is not reduced, and fails as Calcite fails.
        /// </summary>
        [Fact]
        public void AStackOnAGroupingsKeysIsReadThroughIt()
        {
            var container = Declaring(Listed);
            var distinct = $"""SELECT DISTINCT c."id", {Read("kind")} AS k FROM items AS c WHERE {AsInteger("body")} = 1""";
            var grouped = $"""SELECT {Read("kind")} AS k, COUNT(*) AS n FROM items AS c WHERE {AsInteger("body")} = 1 GROUP BY {Read("kind")}""";
            var keyed = $"""SELECT DISTINCT c."id", {Read("kind")} AS k, {Read("body")} AS b FROM items AS c WHERE {Read("kind")} IN ('A', 'B')""";

            foreach (var (name, sql) in new[]
            {
                ("dd1", $"""SELECT y."id" FROM (SELECT x."id", x.k FROM ({distinct}) AS x WHERE x.k IN ('A', 'B')) AS y WHERE y.k = 'C'"""),
                ("dd7", $"""SELECT y."id" FROM (SELECT x."id", x.k FROM ({distinct}) AS x WHERE x.k <> 'C') AS y WHERE y.k = 'C'"""),
                ("dg1", $"""SELECT y.k FROM (SELECT x.k, x.n FROM ({grouped}) AS x WHERE x.k IN ('A', 'B')) AS y WHERE y.k = 'C'"""),
                ("ps2", $"""SELECT y."id" FROM (SELECT x."id", x.k, x.b FROM ({keyed}) AS x WHERE CAST(x.b AS INTEGER) = 1) AS y WHERE y.k = 'C'"""),
            })
            {
                var plan = PlanToCosmos(sql, container, out _);
                PlanText(plan).Should().Contain(KeepsNothing, name + ": Calcite merges the stack beneath the grouping and simplifies it to FALSE: " + PlanText(plan));
                PlanText(plan).Should().NotContain("CosmosToClrCursorConverter", name + ": and nothing is sent: " + PlanText(plan));
            }

            var alone = PlanToCosmos($"""SELECT x."id" FROM ({distinct}) AS x WHERE x.k = 'C'""", container, out _);
            PlanText(alone).Should().NotContain(KeepsNothing, "Calcite evaluates the cast beneath the grouping on every document: " + PlanText(alone));
            PlanText(alone).Should().Contain("ClrCursorFilter(condition=[=(CAST(", "and the cast is Calcite's: " + PlanText(alone));
        }

        /// <summary>
        /// A stack of filters over a correlate (a lateral, an array traversal, a scalar sub-query), the lowest casting the
        /// text <c>body</c> to <c>INTEGER</c>: Calcite pushes each conjunct that reads one side alone into that side
        /// (<c>FILTER_CORRELATE</c>), where it is evaluated on every document before anything above, so a conjunction above
        /// that its simplifier reads as <c>FALSE</c> does not stop the cast, and Calcite fails on a string that is no number.
        /// The reduce reads the run as Calcite splits it, so none is reduced, declared or not, and the cast stays in Calcite.
        /// The same lateral with a comparison that cannot fail in place of the cast is still the empty relation.
        /// </summary>
        [Fact]
        public void ARunOverACorrelateIsReadAsCalciteSplitsIt()
        {
            static string Lateral(string beneath) =>
                $"""SELECT z."id" FROM (SELECT y."id", y.k2 FROM (SELECT x."id", x.k2 FROM (SELECT c."id", l.k2 FROM items AS c CROSS JOIN LATERAL (SELECT {Read("kind")} AS k2 FROM (VALUES (1)) AS v(x0)) AS l WHERE {beneath}) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C') AS z""";

            var shapes = new[]
            {
                ("latrun", Lateral($"{AsInteger("body")} = 1")),
                ("unrun", $"""SELECT z."id" FROM (SELECT y."id", y.t FROM (SELECT x."id", x.t FROM (SELECT c."id", u.t AS t FROM items AS c, UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t) WHERE {AsInteger("body")} = 1) AS x WHERE x.t IN ('a', 'b')) AS y WHERE y.t = 'c') AS z"""),
                ("sqrun3", $"""SELECT x."id" FROM (SELECT w."id", w.k2 FROM (SELECT c."id", c."DOC", (SELECT {Read("kind")} FROM (VALUES (1)) AS v(x0)) AS k2 FROM items AS c) AS w WHERE CAST(JSON_VALUE(w."DOC", '$.body') AS INTEGER) = 1 AND w.k2 = 'A') AS x WHERE x.k2 = 'B'"""),
            };

            foreach (var (declared, container) in new[] { ("declared", Declaring(Listed)), ("undeclared", TestReasoning.Bind(new CosmosContainerMetadata("items", new[] { "/ref" }))) })
                foreach (var (name, sql) in shapes)
                {
                    var plan = PlanToCosmos(sql, container, out _, subqueries: true);
                    PlanText(plan).Should().NotContain(KeepsNothing, $"{name} {declared}: Calcite casts beneath the correlate before anything above: " + PlanText(plan));
                    PlanText(plan).Should().Contain("CAST(JSON_VALUE(", $"{name} {declared}: and the cast is Calcite's: " + PlanText(plan));
                }

            var safe = PlanToCosmos(Lateral($"{Read("body")} = 'q'"), Declaring(Listed), out _, subqueries: true);
            PlanText(safe).Should().Contain(KeepsNothing, "nothing beneath can fail, and Calcite's simplifier reads what it pushes into the lateral as FALSE: " + PlanText(safe));
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
