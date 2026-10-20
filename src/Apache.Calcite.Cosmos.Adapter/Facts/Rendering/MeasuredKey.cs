using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The key of a measured row: what was asked, of what operand, over what class of value.
    /// </summary>
    internal readonly record struct MeasuredKey(CosmosServiceTest Test, CosmosValueClass Operand, CosmosValueClass Cell) : IComparable<MeasuredKey>
    {

        /// <summary>The key, as a rule computes one.</summary>
        public static MeasuredKey Of(CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell) => new(test, operand, cell);

        /// <inheritdoc />
        public int CompareTo(MeasuredKey other) =>
            Test != other.Test ? Test.CompareTo(other.Test) : Operand != other.Operand ? Operand.CompareTo(other.Operand) : Cell.CompareTo(other.Cell);

        /// <summary>The key spelled <c>test/operand/cell</c>, as a missing one is named.</summary>
        public override string ToString() => Test + "/" + Operand + "/" + Cell;

    }

}
