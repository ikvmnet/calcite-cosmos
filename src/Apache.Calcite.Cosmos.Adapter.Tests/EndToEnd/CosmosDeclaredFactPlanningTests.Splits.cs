using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What the manual and <c>TODO.md</c> say a filter beside a part the declaration rules out is planned as: sent where the
    /// service takes it whole, and, where part of it stays in Calcite, the empty relation beneath that part, which reads
    /// only the rows the part sent returns (<c>docs/14</c>, 14.2; <c>DESIGN.md</c>, <em>Not covered: the split</em>).
    /// </summary>
    public partial class CosmosDeclaredFactPlanningTests
    {

        const string Statuses = """{ "type": "object", "required": ["status", "body", "name"], "properties": { "status": { "enum": ["active", "archived"] }, "body": { "type": "string" }, "name": { "type": "string" } } }""";

        /// <summary>
        /// A part that stays in Calcite beside a contradicted <c>status</c> reads what the part sent returns, which the
        /// declaration says is nothing, so no request is made and the part kept fails on nothing, where Calcite fails on a
        /// required string read as an integer. A comparison of two properties no declaration names stays in Calcite too; of
        /// two declared strings it is sent with the rest on Azure.
        /// </summary>
        [Fact]
        public void ThePartKeptInProcessReadsOnlyWhatTheSplitSends()
        {
            var container = Declaring(Statuses);
            foreach (var kept in new[]
            {
                """JSON_VALUE(c."DOC", '$.body' RETURNING INTEGER) = 2""",
                """UPPER(JSON_VALUE(c."DOC", '$.name')) = 'X'""",
                """JSON_VALUE(c."DOC", '$.a') = JSON_VALUE(c."DOC", '$.b')""",
            })
            {
                var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND {kept}""", container, out _);
                PlanText(plan).Should().Contain(KeepsNothing, "the part sent keeps nothing: " + PlanText(plan));
                PlanText(plan).Should().Contain("ClrCursorFilter", "and the part kept reads it: " + PlanText(plan));
            }
        }

        /// <summary>
        /// A <c>LIKE</c> over <c>id</c>, which the adapter cannot show never fails, is sent with the contradicted
        /// <c>status</c> as one request that finds nothing.
        /// </summary>
        [Fact]
        public void ALikeOverTheIdBesideAContradictionIsSentWhole()
        {
            var container = Declaring(Statuses);
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND c."id" LIKE 'a%'""", container, out _);
            PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));
            Query(FindCosmos(plan), container).Sql.Should().Contain("c.status", "the whole WHERE is sent");
        }

        /// <summary>
        /// <c>RETURNING DOUBLE</c> over a required integer is sent, though Calcite fails on a whole number written without a
        /// point: the rendering graph lifts the agreement beside the raise (<c>TODO.md</c>, <em>The rendering graph lifts an
        /// agreement where a raise sits beside a truth value</em>). A fraction no integer equals is sent too, since a raise
        /// keeps the reduce shut.
        /// </summary>
        [Fact]
        public void AReturningDoubleOverARequiredIntegerIsSent()
        {
            var container = Declaring("""{ "type": "object", "required": ["body"], "properties": { "body": { "type": "integer" } } }""");
            foreach (var value in new[] { "2", "1.5" })
            {
                var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.body' RETURNING DOUBLE) = {value}""", container, out _);
                PlanText(plan).Should().NotContain(KeepsNothing, "Calcite may fail: " + PlanText(plan));
                Query(FindCosmos(plan), container).Sql.Should().EndWith("WHERE (c.body = @p0)", "the agreement is lifted");
            }
        }

        /// <summary>
        /// <c>RETURNING DOUBLE</c> against a parameter beside a contradicted <c>status</c> is sent: a typed comparison with a
        /// parameter raises over every scalar (<c>TODO.md</c>, <em>A typed comparison with a parameter raises over every
        /// scalar</em>), so the reduce stays shut.
        /// </summary>
        [Fact]
        public void ATypedComparisonWithAParameterBesideAContradictionIsSent()
        {
            var container = Declaring("""{ "type": "object", "required": ["status"], "properties": { "status": { "enum": ["active", "archived"] }, "n": { "type": "number" } } }""");
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.status') = 'deleted' AND JSON_VALUE(c."DOC", '$.n' RETURNING DOUBLE) = ?""", container, out _);
            PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));
        }

        /// <summary>
        /// The cast to text of a key declared an integer keeps no document: Calcite casts a <c>VARIANT</c>'s number to null
        /// (<c>Calcite.CastOfARawValueToText</c>), and the declaration rules out a stored string. Undeclared, the same
        /// comparison routes to the stored string <c>"30"</c> (<c>CosmosPlannerTests.ACastAgainstTextANumberRendersAsIsTakenOverAVariant</c>).
        /// </summary>
        [Fact]
        public void ACastToTextOfAKeyDeclaredANumberKeepsNothing()
        {
            var container = Declaring("""{ "type": "object", "properties": { "ref": { "type": "integer" } } }""");
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE CAST(c."$.ref" AS VARCHAR) = '30'""", container, out _);
            PlanText(plan).Should().Contain(KeepsNothing, PlanText(plan));
        }

        /// <summary>
        /// A boolean pins the key, written <c>CAST(TRUE AS VARIANT)</c> since Calcite compares a <c>VARIANT</c> with no bare
        /// boolean (<c>docs/07</c>).
        /// </summary>
        [Fact]
        public void ABooleanKeyPinsThePartition()
        {
            var container = Declaring("""{ "type": "object" }""");
            var plan = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE c."$.ref" = CAST(TRUE AS VARIANT)""", container, out _);
            Query(FindCosmos(plan), container).PartitionKeyValues.Should().Equal(true);
        }

    }

}
