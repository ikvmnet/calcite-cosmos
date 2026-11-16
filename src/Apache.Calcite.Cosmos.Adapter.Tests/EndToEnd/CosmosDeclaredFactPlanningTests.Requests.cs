using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// Whether a statement is sent is asked once, of the request, when the plan is implemented: the plan is plain
    /// pushdown's, declared or not, and a statement whose <c>WHERE</c> keeps no document the declaration admits and fails
    /// on none is pushed as any other and not sent.
    /// </summary>
    /// <remarks>
    /// No emptiness or redundancy the declaration proves reaches a node Calcite reads: no empty relation stands in a
    /// filter's place, no <c>FALSE</c>, deletion or arm never read is in a node's condition. So Calcite plans a statement
    /// as it would knowing nothing, and its operators above a request that is left out run over no rows, as over the
    /// service's real, empty answer. The answer is the one sending the request gives, whatever plan Calcite picked.
    /// </remarks>
    public partial class CosmosDeclaredFactPlanningTests
    {

        /// <summary><c>kind</c> is <c>A</c> or <c>B</c>, <c>body</c> any string, <c>n</c> 1 or 2, and <c>tags</c> strings.</summary>
        const string Tagged = """{ "type": "object", "required": ["kind", "body", "n"], "properties": { "kind": { "enum": ["A", "B"] }, "body": { "type": "string" }, "n": { "enum": [1, 2] }, "tags": { "type": "array", "items": { "type": "string" } } } }""";

        static string Json(string alias, string path) => $"""JSON_VALUE({alias}."DOC", '$.{path}')""";

        static string JsonInteger(string alias, string path) => $"CAST({Json(alias, path)} AS INTEGER)";

        static string KindOf(string alias) => Json(alias, "kind");

        static string Lateral9(string t, string extra = "") =>
            $"{t} AS c CROSS JOIN LATERAL (SELECT {KindOf("c")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l{extra}";

        static string LeftKind(string t, string where) =>
            $"(SELECT p.\"id\", p.\"DOC\", p.k FROM (SELECT c.\"id\", c.\"DOC\", {Json("c", "kind")} AS k FROM {t} AS c) AS p WHERE {where})";

        static string LateralBody(string alias) =>
            $"CROSS JOIN LATERAL (SELECT {Json(alias, "body")} AS rb FROM (VALUES (1)) AS v(x0)) AS r";

        /// <summary>
        /// The shapes the reading of Calcite's plan answered with no rows and no request where Calcite fails, each over a
        /// cast of the text <c>body</c> to <c>INTEGER</c> Calcite evaluates somewhere: a run over a correlate, a lateral,
        /// an array traversal or a scalar sub-query, a run through a grouping's keys, and a select list's cast above a sort.
        /// </summary>
        static string Shape(string name, string t) => name switch
        {
                "pc1" => $"SELECT z.\"id\" FROM (SELECT y.\"id\", y.k, y.rb FROM (SELECT l.\"id\", l.k, r.rb FROM {LeftKind(t, "p.k IN ('A', 'B')")} AS l {LateralBody("l")}) AS y WHERE (CAST(y.rb AS INTEGER) = 1 AND y.k = 'C') OR (CAST(y.rb AS INTEGER) = 2 AND y.k = 'D')) AS z WHERE z.k <> 'E'",
                "pc2" => $"SELECT z.\"id\" FROM (SELECT y.\"id\", y.k, y.rb FROM (SELECT l.\"id\", l.k, r.rb FROM {LeftKind(t, "p.k = 'A'")} AS l {LateralBody("l")}) AS y WHERE (CAST(y.rb AS INTEGER) = 1 AND y.k = 'B') OR (CAST(y.rb AS INTEGER) = 2 AND y.k = 'C')) AS z WHERE z.k <> 'E'",
                "pc4" => $"SELECT z.\"id\" FROM (SELECT y.\"id\", y.k, y.rb FROM (SELECT l.\"id\", l.k, r.rb FROM {LeftKind(t, "p.k IN ('A', 'B')")} AS l {LateralBody("l")}) AS y WHERE y.k <> 'E') AS z WHERE (CAST(z.rb AS INTEGER) = 1 AND z.k = 'C') OR (CAST(z.rb AS INTEGER) = 2 AND z.k = 'D')",
                "pc5" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.k, x.rb FROM (SELECT p.\"id\", p.k, (SELECT {Json("p", "body")} FROM (VALUES (1)) AS v(x0)) AS rb FROM (SELECT c.\"id\", c.\"DOC\", {Json("c", "kind")} AS k FROM {t} AS c) AS p WHERE p.k = 'A') AS x WHERE (CAST(x.rb AS INTEGER) = 1 AND x.k = 'B') OR (CAST(x.rb AS INTEGER) = 2 AND x.k = 'C')) AS y WHERE y.k <> 'E'",
                "pr1" => $"SELECT y.\"id\", y.w FROM (SELECT x.\"id\", x.k, x.k2, x.w FROM (SELECT c.\"id\", {Json("c", "kind")} AS k, l.k2, l.w FROM {t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "kind")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l) AS x WHERE (x.k = 'A' AND x.k = 'B') OR (x.k2 = 'A' AND x.k2 = 'B')) AS y WHERE y.k <> 'Z'",
                "pr2" => $"SELECT y.\"id\", y.w FROM (SELECT x.\"id\", x.k, x.k2, x.w FROM (SELECT l.\"id\", l.k, l.w, r.k2 FROM (SELECT c.\"id\", c.\"DOC\", {Json("c", "kind")} AS k, {JsonInteger("c", "body")} AS w FROM {t} AS c) AS l CROSS JOIN LATERAL (SELECT {Json("l", "kind")} AS k2 FROM (VALUES (1)) AS v(x0)) AS r) AS x WHERE (x.k = 'A' AND x.k = 'B') OR (x.k2 = 'A' AND x.k2 = 'B')) AS y WHERE y.k <> 'Z'",
                "pr9" => $"SELECT y.\"id\", y.w FROM (SELECT x.\"id\", x.k, x.k2, x.w FROM (SELECT c.\"id\", {Json("c", "kind")} AS k, l.k2, l.w FROM {t} AS c LEFT JOIN LATERAL (SELECT {Json("c", "kind")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l ON TRUE) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "dlat1" => $"SELECT z.k2 FROM (SELECT y.k2 FROM (SELECT x.k2, x.b FROM (SELECT DISTINCT l.k2, {Json("c", "body")} AS b FROM {t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "kind")} AS k2 FROM (VALUES (1)) AS v(x0)) AS l) AS x WHERE CAST(x.b AS INTEGER) = 1) AS y WHERE y.k2 IN ('A', 'B')) AS z WHERE z.k2 = 'C'",
                "gb5" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.k2 FROM (SELECT c.\"id\", l.k2 FROM {t} AS c CROSS JOIN LATERAL (SELECT {KindOf("c")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "gb6" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.k2 FROM (SELECT c.\"id\", l.k2 FROM {t} AS c CROSS JOIN LATERAL (SELECT {KindOf("c")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l WHERE c.\"id\" <> 'zz') AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "gb7" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.\"DOC\" FROM (SELECT c.\"id\", c.\"DOC\", u.t FROM {t} AS c, UNNEST(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t) WHERE CAST(u.t AS INTEGER) > 0 OR {Json("c", "n")} = '1') AS x WHERE {KindOf("x")} IN ('A', 'B')) AS y WHERE {KindOf("y")} = 'C'",
                "gc3" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.k2 FROM (SELECT c.\"id\", l.k2 FROM {t} AS c LEFT JOIN LATERAL (SELECT {KindOf("c")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l ON TRUE) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "gc4" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.k2 FROM (SELECT c.\"id\", l.k2 FROM {t} AS c CROSS JOIN LATERAL (SELECT {KindOf("c")} AS k2, CAST(u.t AS INTEGER) AS w FROM UNNEST(JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t)) AS l) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "gc10" => $"SELECT y.\"id\", y.w FROM (SELECT x.\"id\", x.k2, x.w FROM (SELECT c.\"id\", l.k2, l.w FROM {Lateral9(t)}) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "gc11" => $"SELECT COUNT(*) AS q FROM (SELECT x.\"id\", x.k2 FROM (SELECT c.\"id\", l.k2 FROM {Lateral9(t)}) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "gc13" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.k2 FROM (SELECT c.\"id\", l.k2 FROM {Lateral9(t)}) AS x WHERE x.k2 = 'C') AS y WHERE y.k2 IN ('A', 'B')",
                "gc15" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.k2, x.\"DOC\" FROM (SELECT c.\"id\", c.\"DOC\", l.k2 FROM {Lateral9(t)}) AS x WHERE x.k2 = {Json("x", "n")}) AS y WHERE y.k2 <> {Json("y", "n")}",
                "gc20" => $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.\"DOC\" FROM (SELECT c.\"id\", c.\"DOC\", l.k2 FROM {Lateral9(t)} WHERE l.k2 IS NOT NULL OR c.\"id\" IS NOT NULL) AS x WHERE {KindOf("x")} IN ('A', 'B')) AS y WHERE {KindOf("y")} = 'C'",
                "gc21" => $"SELECT x.\"id\" FROM (SELECT c.\"id\", c.\"DOC\", l.k2 FROM {Lateral9(t)} WHERE l.k2 IS NOT NULL OR c.\"id\" IS NOT NULL) AS x WHERE {KindOf("x")} = 'C'",
                "lc1" => $"SELECT z.\"id\" FROM (SELECT y.\"id\", y.k2 FROM (SELECT x.\"id\", x.k2 FROM (SELECT c.\"id\", l.k2, l.w FROM {t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "kind")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l WHERE {Json("c", "n")} IN ('1', '2')) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C') AS z",
                "lc1b" => $"SELECT y.\"id\", y.w FROM (SELECT x.\"id\", x.k2, x.w FROM (SELECT c.\"id\", l.k2, l.w FROM {t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "kind")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "lc1w" => $"SELECT z.\"id\", z.w FROM (SELECT y.\"id\", y.k2, y.w FROM (SELECT x.\"id\", x.k2, x.w FROM (SELECT c.\"id\", l.k2, l.w FROM {t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "kind")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l WHERE {Json("c", "n")} IN ('1', '2')) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C') AS z",
                "lc2w" => $"SELECT z.\"id\", z.w FROM (SELECT y.\"id\", y.k2, y.w FROM (SELECT x.\"id\", x.k2, x.w FROM (SELECT d.\"id\", l.k2, d.w FROM (SELECT c.\"id\", c.\"DOC\", {JsonInteger("c", "body")} AS w FROM {t} AS c) AS d CROSS JOIN LATERAL (SELECT {Json("d", "kind")} AS k2 FROM (VALUES (1)) AS v(x0)) AS l WHERE {Json("d", "n")} IN ('1', '2')) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C') AS z",
                "un9" => $"SELECT z.\"id\", z.w FROM (SELECT y.\"id\", y.t, y.w FROM (SELECT x.\"id\", x.t, x.w FROM (SELECT d.\"id\", u.t AS t, d.w FROM (SELECT c.\"id\", c.\"DOC\", {JsonInteger("c", "body")} AS w FROM {t} AS c) AS d, UNNEST(JSON_QUERY(d.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t) WHERE {Json("d", "n")} IN ('1', '2')) AS x WHERE x.t IN ('a', 'b')) AS y WHERE y.t = 'c') AS z",
                "un9b" => $"SELECT y.\"id\", y.w FROM (SELECT x.\"id\", x.t, x.w FROM (SELECT d.\"id\", u.t AS t, d.w FROM (SELECT c.\"id\", c.\"DOC\", {JsonInteger("c", "body")} AS w FROM {t} AS c) AS d, UNNEST(JSON_QUERY(d.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t)) AS x WHERE x.t IN ('a', 'b')) AS y WHERE y.t = 'c'",
                "sq9" => $"SELECT z.\"id\", z.w FROM (SELECT y.\"id\", y.k2, y.w FROM (SELECT x.\"id\", x.k2, x.w FROM (SELECT d.\"id\", d.w, (SELECT {Json("d", "kind")} FROM (VALUES (1)) AS v(x0)) AS k2 FROM (SELECT c.\"id\", c.\"DOC\", {JsonInteger("c", "body")} AS w FROM {t} AS c) AS d) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C') AS z",
                "gk4" => $"SELECT y.k FROM (SELECT x.k FROM (SELECT DISTINCT {Json("c", "kind")} AS k FROM {t} AS c WHERE {JsonInteger("c", "body")} = 1) AS x WHERE x.k IS NOT NULL) AS y WHERE y.k IS NULL",
                "gk9" => $"SELECT y.k2 FROM (SELECT x.k2 FROM (SELECT DISTINCT l.k2 FROM {t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "kind")} AS k2, {JsonInteger("c", "body")} AS w FROM (VALUES (1)) AS v(x0)) AS l WHERE {Json("c", "n")} IN ('1', '2')) AS x WHERE x.k2 IN ('A', 'B')) AS y WHERE y.k2 = 'C'",
                "dk1" => $"SELECT y.k FROM (SELECT x.k FROM (SELECT DISTINCT {Json("c", "kind")} AS k, {JsonInteger("c", "body")} AS w FROM {t} AS c) AS x WHERE x.k IS NOT NULL) AS y WHERE y.k IS NULL",
                "dk4" => $"SELECT y.k FROM (SELECT x.k FROM (SELECT DISTINCT {Json("c", "kind")} AS k FROM {t} AS c WHERE {JsonInteger("c", "body")} = 1) AS x WHERE x.k IS NULL) AS y WHERE y.k IS NOT NULL",
                "dk6" => $"SELECT y.k FROM (SELECT x.k, x.n FROM (SELECT {Json("c", "kind")} AS k, COUNT(*) AS n FROM {t} AS c WHERE {JsonInteger("c", "body")} = 1 GROUP BY {Json("c", "kind")}) AS x WHERE x.k IS NOT NULL) AS y WHERE y.k IS NULL",
                "sf3w" => $"SELECT y.\"id\", y.w FROM (SELECT x.\"id\", x.k, x.w FROM (SELECT c.\"id\", {Json("c", "kind")} AS k, {JsonInteger("c", "body")} AS w FROM {t} AS c ORDER BY c.\"id\" FETCH FIRST 5 ROWS ONLY) AS x WHERE x.k IN ('A', 'B')) AS y WHERE y.k = 'C'",
            _ => throw new System.ArgumentOutOfRangeException(nameof(name), name, "No such shape."),
        };

        /// <summary>
        /// Each shape plans, declared and not, as plain pushdown: nothing in the plan stands for an empty answer, and the
        /// declared statement sends nothing only where the <c>WHERE</c> it sends keeps no document the declaration admits
        /// and nothing in it can raise, here a run over a lateral whose filter on the left side Calcite pushes and the
        /// declaration rules out, the cast Calcite evaluates above it reading no row. The rest send their request, the
        /// cast evaluated by Calcite where its plan puts it. A container that declares nothing sends every one but the run on a
        /// <c>DISTINCT</c>'s keys Calcite moves beneath it, whose null tests contradict each other whatever is declared:
        /// the cast the <c>DISTINCT</c> computes above reads no row, where Calcite keeps the run above it and fails, as on
        /// <c>main</c> (recorded, <c>docs/23</c>).
        /// </summary>
        [Theory]
        [InlineData("pc1", true, false)]
        [InlineData("pc2", true, false)]
        [InlineData("pc4", true, false)]
        [InlineData("pc5", true, false)]
        [InlineData("pr1", false, false)]
        [InlineData("pr2", false, false)]
        [InlineData("pr9", false, false)]
        [InlineData("dlat1", false, false)]
        [InlineData("gb5", false, false)]
        [InlineData("gb6", false, false)]
        [InlineData("gb7", false, false)]
        [InlineData("gc3", false, false)]
        [InlineData("gc4", false, false)]
        [InlineData("gc10", false, false)]
        [InlineData("gc11", false, false)]
        [InlineData("gc13", false, false)]
        [InlineData("gc15", false, false)]
        [InlineData("gc20", false, false)]
        [InlineData("gc21", false, false)]
        [InlineData("lc1", false, false)]
        [InlineData("lc1b", false, false)]
        [InlineData("lc1w", false, false)]
        [InlineData("lc2w", false, false)]
        [InlineData("un9", false, false)]
        [InlineData("un9b", false, false)]
        [InlineData("sq9", false, false)]
        [InlineData("gk4", false, false)]
        [InlineData("gk9", false, false)]
        [InlineData("dk1", true, true)]
        [InlineData("dk4", false, false)]
        [InlineData("dk6", false, false)]
        [InlineData("sf3w", false, false)]
        public void EachShapeIsPlainPushdown(string name, bool declaredSendsNothing, bool undeclaredSendsNothing)
        {
            foreach (var (declared, container) in new[] { ("declared", Declaring(Tagged)), ("undeclared", TestReasoning.Bind(new CosmosContainerMetadata("items", new[] { "/ref" }))) })
            {
                var plan = PlanToCosmos(Shape(name, "items"), container, out _, windows: true, subqueries: true);
                PlanText(plan).Should().Contain("CosmosToClrCursorConverter", $"{name} {declared}: pushed as any statement is: " + PlanText(plan));
                PlanText(plan).Should().NotContain("Values(tuples=[[]])", $"{name} {declared}: nothing stands for an empty answer: " + PlanText(plan));

                if (declared == "declared" ? declaredSendsNothing : undeclaredSendsNothing)
                    PlanText(plan).Should().Contain(KeepsNothing, $"{name} {declared}: the WHERE sent keeps nothing and raises nowhere: " + PlanText(plan));
                else
                    PlanText(plan).Should().NotContain(KeepsNothing, $"{name} {declared}: the request is sent: " + PlanText(plan));
            }
        }

        /// <summary>
        /// An aggregate with no grouping answers one row over no document, the count's 0, so its statement is sent even
        /// where its <c>WHERE</c> keeps nothing: the text sent is the constant, which the service answers with that row.
        /// </summary>
        [Fact]
        public void AScalarAggregateOverAContradictedWhereIsSent()
        {
            var container = Declaring(Statuses);
            var plan = PlanToCosmos($"""SELECT COUNT(*) AS n FROM items AS c WHERE {Read("status")} = 'deleted'""", container, out _);

            PlanText(plan).Should().Contain("CosmosAggregate", "the count is pushed: " + PlanText(plan));
            PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));

            var query = Query(FindCosmos(plan), container);
            query.SendsNothing.Should().BeFalse("the service answers it with one row");
            query.Sql.Should().EndWith("WHERE false", "the WHERE it sends is the constant");

            var grouped = PlanToCosmos($"""SELECT {Read("status")} AS s, COUNT(*) AS n FROM items AS c WHERE {Read("status")} = 'deleted' GROUP BY {Read("status")}""", container, out _);
            PlanText(grouped).Should().Contain(KeepsNothing, "a grouping answers no group over no document: " + PlanText(grouped));
        }

        /// <summary>
        /// A <c>SUM</c> with no grouping is <c>NULL</c> over no document and the service's 0, so it is pushed only as
        /// Calcite's reduction of it: a <c>$SUM0</c> beside a <c>COUNT(*)</c>, both sent, and the <c>CASE</c> above in
        /// Calcite, reading <c>NULL</c> where the count is 0; an <c>AVG</c> reduced to a sum and a count takes the same
        /// sum. Over a <c>WHERE</c> the declaration rules out the statement is sent, its <c>WHERE</c> the constant, and over
        /// one it does not, the same. Per group the sum is the service's (<c>CosmosDifferentialTests.ASumOverNoDocumentIsNull</c>
        /// answers each against Calcite).
        /// </summary>
        [Theory]
        [InlineData("""SUM(c."_ts")""")]
        [InlineData("""AVG(c."_ts")""")]
        [InlineData("""SUM(c."_ts") + 1""")]
        public void AScalarSumIsSentBesideItsCount(string aggregate)
        {
            var container = Declaring(Statuses);
            foreach (var (where, constant) in new[] { ($"{Read("status")} = 'deleted'", true), ("""c."id" = 'zz'""", false) })
            {
                var plan = PlanToCosmos($"""SELECT {aggregate} AS s FROM items AS c WHERE {where}""", container, out _);
                var text = PlanText(plan);

                text.Should().MatchRegex(@"CosmosAggregate\(group=\[\{\}\][^\n]*\$SUM0\(", "the sum is pushed as its reduction's: " + text);
                text.Should().MatchRegex(@"CosmosAggregate\(group=\[\{\}\][^\n]*COUNT\(\)", "beside a count: " + text);
                text.Should().MatchRegex(@"ClrCursor[^\n]*CASE\(=\(", "the CASE on the count is Calcite's: " + text);
                text.Should().NotContain(KeepsNothing, text);

                var query = Query(FindCosmos(plan), container);
                query.SendsNothing.Should().BeFalse();
                query.Sql.Should().Contain("SUM(c._ts)").And.Contain("COUNT(1)");
                if (constant)
                    query.Sql.Should().EndWith("WHERE false", "the WHERE it sends is the constant");
            }

            var grouped = PlanText(PlanToCosmos($"""SELECT {Read("status")} AS k, SUM(c."_ts") AS s FROM items AS c WHERE c."id" > 'a' GROUP BY {Read("status")}""", container, out _));
            grouped.Should().Contain("CosmosAggregate(group=[{0}], s=[SUM($1)])", "a group is never empty, so its sum is the service's: " + grouped);
            grouped.Should().NotContain("CASE(", grouped);
        }

        /// <summary>
        /// A clause above a statement reads what the rows it keeps hold from the filter's node assuming every conjunct, which
        /// a <c>WHERE</c> the declaration rules out leaves holding nothing: so a <c>MIN</c> of <c>kind</c>, a <c>MAX</c> of
        /// <c>id</c> and an <c>ORDER BY</c> of either over <c>kind = 'C'</c>, each pushed without a schema, are Calcite's
        /// with one, over a statement that sends nothing. The answer is the same, a <c>NULL</c> or no rows; the plan is one a
        /// schema changes (<c>TODO.md</c>, <em>A clause above a WHERE the declaration rules out reads nothing of its
        /// rows</em>). Over <c>kind = 'A'</c> each is pushed with a schema too.
        /// </summary>
        [Theory]
        [InlineData("SELECT MIN(JSON_VALUE(c.\"DOC\", '$.kind')) AS q FROM items AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') = '{0}'", "CosmosAggregate")]
        [InlineData("SELECT MAX(c.\"id\") AS q FROM items AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') = '{0}'", "CosmosAggregate")]
        [InlineData("SELECT JSON_VALUE(c.\"DOC\", '$.kind') AS k FROM items AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') = '{0}' ORDER BY JSON_VALUE(c.\"DOC\", '$.kind')", "CosmosSort")]
        [InlineData("SELECT c.\"id\" FROM items AS c WHERE JSON_VALUE(c.\"DOC\", '$.kind') = '{0}' ORDER BY c.\"id\"", "CosmosSort")]
        public void AClauseAboveAWhereTheDeclarationRulesOutIsCalcites(string sql, string clause)
        {
            var declared = PlanText(PlanToCosmos(string.Format(sql, "C"), Declaring(Tagged), out _));
            declared.Should().NotContain(clause, "the node holds nothing the clause is asked of: " + declared);
            declared.Should().Contain(KeepsNothing, declared);

            var undeclared = PlanText(PlanToCosmos(string.Format(sql, "C"), Declaring("""{ "type": "object" }"""), out _));
            undeclared.Should().Contain(clause, "without a schema the WHERE says kind is a string: " + undeclared);

            var admitted = PlanText(PlanToCosmos(string.Format(sql, "A"), Declaring(Tagged), out _));
            admitted.Should().Contain(clause, "a WHERE the declaration admits: " + admitted);
        }

        /// <summary>
        /// A null test of what Calcite types never null, over a cast that can fail, is no row Calcite's type check alone
        /// makes empty: the cast of the text <c>id</c> to <c>INTEGER</c> fails on an <c>id</c> that is no number, and an
        /// integer divided by zero fails on every document. Each stays in Calcite, the request reads every document, and
        /// Calcite fails as it does (<c>CosmosDifferentialTests.WhatCalciteFailsOnFailsPushedToo</c>). The reduce answered
        /// a condition Calcite read as never true with no rows and no request where nothing gated it; probed at
        /// <c>e6a1b85e</c>, neither reached it, each staying in Calcite unrendered.
        /// </summary>
        [Theory]
        [InlineData("""CAST(c."id" AS INTEGER) IS NULL""")]
        [InlineData("""(c."_ts" / 0 > 1) IS NULL""")]
        public void ATypeTautologyOverACastThatMayFailIsSent(string where)
        {
            var container = Declaring(Statuses);
            var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE {where}""", container, out _);

            PlanText(plan).Should().Contain("ClrCursorFilter(condition=[IS NULL(", "the condition is Calcite's: " + PlanText(plan));
            PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));
            Query(FindCosmos(plan), container).Sql.Should().Be("SELECT VALUE c FROM items c", "the request reads every document");
        }

        /// <summary>
        /// Where the part of a <c>WHERE</c> no document satisfies stays in Calcite, one request goes out, reading the
        /// documents Calcite reads, and Calcite answers what it computes over them: a <c>COALESCE</c> over the <c>id</c>,
        /// which never is null, tested for null, has no form the service evaluates.
        /// </summary>
        [Fact]
        public void AnImpossiblePartKeptInCalciteSendsOneRequest()
        {
            var container = Declaring(Statuses);
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE COALESCE(c."id", 'none') IS NULL""", container, out _);

            PlanText(plan).Should().Contain("ClrCursorFilter", "the test is Calcite's: " + PlanText(plan));
            PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));
            Query(FindCosmos(plan), container).Sql.Should().Be("SELECT VALUE c FROM items c");
        }

    }

}
