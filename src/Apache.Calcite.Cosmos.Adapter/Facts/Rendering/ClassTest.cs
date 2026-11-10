using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A test of a reading the cell decides over one class of value, by what the reading reads it as (<c>readcompares</c>,
    /// G7): a comparison of a value read as itself, rendered or parsed, which the value read answers.
    /// </summary>
    /// <param name="Kind">The class of the stored value.</param>
    /// <param name="Test">The test.</param>
    /// <param name="Answer">What the reading reads the class as (<see cref="CalciteRead"/>).</param>
    internal readonly record struct ClassTest(CosmosValueClass Kind, CosmosServiceTest Test, string Answer) : IComparable<ClassTest>
    {

        /// <inheritdoc />
        public int CompareTo(ClassTest other) => Kind != other.Kind ? Kind.CompareTo(other.Kind) : Test != other.Test ? Test.CompareTo(other.Test) : string.CompareOrdinal(Answer, other.Answer);

        /// <summary>The test spelled <c>kind:test?answer</c>.</summary>
        public override string ToString() => Kind + ":" + Test + "?" + Answer;

    }

}
