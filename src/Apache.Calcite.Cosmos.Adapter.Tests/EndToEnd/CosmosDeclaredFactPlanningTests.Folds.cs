using FluentAssertions;
using Xunit;

using org.apache.calcite.rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// A <c>FALSE</c> the rewriter writes, for an arm of a <c>CASE</c> never read or for a projected condition, stops no
    /// evaluation that may raise: the arm's gate reads the <c>WHERE</c> as written, a conjunct the deletions drop among
    /// it, and a projected condition's every predicate of its expression.
    /// </summary>
    public partial class CosmosDeclaredFactPlanningTests
    {

        /// <summary><c>kind</c> is never <c>C</c>, and <c>n</c> is an integer from 0 to 10.</summary>
        const string Ranged = """{ "type": "object", "required": ["kind", "body", "n"], "properties": { "kind": { "enum": ["A", "B"] }, "body": { "type": "string" }, "n": { "type": "integer", "minimum": 0, "maximum": 10 } } }""";

        /// <summary><c>kind</c> is never <c>C</c>, and <c>n</c> is 1 or 2, whose text every integer cast parses.</summary>
        const string Listed = """{ "type": "object", "required": ["kind", "body", "n"], "properties": { "kind": { "enum": ["A", "B"] }, "body": { "type": "string" }, "n": { "enum": [1, 2] } } }""";

        static string Read(string path) => $"""JSON_VALUE(c."DOC", '$.{path}')""";

        static string AsInteger(string path) => $"CAST({Read(path)} AS INTEGER)";

        static string Returning(string path, string type) => $"""JSON_VALUE(c."DOC", '$.{path}' RETURNING {type})""";

        /// <summary>An arm read only where <c>kind</c> is <c>C</c>, which no document the declarations admit is.</summary>
        static readonly string NeverReadArm = $"CASE WHEN {Read("kind")} = 'C' THEN {AsInteger("n")} = 1 ELSE FALSE END";

        /// <summary>
        /// A conjunct the declaration makes true wherever it does not raise is deleted as redundant, and the arm beside it
        /// is never read; but the deleted conjunct raises on every admitted document (<c>RETURNING DOUBLE</c> over an
        /// integer, the text <c>body</c> cast to <c>INTEGER</c>), so Calcite fails. The arm is asked of the <c>WHERE</c> as
        /// written, so it is not written <c>FALSE</c>, and the filter, a <c>COUNT</c> over it too, is sent and answers
        /// nothing after a request, where it was the empty relation with none. Where nothing in the <c>WHERE</c> raises the
        /// arm still folds.
        /// </summary>
        [Fact]
        public void AnArmBesideADeletedConjunctThatMayRaiseIsNotWrittenFalse()
        {
            foreach (var (schema, deleted) in new[]
            {
                (Ranged, $"{Returning("n", "DOUBLE")} = 1.5"),
                (Listed, $"{AsInteger("body")} = 1"),
            })
            {
                var container = Declaring(schema);
                var where = $"NOT ({deleted} AND {Read("kind")} = 'C') AND {NeverReadArm}";

                var rows = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE {where}""", container, out _);
                PlanText(rows).Should().NotContain(KeepsNothing, "the deleted conjunct raises on every document: " + PlanText(rows));
                Query(FindCosmos(rows), container).Sql.Should().EndWith("WHERE ((c.kind = @p0) AND (c.n = @p1))", PlanText(rows));

                var counted = PlanToCosmos($"SELECT COUNT(*) AS n FROM items AS c WHERE {where}", container, out _);
                PlanText(counted).Should().NotContain(KeepsNothing, PlanText(counted));
                Query(FindCosmos(counted), container).Sql.Should().Be("""SELECT COUNT(1) AS "n" FROM items c WHERE ((c.kind = @p0) AND (c.n = @p1))""", PlanText(counted));
            }

            var safe = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE NOT ({Read("body")} = 'q' AND {Read("kind")} = 'C') AND {NeverReadArm}""", Declaring(Listed), out _);
            PlanText(safe).Should().Contain(KeepsNothing, "nothing in the WHERE raises, and no document makes it true: " + PlanText(safe));
        }

        /// <summary>
        /// A projected condition is answered in the text sent and never in the plan, which holds what the query wrote. The
        /// text answers it only where no predicate of the projected expression raises, and here the text <c>body</c> cast
        /// to <c>INTEGER</c> raises on any string that is no number: each keeps the cast in process, where Calcite fails as
        /// it does. Where nothing raises, the plan still holds the condition and the request sends it as the parameter
        /// <c>FALSE</c>. An arm the text writes <c>FALSE</c> keeps its value there, so beside a cast of <c>n</c>'s text the
        /// service is never sent, Calcite computes the column. A conjunct of the condition the declaration makes true is
        /// another matter: the text deletes it as redundant, cast and all (<c>TODO.md</c>, <em>A deletion stops evaluating a
        /// conjunct that may raise</em>); beside a never-read arm no text is written, and the column fails as Calcite's does.
        /// </summary>
        [Fact]
        public void AProjectedConditionIsFoldedOnlyWhereNoPredicateOfItsExpressionRaises()
        {
            var container = Declaring(Listed);
            RelNode Planned(string expression) => PlanToCosmos($"""SELECT c."id", {expression} AS v FROM items AS c""", container, out _);
            string Projected(string expression) => PlanText(Planned(expression));

            var enclosing = Projected($"CASE WHEN NOT ({AsInteger("body")} = 1 AND {Read("kind")} = 'A') AND {NeverReadArm} THEN 1 ELSE 0 END");
            enclosing.Should().Contain("ClrCursorProject").And.Contain("CAST($1):INTEGER, 1)").And.NotContain("v=[0]", "the condition around the arm casts the body");

            var sibling = Projected($"CASE WHEN {AsInteger("body")} = 1 THEN 1 WHEN {Read("kind")} = 'C' THEN 2 ELSE 1 END");
            sibling.Should().Contain("=($2, 'C')").And.NotContain("v=[1]", "the arm folded would merge with the ELSE, its sibling's cast with it");

            var around = Projected($"CASE WHEN {AsInteger("body")} = 1 THEN CASE WHEN {Read("kind")} = 'C' THEN 2 ELSE 1 END ELSE 1 END");
            around.Should().Contain("CAST($1):INTEGER, 1)").And.NotContain("v=[1]", "the inner CASE folded would merge into the outer, its cast with it");

            var conjoined = Projected($"{AsInteger("body")} = 1 AND {NeverReadArm}");
            conjoined.Should().Contain("AND(=(CAST($1):INTEGER, 1)").And.NotContain("CAST(false)", "FALSE beside the cast would drop it");

            var folded = Planned($"CASE WHEN {Read("kind")} = 'C' THEN 2 ELSE 1 END");
            PlanText(folded).Should().Contain("v=[CASE(=(JSON_VALUE($0, '$.kind'), 'C'), 2, 1)]", "the plan holds the condition as written: " + PlanText(folded));
            var answered = Query(FindCosmos(folded), container);
            answered.Sql.Should().Be("""SELECT VALUE { "id": c.id, "v": (@p0 ? @p1 : @p2) } FROM items c""", PlanText(folded));
            answered.Parameters[0].Value.Should().Be(false, "nothing in it raises, so the text answers the condition");

            Projected($"CASE WHEN {Read("kind")} = 'A' AND {NeverReadArm} THEN 1 ELSE 0 END").Should().Contain("ClrCursorProject").And.NotContain("v=[0]",
                "n's text parses, so the text writes the arm FALSE, but keeps the arm's cast, which the service is never sent");

            Projected($"CASE WHEN NOT ({AsInteger("body")} = 1 AND {Read("kind")} = 'C') AND {NeverReadArm} THEN 1 ELSE 0 END")
                .Should().Contain("CAST($1):INTEGER, 1)").And.NotContain("v=[0]", "the arm's cast keeps the column in Calcite, the NOT and its cast with it");

            var deleted = Planned($"CASE WHEN NOT ({AsInteger("body")} = 1 AND {Read("kind")} = 'C') THEN 1 ELSE 0 END");
            PlanText(deleted).Should().Contain("CAST(JSON_VALUE($0, '$.body')):INTEGER, 1)", "the plan holds the NOT as written: " + PlanText(deleted));
            var sent = Query(FindCosmos(deleted), container);
            sent.Sql.Should().Be("""SELECT VALUE { "id": c.id, "v": (@p0 ? @p1 : @p2) } FROM items c""", PlanText(deleted));
            sent.Parameters[0].Value.Should().Be(true, "the NOT is true wherever it does not raise, so the text deletes it, cast and all");
        }

        /// <summary>
        /// A projected <c>CASE</c> whose two conditions the declaration contradicts, compared above with a column casting
        /// the text <c>body</c> to <c>INTEGER</c>: Calcite evaluates the cast on every document and fails on a string that is
        /// no number. The plan holds the <c>CASE</c> as written, so the simplifier finds no <c>NULL</c> to carry into the
        /// comparison and no empty relation is planned: the request is sent and the cast kept in process, the text answering
        /// both conditions as parameters <c>FALSE</c>; so with <c>ELSE NULL</c>, and a <c>COUNT</c> over it. Where the cast and
        /// a one-armed <c>CASE</c> meet in one strict comparison the plan is still the empty relation with no request: the
        /// filter lifts the comparison under the <c>CASE</c>'s guard and the split reduces that guard, as it does where the
        /// <c>WHERE</c> is written so (<c>TODO.md</c>, <em>A strict comparison's other operands move under a CASE's
        /// guard</em>); the cast stays in the plan, over no rows.
        /// </summary>
        [Fact]
        public void AProjectedConditionTheDeclarationContradictsEmptiesNoFilterAbove()
        {
            var container = Declaring(Listed);
            var kind = Read("kind");
            var arms = $"""SELECT c."id", CASE WHEN {kind} = 'C' THEN 1 WHEN {kind} = 'Z' THEN 2 END AS v, {AsInteger("body")} AS w FROM items AS c""";
            var otherwise = $"""SELECT c."id", CASE WHEN {kind} = 'C' THEN 1 WHEN {kind} = 'Z' THEN 2 ELSE NULL END AS v, {AsInteger("body")} AS w FROM items AS c""";

            foreach (var sql in new[]
            {
                $"""SELECT x."id" FROM ({arms}) AS x WHERE x.w = x.v""",
                $"""SELECT COUNT(*) AS n FROM ({arms}) AS x WHERE x.w = x.v""",
                $"""SELECT x."id" FROM ({otherwise}) AS x WHERE x.w > x.v""",
            })
            {
                var plan = PlanToCosmos(sql, container, out _);
                PlanText(plan).Should().NotContain(KeepsNothing, "the cast raises where body is no number: " + PlanText(plan));
                PlanText(plan).Should().Contain("ClrCursorFilter(condition=[").And.Contain("CAST($2):INTEGER", "the cast is Calcite's: " + PlanText(plan));
                var query = Query(FindCosmos(plan), container);
                query.Sql.Should().Contain("\"$f1\": (@p0 ? @p1 : (@p2 ? @p3 : @p4))", PlanText(plan));
                query.Parameters[0].Value.Should().Be(false, "kind is never C");
                query.Parameters[2].Value.Should().Be(false, "nor Z");
            }

            var one = $"""SELECT c."id", CASE WHEN {kind} = 'C' THEN 1 END AS v, {AsInteger("body")} AS w FROM items AS c""";
            foreach (var sql in new[]
            {
                $"""SELECT x."id" FROM (SELECT c."id", {AsInteger("body")} + CASE WHEN {kind} = 'C' THEN 1 END AS v FROM items AS c) AS x WHERE x.v > 0""",
                $"""SELECT x."id" FROM ({one}) AS x WHERE x.w <> x.v""",
                $"""SELECT x."id" FROM (SELECT c."id", CASE {kind} WHEN 'C' THEN 1 END AS v, {AsInteger("body")} AS w FROM items AS c) AS x WHERE x.w = x.v""",
            })
            {
                var plan = PlanToCosmos(sql, container, out _);
                PlanText(plan).Should().Contain(KeepsNothing, "the lift's guard is reduced: " + PlanText(plan));
                PlanText(plan).Should().Contain("ClrCursorFilter(condition=[CASE(=(JSON_VALUE($0, '$.kind'), 'C')").And.Contain("CAST(JSON_VALUE($0, '$.body')):INTEGER",
                    "what is lifted holds the cast, over no rows: " + PlanText(plan));
            }
        }

    }

}
