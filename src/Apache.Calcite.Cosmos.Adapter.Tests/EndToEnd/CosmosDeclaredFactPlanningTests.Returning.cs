using System.Linq;

using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What a declaration may conclude from a comparison through a typed <c>RETURNING</c>: what the path holds where the
    /// clause asserts its type, and only what a cast to a number would say where it parses.
    /// </summary>
    /// <remarks>
    /// <c>RETURNING DECIMAL</c> is the one that parses, and over a path declared a string it was read as the stored number
    /// and the filter rewritten to <c>FALSE</c>. Found by the review of the predicate oracle's gate, and reproduced on the
    /// emulator: over a container declared <c>{ body: string }</c> holding <c>"2"</c>, <c>" 2 "</c>, <c>"3"</c> and
    /// <c>"2.0"</c>, <c>= 2</c> returned nothing where Calcite returns three documents, and <c>&gt; 2</c> nothing where
    /// it returns one.
    /// </remarks>
    public partial class CosmosDeclaredFactPlanningTests
    {

        const string Bodies = """{ "type": "object", "properties": { "body": { "type": "string" } } }""";

        const string WholeBodies = """{ "type": "object", "properties": { "body": { "type": "integer" } } }""";

        const string Body = """JSON_VALUE(c."DOC", '$.body' RETURNING DECIMAL)""";

        /// <summary>
        /// Over a path declared a string, a comparison through <c>RETURNING DECIMAL</c> is no contradiction: it is
        /// rechecked in process over every string, and only a number outside the window is left at the service.
        /// </summary>
        [Theory]
        [InlineData("= 2")]
        [InlineData("> 2")]
        [InlineData("= 2.5")]
        public void ADecimalReturningOverADeclaredStringIsNoContradiction(string comparison)
        {
            var container = Declaring(Bodies);
            var best = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE {Body} {comparison}""", container, out _);

            PlanText(best).Should().NotContain("condition=[false]", "a stored \"2\" is 2 to the clause: " + PlanText(best));
            PlanText(best).Should().Contain("ClrCursorFilter", "and the comparison is Calcite's: " + PlanText(best));

            Query(FindCosmos(best), container).Sql.Should().Contain("(NOT IS_NUMBER(c.body)) OR",
                "a string passes to the recheck, the declaration saying nothing of a number");
        }

        /// <summary>
        /// The same through a view's column, which a host transposing the filter writes as the accessor; and the same
        /// over the partition key, where a literal 2 must not route to the partition named for the number.
        /// </summary>
        [Fact]
        public void ADecimalReturningIsNoClaimThroughAViewOrOnThePartitionKey()
        {
            var container = Declaring(Bodies);
            var view = PlanToCosmos($"""SELECT p."id" FROM (SELECT c."id", {Body} AS "B" FROM items AS c) AS p WHERE p."B" = 2""", container, out _);

            PlanText(view).Should().NotContain("condition=[false]", PlanText(view));

            var keyed = Declaring("""{ "type": "object", "properties": { "ref": { "type": "string" } } }""");
            var routed = Query(FindCosmos(PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.ref' RETURNING DECIMAL) = 2""", keyed, out _)), keyed);

            routed.PartitionKeyValues.Should().BeNull("documents storing \"2\" live in the partition named for the string");
        }

        /// <summary>
        /// Over a path declared an integer, the type test the window carries is dropped, and the service does all the
        /// narrowing; the comparison is still rechecked, the parse being Calcite's.
        /// </summary>
        [Fact]
        public void ADecimalReturningOverADeclaredIntegerPushesTheWindowAlone()
        {
            var container = Declaring(WholeBodies);
            var best = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE {Body} = 2""", container, out _);
            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Be("SELECT VALUE c FROM items c WHERE ((c.body > @p0) AND (c.body < @p1))");
            query.Parameters.Select(p => p.Value?.ToString()).Should().Equal("1", "3");
            PlanText(best).Should().Contain("ClrCursorFilter", PlanText(best));
        }

        /// <summary>
        /// A literal cast down to a <c>DECIMAL</c> its whole digits do not fit raises in Calcite on every row, and the
        /// window it would have stated is no fact: over a path declared at most 10, <c>&gt; 50</c> is not rewritten to
        /// <c>FALSE</c>.
        /// </summary>
        /// <remarks>
        /// The comparison's common type would pass nineteen digits, so Calcite casts the literal to the clause's type,
        /// <c>CAST(50):DECIMAL(19, 18)</c>, which has one whole digit. Read as 50, the window said the path holds a number
        /// above 49, which the maximum contradicts, and the statement answered nothing where the engine raises whatever
        /// the data. Found by the review of the <c>RETURNING DECIMAL</c> fix.
        /// </remarks>
        [Fact]
        public void ALiteralCastDownToADecimalItDoesNotFitIsNoFact()
        {
            var container = Declaring("""{ "type": "object", "properties": { "body": { "type": "integer", "maximum": 10 } } }""");
            var best = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.body' RETURNING DECIMAL(19, 18)) > 50""", container, out _);

            PlanText(best).Should().NotContain("condition=[false]", "the engine raises rather than answering nothing: " + PlanText(best));
            Query(FindCosmos(best), container).Sql.Should().NotContain("c.body >", "and no bound is read from the literal");

            var fits = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.body' RETURNING DECIMAL(19, 18)) > 5""", container, out _);
            Query(FindCosmos(fits), container).Sql.Should().Contain("c.body > @p0", "a literal the type holds still bounds");
        }

        /// <summary>
        /// The control: a <c>RETURNING</c> that asserts its type is a claim about what the path holds, and a declared
        /// string contradicts it.
        /// </summary>
        /// <remarks>
        /// Calcite raises on every stored string under <c>RETURNING INTEGER</c>, so the rows it answers for hold a
        /// number; with the path declared a string there are none, and the rewrite keeps nothing — the deliberate
        /// divergence <c>DESIGN.md</c> records, answering where the engine raises.
        /// </remarks>
        [Fact]
        public void AnAssertingReturningOverADeclaredStringIsStillAContradiction()
        {
            var container = Declaring(Bodies);
            var best = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.body' RETURNING INTEGER) = 2""", container, out _);

            PlanText(best).Should().Contain("condition=[false]", PlanText(best));
        }

    }

}
