using System;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a test of a reading answers over every value of one class, whatever the value (<c>readtruths</c>, G7): the truths
    /// its read of the class means, or its operand's where a row says the test is the operand's.
    /// </summary>
    /// <param name="Kind">The class of the stored value.</param>
    /// <param name="Test">The test.</param>
    /// <param name="Truths">What it answers there.</param>
    internal readonly record struct ClassTruths(CosmosValueClass Kind, CosmosServiceTest Test, Truths Truths) : IComparable<ClassTruths>
    {

        /// <inheritdoc />
        public int CompareTo(ClassTruths other) => Kind != other.Kind ? Kind.CompareTo(other.Kind) : Test != other.Test ? Test.CompareTo(other.Test) : Truths.CompareTo(other.Truths);

        /// <summary>The answer spelled <c>kind:test=truths</c>.</summary>
        public override string ToString() => Kind + ":" + Test + "=" + (int)Truths;

    }

}
