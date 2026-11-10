using System;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using org.apache.calcite.plan;
using org.apache.calcite.plan.hep;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rel.rules;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What Calcite's <c>CASE</c>, its truth tests and <c>COALESCE</c> answer, and that a host keeps a <c>CASE</c> whose
    /// arm raises off its condition: the Calcite side of every <c>case</c> the rendering graph reads.
    /// </summary>
    /// <remarks>
    /// The operands are read from documents in a scanned table (<see cref="MaterialisedTable"/>), so nothing is folded at
    /// planning: <c>RETURNING BOOLEAN</c> over a boolean or an absent path, and <c>RETURNING INTEGER</c> over a string,
    /// which raises. Pairs of <c>a</c> and <c>b</c> are TT, TF, TU, FT, FF, FU, UT, UF, UU, a letter a row.
    /// </remarks>
    public class CalciteCaseMeasurementTests
    {

        const string A = "JSON_VALUE(\"d\", '$.a' RETURNING BOOLEAN)";
        const string B = "JSON_VALUE(\"d\", '$.b' RETURNING BOOLEAN)";
        const string Raising = "(JSON_VALUE(\"d\", '$.r' RETURNING INTEGER) = 1)";

        static readonly string?[] Truths = ["true", "false", null];

        static MaterialisedTable Pairs() => MaterialisedTable.Of(Truths.SelectMany(a => Truths.Select(b => Document(("a", a), ("b", b), ("r", "\"x\"")))));

        static MaterialisedTable Singles() => MaterialisedTable.Of(Truths.Select(a => Document(("a", a), ("r", "\"x\""))));

        static string Document(params (string Name, string? Json)[] members) =>
            "{" + string.Join(",", members.Where(m => m.Json is not null).Select(m => $"\"{m.Name}\":{m.Json}")) + "}";

        /// <summary>
        /// <c>CASE</c> takes the arm of the first condition that is TRUE, a FALSE or UNKNOWN one passing to the next, and the
        /// <c>ELSE</c> where none is, NULL without one; an arm not taken is not evaluated.
        /// </summary>
        [Fact, CosmosPins("Calcite.CaseTakesTheFirstTrueArm")]
        public void ACaseTakesTheFirstTrueArmAndEvaluatesNoOther()
        {
            using var connection = MaterialisedTable.Open();
            Pairs().Ask(connection, $"CASE WHEN {A} THEN TRUE WHEN {B} THEN FALSE ELSE NULL END", $"CASE WHEN {A} THEN TRUE WHEN {B} THEN FALSE END",
                $"CASE WHEN {A} THEN {B} ELSE NOT {B} END").Should().Equal("TTTFUUFUU", "TTTFUUFUU", "TFUFTUFTU");

            // The row S's ternary licence reads: the then-arm where the condition is TRUE, the else-arm where FALSE or UNKNOWN.
            var row = CosmosAxioms.Get("Calcite.CaseTakesTheFirstTrueArm").Outcomes!.Single(o => o.Test == CosmosServiceTest.Choose).Cells.ToDictionary(c => c.Cell, c => c.Answer.ToString()[0]);
            Singles().Ask(connection, $"CASE WHEN {A} THEN TRUE ELSE FALSE END").Should().Equal(
                string.Concat(new[] { CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Undefined }.Select(c => row[c])));

            // An arm off its condition raises nowhere, the condition FALSE or UNKNOWN as TRUE.
            MaterialisedTable.Of([Document(("a", "true"), ("r", "\"x\""))]).Ask(connection, $"CASE WHEN {A} THEN TRUE ELSE {Raising} END").Should().Equal("T");
            MaterialisedTable.Of([Document(("a", "false"), ("r", "\"x\"")), Document(("r", "\"x\""))]).Ask(connection, $"CASE WHEN {A} THEN {Raising} ELSE FALSE END").Should().Equal("FF");
        }

        /// <summary>
        /// A <c>CASE</c> whose arm raises off its condition is kept as a <c>CASE</c>: through the planner a connection runs,
        /// in a <c>WHERE</c> and projected; through a simplifying <c>RelBuilder</c>; and through Hep's reduction of a filter.
        /// None makes it the eager <c>AND</c> or <c>OR</c> that would raise, so the guard still guards.
        /// </summary>
        [Fact, CosmosPins("Calcite.CaseWithAnArmThatRaisesIsKept")]
        public void ACaseWhoseArmRaisesIsKept()
        {
            using var connection = MaterialisedTable.Open();
            var guarded = MaterialisedTable.Of([Document(("a", "false"), ("r", "\"x\"")), Document(("r", "\"x\"")), Document(("a", "true"), ("r", "1"))]);
            guarded.Keeps(connection, $"CASE WHEN {A} THEN {Raising} ELSE FALSE END").Should().Equal(2);
            guarded.Keeps(connection, $"CASE WHEN {A} THEN {Raising} ELSE TRUE END").Should().Equal(0, 1, 2);
            guarded.Keeps(connection, $"NOT CASE WHEN {A} THEN {Raising} ELSE FALSE END").Should().Equal(0, 1);
            guarded.Ask(connection, $"CASE WHEN {A} THEN {Raising} ELSE FALSE END").Should().Equal("FFT");

            // The same shape as Rex: x <> 0 guarding 10 / x > 1, over a nullable integer column.
            var types = new org.apache.calcite.jdbc.JavaTypeFactoryImpl();
            var rex = new RexBuilder(types);
            var cluster = RelOptCluster.create(new org.apache.calcite.plan.volcano.VolcanoPlanner(), rex);
            var integer = types.createTypeWithNullability(types.createSqlType(SqlTypeName.INTEGER), true);
            var builder = RelFactories.LOGICAL_BUILDER.create(cluster, null);
            builder.values(new[] { "x" }, java.lang.Integer.valueOf(0), java.lang.Integer.valueOf(5));
            var x = rex.makeCast(integer, builder.field(0));
            var condition = rex.makeCall(SqlStdOperatorTable.CASE,
                rex.makeCall(SqlStdOperatorTable.NOT_EQUALS, x, rex.makeExactLiteral(java.math.BigDecimal.ZERO)),
                rex.makeCall(SqlStdOperatorTable.GREATER_THAN, rex.makeCall(SqlStdOperatorTable.DIVIDE, rex.makeExactLiteral(java.math.BigDecimal.TEN), x), rex.makeExactLiteral(java.math.BigDecimal.ONE)),
                rex.makeLiteral(false));

            var filtered = builder.filter(condition).build();
            ((LogicalFilter)filtered).getCondition().getKind().name().Should().Be("CASE", "a simplifying RelBuilder keeps it");

            var hep = new HepPlanner(new HepProgramBuilder().addRuleInstance(CoreRules.FILTER_REDUCE_EXPRESSIONS).build());
            hep.setRoot(filtered);
            var reduced = hep.findBestExp();
            (reduced is LogicalFilter { } f ? f.getCondition().getKind().name() : reduced.getRelTypeName()).Should().Be("CASE", "Hep's reduction of the filter keeps it");
        }

        /// <summary>
        /// The truth tests are cases: <c>x IS TRUE</c> is TRUE where <c>x</c> is and FALSE elsewhere, <c>IS NOT TRUE</c> its
        /// negation, <c>IS FALSE</c> and <c>IS NOT FALSE</c> alike for FALSE, <c>IS NULL</c> and <c>IS UNKNOWN</c> over a
        /// predicate TRUE where it is unknown; <c>x = TRUE</c> and <c>x &lt;&gt; FALSE</c> are <c>x</c>, and <c>x = FALSE</c>
        /// is <c>NOT x</c>.
        /// </summary>
        [Fact, CosmosPins("Calcite.TruthTestsAreCases")]
        public void TheTruthTestsAreCases()
        {
            using var connection = MaterialisedTable.Open();
            var singles = Singles();
            (string Test, string Case, string Answers)[] tests =
            [
                ($"{A} IS TRUE", $"CASE WHEN {A} THEN TRUE ELSE FALSE END", "TFF"),
                ($"{A} IS NOT TRUE", $"CASE WHEN {A} THEN FALSE ELSE TRUE END", "FTT"),
                ($"{A} IS FALSE", $"CASE WHEN NOT {A} THEN TRUE ELSE FALSE END", "FTF"),
                ($"{A} IS NOT FALSE", $"CASE WHEN NOT {A} THEN FALSE ELSE TRUE END", "TFT"),
                ($"({A}) IS NULL", $"CASE WHEN {A} THEN FALSE WHEN NOT {A} THEN FALSE ELSE TRUE END", "FFT"),
                ($"{A} IS UNKNOWN", $"CASE WHEN {A} THEN FALSE WHEN NOT {A} THEN FALSE ELSE TRUE END", "FFT"),
                ($"{A} = TRUE", A, "TFU"),
                ($"{A} <> FALSE", A, "TFU"),
                ($"{A} = FALSE", $"NOT {A}", "FTU"),
            ];

            foreach (var (test, spelled, answers) in tests)
                singles.Ask(connection, test, spelled).Should().Equal([answers, answers], test);

            Pairs().Ask(connection, $"({A} AND {B}) IS TRUE", $"({A} OR {B}) IS NOT FALSE").Should().Equal("TFFFFFFFF", "TTTTFTTTT");
        }

        /// <summary>
        /// <c>COALESCE</c> is the case of its first operand not null: <c>COALESCE(p, FALSE)</c> is TRUE where <c>p</c> is and
        /// FALSE elsewhere, and an operand after one not null is not evaluated.
        /// </summary>
        [Fact, CosmosPins("Calcite.CoalesceIsACase")]
        public void CoalesceIsACase()
        {
            using var connection = MaterialisedTable.Open();
            Singles().Ask(connection, $"COALESCE({A}, FALSE)", $"COALESCE({A}, TRUE)", $"CASE WHEN {A} IS NOT NULL THEN {A} ELSE FALSE END").Should().Equal("TFF", "TFT", "TFF");
            Pairs().Ask(connection, $"COALESCE({A}, {B})").Should().Equal("TTTFFFTFU");
            MaterialisedTable.Of([Document(("a", "true"), ("r", "\"x\"")), Document(("a", "false"), ("r", "\"x\""))]).Ask(connection, $"COALESCE({A}, {Raising})").Should().Equal("TF");
        }

    }

}
