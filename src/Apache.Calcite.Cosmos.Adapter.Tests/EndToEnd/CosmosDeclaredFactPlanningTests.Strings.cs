using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What a declared <c>pattern</c> changes about a string function: each pushes where the facts prove its argument's
    /// characters lie where the service computes what Calcite does, and is computed in process elsewhere
    /// (<c>CosmosStringFunctions</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every path here is declared a string, so the rendering is the value at both ends, and what is left to prove is the
    /// characters. <c>s</c> declares nothing of them; <c>a</c> printable ASCII, which a space and nothing the service
    /// trims; <c>b</c> every unit but the surrogates, so no character beyond U+FFFF; <c>w</c> everything but what the
    /// service trims beside a space.
    /// </para>
    /// <para>
    /// Measured, over a path declared a string and nothing more, each of these was pushed exactly with nothing rechecked,
    /// and answered wrongly where a document held a supplementary character, ß, U+212A, a tab or U+00A0
    /// (<c>reports/verify-strings.md</c>).
    /// </para>
    /// </remarks>
    public partial class CosmosDeclaredFactPlanningTests
    {

        const string Characters = """
        {
          "type": "object",
          "properties": {
            "s": { "type": "string" },
            "a": { "type": "string", "pattern": "^[ -~]*$" },
            "b": { "type": "string", "pattern": "^[\\u0000-\\uD7FF\\uE000-\\uFFFF]*$" },
            "w": { "type": "string", "pattern": "^[^\\t\\n\\u000B\\f\\r\\u0085\\u00A0\\u1680\\u180E\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]*$" }
          }
        }
        """;

        static string Of(string path) => $"""JSON_VALUE(c."DOC", '$.{path}')""";

        /// <summary>The statement a projection or a filter over the declared paths sends.</summary>
        static string StatementOf(string sql, out string plan)
        {
            var container = Declaring(Characters);
            var best = PlanToCosmos(sql, container, out _);
            plan = PlanText(best);
            return Query(FindCosmos(best), container).Sql;
        }

        static string Projected(string expression, out string plan) => StatementOf($"""SELECT {expression} AS "v" FROM items AS c""", out plan);

        static string Filtered(string condition, out string plan) => StatementOf($"""SELECT c."id" FROM items AS c WHERE {condition}""", out plan);

        // ── Counts ────────────────────────────────────────────────────────────────────────

        /// <remarks>
        /// <c>LENGTH</c> counts code points and Calcite's <c>CHAR_LENGTH</c> UTF-16 units: over <c>a</c>U+1F600<c>b</c>
        /// 3 against 4. Declared free of the surrogates, the path's strings count alike; declared nothing of, it is counted
        /// in process over the value the service sends.
        /// </remarks>
        [Fact]
        public void ACountPushesOverAPathDeclaredFreeOfTheSurrogates()
        {
            Projected($"CHAR_LENGTH({Of("b")})", out _).Should().Contain("LENGTH((IS_PRIMITIVE(c.b) ? c.b : null))");
            Filtered($"CHAR_LENGTH({Of("b")}) = 3", out var kept).Should().Contain("LENGTH(c.b)");
            kept.Should().NotContain("ClrCursorFilter", "the count is exact: " + kept);

            Projected($"CHAR_LENGTH({Of("s")})", out var counted).Should().NotContain("LENGTH(");
            counted.Should().Contain("CHAR_LENGTH(", "Calcite counts: " + counted);
            Filtered($"CHAR_LENGTH({Of("s")}) = 3", out var above).Should().NotContain("LENGTH(");
            above.Should().Contain("ClrCursorFilter", "the comparison is Calcite's to make: " + above);
        }

        /// <remarks>
        /// Azure's <c>SUBSTRING</c> counts units and sends a split pair back as U+FFFD; the emulator's counts code points.
        /// Either agrees with Calcite's over a string with no surrogate.
        /// </remarks>
        [Fact]
        public void AWindowPushesOverAPathDeclaredFreeOfTheSurrogates()
        {
            Projected($"SUBSTRING({Of("b")} FROM 2 FOR 1)", out _).Should().Contain("SUBSTRING((IS_PRIMITIVE(c.b) ? c.b : null)");
            Projected($"SUBSTRING({Of("s")} FROM 2 FOR 1)", out var plan).Should().NotContain("SUBSTRING(");
            plan.Should().Contain("SUBSTRING(", "Calcite takes the window: " + plan);
        }

        // ── Case ──────────────────────────────────────────────────────────────────────────

        /// <remarks>
        /// Calcite maps ß to <c>SS</c> and the service leaves it, and both agree over ASCII. A path declared every unit but
        /// the surrogates holds ß, so its case is mapped in process; one declared printable ASCII pushes.
        /// </remarks>
        [Fact]
        public void ACaseMappingPushesOverAPathDeclaredAscii()
        {
            Projected($"UPPER({Of("a")})", out _).Should().Contain("UPPER((IS_PRIMITIVE(c.a) ? c.a : null))");
            Projected($"LOWER({Of("b")})", out var lowered).Should().NotContain("LOWER(");
            lowered.Should().Contain("LOWER(", "Calcite maps the case: " + lowered);
            Filtered($"UPPER({Of("s")}) = 'ASSB'", out var above).Should().NotContain("UPPER(");
            above.Should().Contain("ClrCursorFilter", "the comparison is Calcite's to make: " + above);
        }

        /// <remarks>
        /// <para>
        /// The case-insensitive match folds under the service's mapping, and lost <c>aßb</c> under <c>'%SS%'</c> on both
        /// targets, where no recheck can restore the row. Over a path nothing proves ASCII the fold and the pattern stay
        /// in process, and what goes down is the presence the comparison implies, no narrower than the strings.
        /// </para>
        /// <para>
        /// Over a path proved ASCII it is the service's own case-insensitive <c>CONTAINS</c>, exact
        /// (<c>CosmosPlannerTests.ACaseFoldedContainsIsTheServicesCaseInsensitiveContains</c>) — where the path is not
        /// declared a string. Declared one, no <c>LIKE</c> over it is pushed yet, a missed pushdown <c>TODO.md</c> records.
        /// </para>
        /// </remarks>
        [Fact]
        public void ACaseFoldUnderLikeOverAStringNotProvedAsciiStaysInProcess()
        {
            var weak = Filtered($"UPPER({Of("s")}) LIKE '%SS%'", out var above);

            weak.Should().NotContain("CONTAINS(").And.NotContain("UPPER(").And.Contain("IS_DEFINED(c.s)");
            above.Should().Contain("ClrCursorFilter", "the fold and the pattern are Calcite's to apply: " + above);
        }

        /// <remarks>
        /// What a filter's own values say of a path's characters is a fact of the theory too, in the stratum after it:
        /// beside <c>s IN ('ab', 'cd')</c>, <c>s</c> holds ASCII, and the fold over it pushes.
        /// </remarks>
        [Fact]
        public void AFiltersValuesProveWhatAFunctionAsks()
        {
            Filtered($"{Of("s")} IN ('ab', 'cd') AND UPPER({Of("s")}) = 'AB'", out var plan).Should().Contain("UPPER(c.s)");
            plan.Should().NotContain("ClrCursorFilter", "both conjuncts are exact: " + plan);
        }

        // ── Trims ─────────────────────────────────────────────────────────────────────────

        /// <remarks>
        /// The service trims white space and Calcite U+0020 alone. A path declared free of what the service trims beside a
        /// space pushes, whatever else it holds; one declared every unit but the surrogates may hold a tab or U+00A0, and
        /// is trimmed in process.
        /// </remarks>
        [Fact]
        public void ATrimPushesOverAPathDeclaredFreeOfWhatTheServiceTrims()
        {
            Projected($"TRIM({Of("w")})", out _).Should().Contain("TRIM((IS_PRIMITIVE(c.w) ? c.w : null))");
            Projected($"TRIM(LEADING ' ' FROM {Of("a")})", out _).Should().Contain("LTRIM((IS_PRIMITIVE(c.a) ? c.a : null))");
            Filtered($"TRIM({Of("b")}) = 'a b'", out var above).Should().NotContain("TRIM(");
            above.Should().Contain("ClrCursorFilter", "the comparison is Calcite's to make: " + above);
        }

        // ── What asks nothing, and what cannot be sent ────────────────────────────────────

        /// <remarks>
        /// Azure's <c>INDEX_OF</c> counts UTF-16 units, as Calcite's <c>POSITION</c> does, so it pushes over a string
        /// nothing is declared of; the emulator counts code points, a difference recorded and not declined.
        /// </remarks>
        [Fact]
        public void PositionPushesOverAnyString()
        {
            Projected($"POSITION('b' IN {Of("s")})", out _).Should().Contain("(INDEX_OF((IS_PRIMITIVE(c.s) ? c.s : null), @p0) + 1)");
        }

        /// <remarks>
        /// A lone surrogate is refused by the SDK's UTF-8 encoder before the statement is sent. Only a <c>U&amp;</c>
        /// literal holds one; the position is found in process, and the path beneath it still goes down.
        /// </remarks>
        [Fact]
        public void ALiteralHoldingALoneSurrogateStaysInProcess()
        {
            var sql = Projected($"POSITION(U&'\\D83D' IN {Of("s")})", out var plan);

            sql.Should().NotContain("INDEX_OF").And.Contain("c.s");
            plan.Should().Contain("POSITION(", "Calcite finds it: " + plan);
        }

    }

}
