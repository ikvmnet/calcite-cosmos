using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What the clauses after a filter lean on: the rows the filter kept (L1). A sort, a grouping and a projection each read
    /// rows the <c>WHERE</c> has already applied to, so what its pushed conjuncts establish holds of every one of them —
    /// beside the declaration, which holds of every document. Each beside the control that leans on nothing the filter
    /// proves; <c>CosmosDifferentialTests</c> holds the same shapes to Calcite on the emulator
    /// (<c>CosmosPushdownProbeTests.ClauseProbes</c>).
    /// </summary>
    public partial class CosmosDeclaredFactPlanningTests
    {

        /// <summary>
        /// A string or null, and not required: what a sort is refused over for its null placement, and pushed over once the
        /// filter has removed the nulls. The strings a domain below U+D800, so that only the placement is in question: the
        /// service orders strings by code point and Calcite by UTF-16 code unit, which part only beyond.
        /// </summary>
        const string Optional = """
        { "type": "object", "properties": { "t": { "enum": ["A", "a", "B", null] }, "k": { "type": ["integer", "null"] } } }
        """;

        const string T = """JSON_VALUE(c."DOC", '$.t')""";

        /// <summary>
        /// <c>WHERE t IS NOT NULL ORDER BY t</c> pushes: the filter removes both senses of absent, so the two placements
        /// have nothing to disagree about, and the declaration says the text is the value.
        /// </summary>
        /// <remarks>
        /// Calcite's default puts nulls last ascending and the service puts them first, so a key that may be absent is
        /// refused under the default — the control. Before the sort read the filter beneath, the guarantee came from
        /// Calcite's pulled-up predicates, which stop at an accessor call, and this stayed in process.
        /// </remarks>
        [Fact]
        public void AnIsNotNullMakesADeclaredStringASortKey()
        {
            var container = Container(Optional);

            var best = PlanToCosmos($"""SELECT {T} FROM items AS c WHERE {T} IS NOT NULL ORDER BY 1""", container, out _);
            Query(FindCosmos(best), container).Sql.Should().EndWith("ORDER BY c.t ASC");
            PlanText(best).Should().NotContain("ClrCursorSort", "the sort reaches the service: " + PlanText(best));

            var descending = PlanToCosmos($"""SELECT {T} FROM items AS c WHERE {T} IS NOT NULL ORDER BY 1 DESC""", container, out _);
            Query(FindCosmos(descending), container).Sql.Should().EndWith("ORDER BY c.t DESC");

            var unguarded = PlanToCosmos($"""SELECT {T} FROM items AS c ORDER BY 1""", container, out _);
            PlanText(unguarded).Should().Contain("ClrCursorSort", "an absent t sorts first at the service and last here: " + PlanText(unguarded));
        }

        /// <summary>
        /// Any conjunct that is never true of a null proves the key there: a comparison over the text says the path holds a
        /// scalar, which is all the placement needs.
        /// </summary>
        [Fact]
        public void AComparisonProvesASortKeyPresent()
        {
            var container = Container(Optional);

            var best = PlanToCosmos($"""SELECT {T} FROM items AS c WHERE {T} > 'A' ORDER BY 1""", container, out _);
            Query(FindCosmos(best), container).Sql.Should().EndWith("WHERE (c.t > @p0) ORDER BY c.t ASC");
        }

        /// <summary>
        /// A filter over another path proves nothing of the key.
        /// </summary>
        [Fact]
        public void AFilterOverAnotherPathDoesNotProveTheSortKey()
        {
            var container = Container(Optional);

            var best = PlanToCosmos($"""SELECT {T} FROM items AS c WHERE JSON_VALUE(c."DOC", '$.k' RETURNING INTEGER) IS NOT NULL ORDER BY 1""", container, out _);
            PlanText(best).Should().Contain("ClrCursorSort", "k being there says nothing of t: " + PlanText(best));
        }

        /// <summary>
        /// A string the filter's kind declares is a grouping key, and a sort key whose order the rows complete, its
        /// characters unknown; the kind that declares an integer there is neither, its rendering being text the service
        /// orders and groups as numbers.
        /// </summary>
        [Fact]
        public void AKindTheFilterKeepsDeclaresTheKeysText()
        {
            var container = Container(Planning.CosmosPushdownProbeTests.KindsSchema);
            const string Kind = """JSON_VALUE(c."DOC", '$.kind')""";
            const string Label = """JSON_VALUE(c."DOC", '$.label')""";

            // The text, but not its characters: a label may hold any, and the service orders strings by code point where
            // Calcite orders them by UTF-16 code unit. So the sort is the service's checked as the rows arrive, and the
            // grouping, which asks only equality, is the service's outright.
            var sorted = PlanToCosmos($"""SELECT {Label} FROM items AS c WHERE {Kind} = 'a' AND {Label} IS NOT NULL ORDER BY 1""", container, out _);
            Query(FindCosmos(sorted), container).Sql.Should().EndWith("ORDER BY c.label ASC");
            Query(FindCosmos(sorted), container).Order!.Keys.Should().ContainSingle().Which.Anchor.Should().Be(Apache.Calcite.Cosmos.Adapter.Metadata.CosmosOrderAnchor.Text);

            var grouped = PlanToCosmos($"""SELECT {Label}, COUNT(*) FROM items AS c WHERE {Kind} = 'a' GROUP BY {Label}""", container, out _);
            PlanText(grouped).Should().StartWith("CosmosToClrCursorConverter", "the grouping reaches the service: " + PlanText(grouped));
            Query(FindCosmos(grouped), container).Sql.Should().Contain("GROUP BY (IS_PRIMITIVE(c.label) ? c.label : null)");

            var distinct = PlanToCosmos($"""SELECT DISTINCT {Label} FROM items AS c WHERE {Kind} = 'a'""", container, out _);
            Query(FindCosmos(distinct), container).Sql.Should().StartWith("SELECT DISTINCT VALUE");

            var integers = PlanToCosmos($"""SELECT {Label}, COUNT(*) FROM items AS c WHERE {Kind} = 'b' GROUP BY {Label}""", container, out _);
            PlanText(integers).Should().Contain("ClrCursorAggregate", "a stored 30 and a stored \"30\" are one group as text: " + PlanText(integers));
        }

        /// <summary>
        /// A projection under a rank reads the rows the filter kept, as a plain projection does: <c>UPPER(label)</c> under
        /// <c>kind = 'a'</c> folds a string kind <c>a</c> declares, and the whole statement ranks at the service. Read from
        /// the declaration alone, as a translator built without facts did, the label might have been a number the service's
        /// fold is undefined over, and the rank, its page and the projection stayed in process.
        /// </summary>
        [Fact]
        public void AProjectionUnderARankReadsTheRowsTheFilterKept()
        {
            var container = Container(Planning.CosmosPushdownProbeTests.KindsSchema);
            const string Kind = """JSON_VALUE(c."DOC", '$.kind')""";
            string Ranked(string kind) =>
                $"""SELECT UPPER(JSON_VALUE(c."DOC", '$.label')) AS "L" FROM items AS c WHERE {Kind} = '{kind}' ORDER BY FULLTEXTSCORE(JSON_VALUE(c."DOC", '$.label'), 'steel') DESC FETCH FIRST 10 ROWS ONLY""";

            var ranked = PlanToCosmos(Ranked("a"), container, out _);
            PlanText(ranked).Should().Contain("CosmosRank", "the rank reaches the service: " + PlanText(ranked));
            Query(FindCosmos(ranked), container).Sql.Should().Contain("ORDER BY RANK");

            var unproved = PlanToCosmos(Ranked("b"), container, out _);
            PlanText(unproved).Should().NotContain("CosmosRank", "kind b declares label an integer: " + PlanText(unproved));
        }

        /// <summary>
        /// The binding a <c>DISTINCT</c> gives its key reads the declaration, not the rows: a sort above a <c>DISTINCT</c>
        /// whose key only the filter proves present stays in process, while the <c>DISTINCT</c> itself pushes.
        /// </summary>
        /// <remarks>
        /// The binding is a question the sort's rule asks of the <c>DISTINCT</c> on its own walk of a set, and
        /// implementation asks it again of the tree the planner chose; only what holds of every document answers alike on
        /// both. See <c>CosmosAggregate.DistinctKeyBinding</c>.
        /// </remarks>
        [Fact]
        public void ADistinctBindsItsKeyByTheDeclaration()
        {
            var container = Container(Optional);

            var best = PlanToCosmos($"""SELECT DISTINCT {T} FROM items AS c WHERE {T} IS NOT NULL ORDER BY 1""", container, out _);

            PlanText(best).Should().Contain("ClrCursorSort", "the key binds to no path above the DISTINCT: " + PlanText(best));
            Query(FindCosmos(best), container).Sql.Should().StartWith("SELECT DISTINCT VALUE");
        }

    }

}
