using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// A <c>FALSE</c> the rewriter writes for an arm of a <c>CASE</c> never read stops no evaluation that may raise: the
    /// arm's gate reads the <c>WHERE</c> as written, a conjunct the deletions drop among it.
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

    }

}
