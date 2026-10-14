using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// The plans the theory of JSON gets right where a Horn closure could not: a disjunction reasoned about by cases,
    /// a disequality narrowing a domain, and a union of enums. Each beside the control that shows the declaration, and
    /// not the query, is what moved it.
    /// </summary>
    /// <remarks>
    /// Numbered as the integration plan numbers its gaps. Gaps 4 and 5, a bound against a comparison, need a range to be
    /// read off a predicate, which the extractor does not do yet; see <c>TODO.md</c>.
    /// </remarks>
    public partial class CosmosDeclaredFactPlanningTests
    {

        /// <summary>
        /// A container of orders, invoices and notes, discriminated by <c>kind</c>: an order's and an invoice's
        /// <c>ref</c> is a canonical UUID, and a note carries none.
        /// </summary>
        static readonly string Kinds = """
        {
          "type": "object",
          "oneOf": [
            { "properties": { "kind": { "const": "order" }, "ref": { "type": "string", "pattern": "@" } } },
            { "properties": { "kind": { "const": "invoice" }, "ref": { "type": "string", "pattern": "@" } } },
            { "properties": { "kind": { "const": "note" } } }
          ]
        }
        """.Replace("@", RefPattern.Replace("\\", "\\\\"));

        static string WithRef(string condition) => $"""SELECT c."DOC" FROM items AS c WHERE {condition} AND {Ref} = UUID'{Canonical}'""";

        /// <summary>
        /// Gap 1: <c>kind IN ('order', 'invoice')</c> proves what both kinds declare, though neither guard holds alone.
        /// </summary>
        /// <remarks>
        /// The context splits on the discriminator, one case per value the query leaves it, and keeps what every case
        /// proves: both say <c>ref</c> is a canonical UUID, so the comparison lowers onto the stored string. A closure
        /// reads only what one guard proves, and kept the comparison in process over every order and invoice.
        /// </remarks>
        [Fact]
        public void TwoKindsNamedTogetherProveWhatBothDeclare()
        {
            var container = Declaring(Kinds);
            var query = Query(FindCosmos(PlanToCosmos(WithRef($"{Kind} IN ('order', 'invoice')"), container, out _)), container);

            query.Sql.Should().Contain("c.ref = @", "both kinds write ref in the canonical form: " + query.Sql);
        }

        /// <summary>
        /// The control for gap 1: a note declares no form, so <c>kind IN ('order', 'note')</c> proves nothing about
        /// <c>ref</c>, and the comparison stays where it was.
        /// </summary>
        [Fact]
        public void TwoKindsOneOfWhichSaysNothingProveNothing()
        {
            var container = Declaring(Kinds);
            var query = Query(FindCosmos(PlanToCosmos(WithRef($"{Kind} IN ('order', 'note')"), container, out _)), container);

            query.Sql.Should().NotContain("c.ref = @", "a note's ref may be spelled any way: " + query.Sql);
        }

        /// <summary>
        /// Gap 2: a disequality narrows a domain. <c>kind &lt;&gt; 'invoice'</c> over a container of orders and
        /// invoices leaves only orders, and the order branch's facts follow.
        /// </summary>
        [Fact]
        public void ADisequalityLeavesTheOneKindThatRemains()
        {
            const string Two = """
            {
              "type": "object",
              "oneOf": [
                { "properties": { "kind": { "const": "order" }, "ref": { "type": "string", "pattern": "@" } } },
                { "properties": { "kind": { "const": "invoice" } } }
              ]
            }
            """;

            var container = Declaring(Two.Replace("@", RefPattern.Replace("\\", "\\\\")));

            var order = Query(FindCosmos(PlanToCosmos(WithRef($"{Kind} <> 'invoice'"), container, out _)), container);
            order.Sql.Should().Contain("c.ref = @", "what is not an invoice is an order: " + order.Sql);

            var invoice = Query(FindCosmos(PlanToCosmos(WithRef($"{Kind} <> 'order'"), container, out _)), container);
            invoice.Sql.Should().NotContain("c.ref = @", "and what is not an order is an invoice, which declares nothing: " + invoice.Sql);
        }

        /// <summary>
        /// Gap 3: an <c>anyOf</c> of two enums is the enum of their members, and a value outside it keeps no row.
        /// </summary>
        [Fact]
        public void AUnionOfEnumsIsTheirMembers()
        {
            var container = Declaring("""
            { "type": "object", "properties": { "status": { "anyOf": [ { "enum": ["open", "closed"] }, { "enum": ["archived"] } ] } } }
            """);

            var purged = PlanToCosmos(string.Format(Status, "purged"), container, out _);
            PlanText(purged).Should().Contain("condition=[false]", "no branch admits it: " + PlanText(purged));

            var archived = PlanToCosmos(string.Format(Status, "archived"), container, out _);
            PlanText(archived).Should().NotContain("condition=[false]", "one branch admits it: " + PlanText(archived));
        }

        /// <summary>
        /// F3: two declared claims that no one value satisfies say the property is never there, which a document
        /// satisfies by not having it. A query that says nothing about the property keeps its rows.
        /// </summary>
        /// <remarks>
        /// The closure paired the two claims as a contradiction whether or not anything said the path was there, so
        /// every predicate over such a container was reduced to <c>false</c>, and every document was lost.
        /// </remarks>
        [Fact]
        public void TwoDeclaredClaimsNoValueSatisfiesContradictNoQuery()
        {
            var container = Declaring("""
            { "type": "object", "properties": { "status": { "type": "integer", "enum": ["open", "closed"] } } }
            """);

            var other = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE {Kind} = 'A'""", container, out _);
            PlanText(other).Should().NotContain("condition=[false]", "a document without a status satisfies the declaration: " + PlanText(other));

            var status = PlanToCosmos(string.Format(Status, "open"), container, out _);
            PlanText(status).Should().Contain("condition=[false]", "and none with one does: " + PlanText(status));
        }

        /// <summary>
        /// F2: a comparison with the text a stored number renders as matches the number too, so it says nothing
        /// about which of the two the document holds.
        /// </summary>
        /// <remarks>
        /// <c>JSON_VALUE</c> reads a stored <c>5</c> as the text <c>5</c>. Read as the string <c>"5"</c>, the
        /// equality contradicted a declared integer and the predicate became <c>false</c>, losing the document
        /// storing the number 5 — the one it was written to find.
        /// </remarks>
        [Fact]
        public void TextARenderedNumberMatchesIsNotAStoredString()
        {
            var container = Declaring("""{ "type": "object", "properties": { "n": { "type": "integer" } } }""");
            const string Text = """SELECT c."DOC" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.n') = '{0}'""";

            var five = PlanToCosmos(string.Format(Text, "5"), container, out _);
            PlanText(five).Should().NotContain("condition=[false]", "a stored 5 reads as '5': " + PlanText(five));

            var word = PlanToCosmos(string.Format(Text, "five"), container, out _);
            PlanText(word).Should().Contain("condition=[false]", "and no integer reads as 'five': " + PlanText(word));
        }

    }

}
