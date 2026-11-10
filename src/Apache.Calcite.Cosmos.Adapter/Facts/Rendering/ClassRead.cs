using System;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a reading reads a stored value of one class as, one of its rows (<c>calcitereads</c>, <c>rawreads</c>, G7):
    /// <see cref="CalciteRead"/>'s answers.
    /// </summary>
    /// <param name="Kind">The class of the stored value.</param>
    /// <param name="Answer">What the reading reads it as.</param>
    internal readonly record struct ClassRead(CosmosValueClass Kind, string Answer) : IComparable<ClassRead>
    {

        /// <summary>One row's, as the rule joins it into a reading's.</summary>
        public static ValueSet<ClassRead> One(CosmosValueClass kind, string answer) => ValueSet.Of(new ClassRead(kind, answer));

        /// <inheritdoc />
        public int CompareTo(ClassRead other) => Kind != other.Kind ? Kind.CompareTo(other.Kind) : string.CompareOrdinal(Answer, other.Answer);

        /// <summary>The row spelled <c>kind=answer</c>.</summary>
        public override string ToString() => Kind + "=" + Answer;

    }

}
