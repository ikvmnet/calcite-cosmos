using FluentAssertions;
using Xunit;

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
        /// A <c>FALSE</c> written for a projected condition lets the simplifier drop more than it: a condition beside it,
        /// <c>X AND FALSE</c> being <c>FALSE</c>; a sibling condition, an arm left with its sibling's value merging into it;
        /// and the condition of the <c>CASE</c> around it, merged in turn. So a condition is folded only where no predicate
        /// of the projected expression raises, and here the text <c>body</c> cast to <c>INTEGER</c> raises on any string
        /// that is no number: each keeps the cast in process, where Calcite fails as it does. Where nothing raises the
        /// condition still folds. A conjunct of the condition the declaration makes true is another matter: it is deleted
        /// as redundant, cast and all, and only then is what is left folded (<c>TODO.md</c>, <em>A deletion stops evaluating
        /// a conjunct that may raise</em>).
        /// </summary>
        [Fact]
        public void AProjectedConditionIsFoldedOnlyWhereNoPredicateOfItsExpressionRaises()
        {
            var container = Declaring(Listed);
            string Projected(string expression) => PlanText(PlanToCosmos($"""SELECT c."id", {expression} AS v FROM items AS c""", container, out _));

            var enclosing = Projected($"CASE WHEN NOT ({AsInteger("body")} = 1 AND {Read("kind")} = 'A') AND {NeverReadArm} THEN 1 ELSE 0 END");
            enclosing.Should().Contain("ClrCursorProject").And.Contain("CAST($1):INTEGER, 1)").And.NotContain("v=[0]", "the condition around the arm casts the body");

            var sibling = Projected($"CASE WHEN {AsInteger("body")} = 1 THEN 1 WHEN {Read("kind")} = 'C' THEN 2 ELSE 1 END");
            sibling.Should().Contain("=($2, 'C')").And.NotContain("v=[1]", "the arm folded would merge with the ELSE, its sibling's cast with it");

            var around = Projected($"CASE WHEN {AsInteger("body")} = 1 THEN CASE WHEN {Read("kind")} = 'C' THEN 2 ELSE 1 END ELSE 1 END");
            around.Should().Contain("CAST($1):INTEGER, 1)").And.NotContain("v=[1]", "the inner CASE folded would merge into the outer, its cast with it");

            var conjoined = Projected($"{AsInteger("body")} = 1 AND {NeverReadArm}");
            conjoined.Should().Contain("AND(=(CAST($1):INTEGER, 1)").And.NotContain("CAST(false)", "FALSE beside the cast would drop it");

            Projected($"CASE WHEN {Read("kind")} = 'C' THEN 2 ELSE 1 END").Should().Contain("v=[CASE(false, 2, 1)]", "nothing in it raises");
            Projected($"CASE WHEN {Read("kind")} = 'A' AND {NeverReadArm} THEN 1 ELSE 0 END").Should().Contain("v=[0]", "n's text parses, so nothing raises");

            Projected($"CASE WHEN NOT ({AsInteger("body")} = 1 AND {Read("kind")} = 'C') AND {NeverReadArm} THEN 1 ELSE 0 END")
                .Should().Contain("v=[0]", "the NOT is true wherever it does not raise, so it is deleted, and what is left raises nowhere");
        }

    }

}
