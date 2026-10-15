using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What a declared string changes about an operator over a rendering: the text <c>JSON_VALUE</c> renders is the
    /// value the service holds, so a string function over it reads the same thing at both ends.
    /// </summary>
    /// <remarks>
    /// Over a container that declares nothing, the same operators stay in process, because a stored number renders as
    /// <c>30</c> and the service hands the function the number. Each test is the pair.
    /// </remarks>
    public partial class CosmosDeclaredFactPlanningTests
    {

        const string Labels = """
        {
          "type": "object",
          "properties": { "label": { "type": "string" } }
        }
        """;

        const string Label = """JSON_VALUE(c."DOC", '$.label')""";

        /// <summary>
        /// A case fold over the accessor projects at the service where the path holds a string, and in process where
        /// nothing says so.
        /// </summary>
        /// <remarks>
        /// Measured against the emulator over a path holding a number and a boolean, <c>UPPER(label)</c> projected
        /// null for both where Calcite projects <c>30</c> and <c>TRUE</c>: the service's string functions are undefined
        /// over a value that is not a string. The guard on the accessor does not help, being about objects and arrays.
        /// </remarks>
        [Fact]
        public void ACaseFoldOverAnAccessorProjectsWhereThePathHoldsAString()
        {
            var sql = $"""SELECT UPPER({Label}) AS "u" FROM items AS c""";

            var declared = Declaring(Labels);
            Query(FindCosmos(PlanToCosmos(sql, declared, out _)), declared).Sql
                .Should().Be("SELECT VALUE { \"u\": UPPER((IS_PRIMITIVE(c.label) ? c.label : null)) } FROM items c");

            var undeclared = Declared(false);
            var best = PlanToCosmos(sql, undeclared, out _);

            PlanText(best).Should().Contain("UPPER(", "the fold is Calcite's over a path that may hold a number: " + PlanText(best));
            Query(FindCosmos(best), undeclared).Sql
                .Should().Be("SELECT VALUE { \"$f0\": (IS_PRIMITIVE(c.label) ? c.label : null) } FROM items c", "and the accessor beneath it is what goes down");
        }

        /// <summary>
        /// A case fold over the accessor in a filter pushes where the path holds a string, and stays above where
        /// nothing says so.
        /// </summary>
        /// <remarks>
        /// <c>UPPER(label) = 'TRUE'</c> keeps the stored boolean in Calcite, which renders it <c>true</c> and folds it,
        /// and nothing at the service, where <c>UPPER(true)</c> is undefined. The comparison looked at its operands to
        /// decide whether it compared a rendering, and the rendering was one call further down.
        /// </remarks>
        [Fact]
        public void ACaseFoldOverAnAccessorFiltersWhereThePathHoldsAString()
        {
            var sql = $"""SELECT c."DOC" FROM items AS c WHERE UPPER({Label}) = 'TRUE'""";

            var declared = Declaring(Labels);
            var pushed = PlanToCosmos(sql, declared, out _);

            Query(FindCosmos(pushed), declared).Sql.Should().Contain("WHERE (UPPER(c.label) = @p0)");
            PlanText(pushed).Should().NotContain("ClrCursorFilter", "the whole predicate is the service's: " + PlanText(pushed));

            var undeclared = Declared(false);
            var kept = PlanToCosmos(sql, undeclared, out _);

            PlanText(kept).Should().Contain("ClrCursorFilter", "the fold is Calcite's over a path that may hold a boolean: " + PlanText(kept));
            Query(FindCosmos(kept), undeclared).Sql.Should().NotContain("UPPER(", "and nothing of it is rendered");
        }

        /// <summary>
        /// The same for a <c>COALESCE</c> passing the accessor out as a column.
        /// </summary>
        /// <remarks>
        /// The column is read as the type the plan declared, which is text, and the service sends the value: measured,
        /// the read failed at the stored boolean where Calcite answers <c>true</c>. Over a declared string it is the
        /// value, and the coalesce pushes whole.
        /// </remarks>
        [Fact]
        public void ACoalesceOverAnAccessorProjectsWhereThePathHoldsAString()
        {
            var sql = $"""SELECT COALESCE({Label}, 'none') AS "l" FROM items AS c""";

            var declared = Declaring(Labels);
            Query(FindCosmos(PlanToCosmos(sql, declared, out _)), declared).Sql
                .Should().Be("SELECT VALUE { \"l\": ((IS_DEFINED(c.label) AND NOT IS_NULL(c.label) AND IS_PRIMITIVE(c.label)) ? (IS_PRIMITIVE(c.label) ? c.label : null) : @p0) } FROM items c");

            var undeclared = Declared(false);
            PlanText(PlanToCosmos(sql, undeclared, out _)).Should().Contain("CASE(", "over a path that may hold a boolean the coalesce is Calcite's");
        }

        /// <summary>
        /// A grouping by the accessor, and a <c>DISTINCT</c> over it, push where the path holds a string, and stay
        /// above the guarded column where nothing says so.
        /// </summary>
        /// <remarks>
        /// Calcite groups the rendering and the service the value, which part where a path holds <c>30</c> in one
        /// document and <c>"30"</c> in another: measured over the typed container, a pushed <c>GROUP BY</c> returned
        /// the group <c>"30"</c> twice, and <c>COUNT(DISTINCT …)</c>, which Calcite expands into a grouping beneath
        /// the count, counted 6 where Calcite counts 5. A string is its own rendering, so over a declared one the
        /// groups are the same at both ends.
        /// </remarks>
        [Fact]
        public void AGroupingOverAnAccessorPushesWhereThePathHoldsAString()
        {
            var grouped = $"""SELECT {Label} AS "l", COUNT(*) AS "n" FROM items AS c GROUP BY {Label}""";
            var distinct = $"""SELECT DISTINCT {Label} AS "l" FROM items AS c""";

            var declared = Declaring(Labels);
            Query(FindCosmos(PlanToCosmos(grouped, declared, out _)), declared).Sql
                .Should().Be("SELECT (IS_PRIMITIVE(c.label) ? c.label : null) AS \"l\", COUNT(1) AS \"n\" FROM items c GROUP BY (IS_PRIMITIVE(c.label) ? c.label : null)");
            Query(FindCosmos(PlanToCosmos(distinct, declared, out _)), declared).Sql
                .Should().Be("SELECT DISTINCT VALUE { \"l\": (IS_PRIMITIVE(c.label) ? c.label : null) } FROM items c");

            var undeclared = Declared(false);

            foreach (var sql in new[] { grouped, distinct })
            {
                var best = PlanToCosmos(sql, undeclared, out _);

                PlanText(best).Should().Contain("ClrCursorAggregate", "over a path that may hold a number the grouping is Calcite's: " + PlanText(best));
                Query(FindCosmos(best), undeclared).Sql
                    .Should().Be("SELECT VALUE { \"l\": (IS_PRIMITIVE(c.label) ? c.label : null) } FROM items c", "and the guarded column beneath it is what goes down");
            }
        }

    }

}
