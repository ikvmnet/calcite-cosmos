using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using org.apache.calcite.plan;
using org.apache.calcite.sql.fun;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// The one reader of strictness: Calcite's declared null policy and the measured row, one row an operator by the names
    /// the IR reads it by (<see cref="CosmosStrictness"/>), which the rendering graph's <c>strictop</c> lattice meets.
    /// </summary>
    public sealed class CosmosStrictnessTests
    {

        /// <summary>Whether S holds a name strict: the meet of its operators' rows, or none where no operator is read by it.</summary>
        static bool? Strict(string name) => ProbeRows.ProbeTheory.Theory.TryGet(ProbeRows.ProbeTheory.StrictOp.Of(name), out var strict) ? strict : null;

        /// <summary>
        /// The comparisons, <c>LIKE</c>, the arithmetic, the cast, <c>NOT</c>, <c>ITEM</c>, <c>FLOOR</c> and <c>CEIL</c>
        /// by Calcite's declared policy, a <c>TRIM</c> by each flag the loader spells it with, the membership the IR reads,
        /// and every function the measured row names are strict in S; what answers a value over a null, or decides null, is
        /// not.
        /// </summary>
        [Fact]
        public void TheDeclaredPolicyAndTheMeasuredRowNameTheStrictOperators()
        {
            string[] strict = ["=", "<>", "<", "<=", ">", ">=", "LIKE", "+", "-", "*", "/", "CAST", "NOT", "ITEM", "FLOOR", "CEIL", "TRIM BOTH", "TRIM LEADING", "TRIM TRAILING",
                PredicateWriter.In, .. CosmosAxioms.Get(CosmosStrictness.Functions).Subjects!];
            strict.Where(name => Strict(name) != true).Should().BeEmpty();
            new[] { "CONCAT_WS", "CONCAT", "COALESCE", "CASE", PredicateWriter.IsNull, "IS NOT NULL", "AND", "OR", PredicateWriter.IsDefined, "IS_STRING" }
                .Where(name => Strict(name) != false).Should().BeEmpty();
        }

        /// <summary>
        /// The IR names a call by its name alone, so a name one operator does not answer null for is strict for none: the
        /// reader writes Calcite's <c>EXTRACT</c> of a date strict, as Calcite declares it, and a library's <c>EXTRACT</c>
        /// of a path from XML not, and S's lattice holds the name not strict.
        /// </summary>
        [Fact]
        public void ANameAnotherOperatorDeclaresNothingForIsNotStrict()
        {
            Strong.policy(SqlStdOperatorTable.EXTRACT).Should().Be(Strong.Policy.ANY);
            CosmosStrictness.Operators.Where(o => o.Name == "EXTRACT").Select(o => o.Strict).Should().BeEquivalentTo([true, false]);
            Strict("EXTRACT").Should().BeFalse();
        }

    }

}
