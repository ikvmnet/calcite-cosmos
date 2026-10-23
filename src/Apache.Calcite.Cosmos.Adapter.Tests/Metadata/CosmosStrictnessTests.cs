using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using org.apache.calcite.plan;
using org.apache.calcite.sql.fun;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// The one reader of strictness: Calcite's declared null policy and the two measured rows, read into names in the
    /// IR's spelling (<see cref="CosmosStrictness"/>).
    /// </summary>
    public sealed class CosmosStrictnessTests
    {

        /// <summary>
        /// The comparisons, <c>LIKE</c>, the arithmetic, the cast, <c>NOT</c>, <c>ITEM</c>, <c>FLOOR</c> and <c>CEIL</c>
        /// by Calcite's declared policy, a <c>TRIM</c> by each flag the loader spells it with, and every function the
        /// measured row names are strict; what answers a value over a null, or decides null, is not.
        /// </summary>
        [Fact]
        public void TheDeclaredPolicyAndTheMeasuredRowNameTheStrictOperators()
        {
            CosmosStrictness.Operators.Should().Contain(["=", "<>", "<", "<=", ">", ">=", "LIKE", "+", "-", "*", "/", "CAST", "NOT", "ITEM", "FLOOR", "CEIL",
                "TRIM BOTH", "TRIM LEADING", "TRIM TRAILING"]);
            CosmosStrictness.Operators.Should().Contain(CosmosAxioms.Get(CosmosStrictness.Functions).Subjects!);
            CosmosStrictness.Operators.Should().NotContain(["CONCAT_WS", "CONCAT", "COALESCE", "CASE", "IS NULL", "IS NOT NULL", "AND", "OR", "IS_DEFINED", "IS_STRING"]);
        }

        /// <summary>
        /// The IR names a call by its name alone, so a name one operator does not answer null for is strict for none:
        /// Calcite declares its <c>EXTRACT</c> of a date strict, and a library's <c>EXTRACT</c> of a path from XML nothing.
        /// </summary>
        [Fact]
        public void ANameAnotherOperatorDeclaresNothingForIsNotStrict()
        {
            Strong.policy(SqlStdOperatorTable.EXTRACT).Should().Be(Strong.Policy.ANY);
            CosmosStrictness.Operators.Should().NotContain("EXTRACT");
        }

        /// <summary>The readings are the measured row's: every one the loader builds, in the atoms' spelling.</summary>
        [Fact]
        public void TheReadersAreTheMeasuredRows()
        {
            CosmosStrictness.Readers.Should().Equal(CosmosAxioms.Get(CosmosStrictness.Readings).Subjects!);
            CosmosStrictness.Readers.Should().HaveCount(18).And.Contain([PredicateAtom.TextReader, PredicateAtom.RawReader, PredicateAtom.TypedReader("BOOLEAN"),
                PredicateAtom.CastReader("DOUBLE", PredicateAtom.TextReader), PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader)]);
        }

    }

}
