using FluentAssertions;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// A number's text is what the service returns, which Calcite renders: the service holds an int64 as itself and any
    /// other number as its double (<c>Service.NumbersBeyondLongCompareAsDoubles</c>), and returns a whole number within a
    /// long as its digits however it was written (<c>Service.IntegralNumbersReadBackAsHeld</c>). So a declared
    /// <c>6.0000000000000001</c> reaches Calcite as <c>6</c> from the emulator, whose text is <c>'6'</c>, and a declared
    /// <c>0.10000000000000001</c> as <c>0.1</c>, whose text is <c>'0.1'</c>.
    /// </summary>
    /// <remarks>
    /// Each case was the empty relation, or a filter sent with no recheck, where Calcite answers rows: the facts graph read
    /// the declared spelling as the value. A cast of the text to <c>ANY</c> was read as the raw value, which the service
    /// compares as a number, where Calcite holds the text through it; it is now the cast it is, which no row measured.
    /// </remarks>
    public partial class CosmosDeclaredFactPlanningTests
    {

        const string Sixes = """{ "type": "object", "required": ["q"], "properties": { "q": { "enum": [6.0000000000000001] } } }""";
        const string Tenths = """{ "type": "object", "required": ["p"], "properties": { "p": { "enum": [0.10000000000000001, 0.25] } } }""";

        const string Hundred = """{ "type": "object", "required": ["n"], "properties": { "n": { "type": "integer", "minimum": 0, "maximum": 100 } } }""";

        const string PastADouble = """{ "type": "object", "required": ["v"], "properties": { "v": { "enum": [9007199254740993.0] } } }""";

        /// <summary>
        /// <c>RETURNING DOUBLE</c> over a declared <c>6.0000000000000001</c>, or a range of fractions holding one, is never the
        /// empty relation: the emulator returns the number as <c>6</c>, digits, over which Calcite fails, and Azure was not
        /// measured returning it, so the comparison may raise wherever it is FALSE.
        /// </summary>
        [Fact]
        public void AReturningDoubleOverAFractionReturnedAsDigitsIsNeverEmpty()
        {
            foreach (var (schema, sql) in new[]
            {
                (Sixes, """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.q' RETURNING DOUBLE) = 7"""),
                (Sixes, """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.q' RETURNING DOUBLE) IN (7, 8)"""),
                (Sixes, """SELECT COUNT(*) FROM items AS c WHERE JSON_VALUE(c."DOC", '$.q' RETURNING DOUBLE) = 7"""),
                ("""{ "type": "object", "required": ["q"], "properties": { "q": { "type": "number", "minimum": 5.9, "maximum": 5.99999999999999999 } } }""",
                    """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.q' RETURNING DOUBLE) = 7"""),
            })
            {
                var plan = PlanToCosmos(sql, Declaring(schema), out _);
                PlanText(plan).Should().NotContain(KeepsNothing, "Calcite fails on the 6 the emulator returns: " + PlanText(plan));
            }
        }

        /// <summary>
        /// An integer parse over integers its type holds is the empty relation where it keeps none of them: the service
        /// returns each as its digits however it was written, <c>30.0</c> as <c>30</c>, which <c>RETURNING INTEGER</c> and
        /// the integer cast of the text both read (<c>Service.IntegralNumbersReadBackAsHeld</c>). Where the declaration
        /// admits an integer an <c>INTEGER</c> does not hold, the cast fails on it, and the filter is sent.
        /// </summary>
        [Fact]
        public void AnIntegerParseOverIntegersItsTypeHoldsIsReduced()
        {
            foreach (var sql in new[]
            {
                """SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER) = 300""",
                """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.n' RETURNING INTEGER) = 300""",
                """SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS BIGINT) > 200""",
            })
            {
                var plan = PlanToCosmos(sql, Declaring(Hundred), out _);
                PlanText(plan).Should().Contain(KeepsNothing, "no integer from 0 to 100 satisfies it, and none fails the parse: " + PlanText(plan));
            }

            var wide = PlanToCosmos("""SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER) = 300""",
                Declaring("""{ "type": "object", "required": ["n"], "properties": { "n": { "type": "integer", "maximum": 100 } } }"""), out _);
            PlanText(wide).Should().NotContain(KeepsNothing, "the cast fails on an integer below an INTEGER's range: " + PlanText(wide));
        }

        /// <summary>
        /// A number written with a point past 2^53 is held by the emulator as its double, and returned as that double's
        /// digits, <c>9007199254740993.0</c> as <c>9007199254740992</c>, where Azure returns <c>9007199254740993</c>
        /// (<c>Service.IntegralNumbersReadBackAsHeld</c>): compared with 9007199254740992, by <c>RETURNING DECIMAL</c>, by
        /// <c>RETURNING BIGINT</c> or through the cast of the text to <c>BIGINT</c>, it may be equal, and the filter is never
        /// the empty relation.
        /// </summary>
        [Fact]
        public void ANumberTheEmulatorHoldsAsItsDoubleIsNeverEmpty()
        {
            foreach (var reading in new[]
            {
                """JSON_VALUE(c."DOC", '$.v' RETURNING DECIMAL)""",
                """JSON_VALUE(c."DOC", '$.v' RETURNING BIGINT)""",
                """CAST(JSON_VALUE(c."DOC", '$.v') AS BIGINT)""",
            })
            {
                var plan = PlanToCosmos($"""SELECT c."id" FROM items AS c WHERE {reading} = 9007199254740992""", Declaring(PastADouble), out _);
                PlanText(plan).Should().NotContain(KeepsNothing, "the emulator returns 9007199254740992: " + PlanText(plan));
            }
        }

        /// <summary>
        /// The text of a declared <c>6.0000000000000001</c> equals <c>'6'</c>, counted or not: sent with the numeric arm that
        /// finds the stored double, and rechecked.
        /// </summary>
        [Fact]
        public void TheTextOfANumberIsTheServicesSpelling()
        {
            var container = Declaring(Sixes);
            foreach (var sql in new[]
            {
                """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.q') = '6'""",
                """SELECT COUNT(*) FROM items AS c WHERE JSON_VALUE(c."DOC", '$.q') = '6'""",
            })
            {
                var plan = PlanToCosmos(sql, container, out _);
                PlanText(plan).Should().NotContain(KeepsNothing, "the service returns 6: " + PlanText(plan));
                PlanText(plan).Should().Contain("ClrCursorFilter", "and Calcite rechecks what is sent: " + PlanText(plan));
                Query(FindCosmos(plan), container).Sql.Should().Contain("(IS_NUMBER(c.q) AND (c.q = @p1))", "the stored double is found by its number");
            }
        }

        /// <summary>
        /// What a number's text is unequal to, through <c>&lt;&gt;</c> or <c>NOT</c>, is rechecked: <c>'0.1'</c> is the text
        /// of a declared <c>0.10000000000000001</c>, and <c>'6'</c> of a declared <c>6.0000000000000001</c>.
        /// </summary>
        [Fact]
        public void WhatANumbersTextIsUnequalToIsRechecked()
        {
            foreach (var (schema, sql) in new[]
            {
                (Tenths, """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.p') <> '0.1'"""),
                (Sixes, """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.q') <> '6'"""),
                (Tenths, """SELECT c."id" FROM items AS c WHERE NOT (JSON_VALUE(c."DOC", '$.p') = '0.1')"""),
            })
            {
                var plan = PlanToCosmos(sql, Declaring(schema), out _);
                PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));
                PlanText(plan).Should().Contain("ClrCursorFilter", "the service's comparison is not Calcite's: " + PlanText(plan));
            }
        }

        /// <summary>
        /// Where no form of the text's comparison is exact over a declared <c>0.10000000000000001</c>, the comparison stays in
        /// Calcite, counted, beside another, or through a cast to <c>ANY</c>, which holds the text, alone or cast back to text:
        /// nothing of it is sent.
        /// </summary>
        [Fact]
        public void ATextComparisonNoFormMakesExactStaysInCalcite()
        {
            foreach (var (schema, sql) in new[]
            {
                (Tenths, """SELECT COUNT(*) FROM items AS c WHERE JSON_VALUE(c."DOC", '$.p') = '0.1'"""),
                (Tenths, """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.p') = '0.1' OR JSON_VALUE(c."DOC", '$.p') = '9'"""),
                (Sixes, """SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.q') AS ANY) = '6'"""),
                (Tenths, """SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.p') AS ANY) = '0.1'"""),
                (Sixes, """SELECT c."id" FROM items AS c WHERE CAST(CAST(JSON_VALUE(c."DOC", '$.q') AS ANY) AS VARCHAR) = '6'"""),
            })
            {
                var container = Declaring(schema);
                var plan = PlanToCosmos(sql, container, out _);
                PlanText(plan).Should().NotContain(KeepsNothing, PlanText(plan));
                PlanText(plan).Should().Contain("ClrCursorFilter", "the comparison is Calcite's: " + PlanText(plan));
                Query(FindCosmos(plan), container).Sql.Should().Be("SELECT VALUE c FROM items c", "nothing of it is sent");
            }
        }

    }

}
