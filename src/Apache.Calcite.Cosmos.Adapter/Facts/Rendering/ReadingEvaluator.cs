using System;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The evaluator a row names for a reading (<c>calciteevaluator</c>, G7), with the SQL type it evaluates a value of where
    /// it takes one (<see cref="CalciteEvaluator.Typed"/>, <see cref="CalciteEvaluator.NumberCast"/>), and the kinds of value
    /// Calcite's type system says that type holds (<c>CosmosRendering.ReadTypeKinds</c>); none otherwise.
    /// </summary>
    /// <param name="Evaluator">The evaluator.</param>
    /// <param name="Type">The SQL type it evaluates a value of; empty where it takes none.</param>
    /// <param name="Kinds">The kinds of value the type holds, as the type system's families say; none where it takes no type.</param>
    internal readonly record struct ReadingEvaluator(CalciteEvaluator Evaluator, string Type, ValueSet<string> Kinds) : IComparable<ReadingEvaluator>
    {

        /// <inheritdoc />
        public int CompareTo(ReadingEvaluator other) => Evaluator != other.Evaluator ? Evaluator.CompareTo(other.Evaluator)
            : string.CompareOrdinal(Type, other.Type) is var byType and not 0 ? byType
            : string.CompareOrdinal(string.Join(",", Kinds), string.Join(",", other.Kinds));

        /// <summary>The evaluator spelled by its name, and its type in parentheses where it takes one.</summary>
        public override string ToString() => Type.Length == 0 ? Evaluator.ToString() : Evaluator + "(" + Type + ")";

    }

}
