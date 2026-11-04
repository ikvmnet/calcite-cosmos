using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The key of a measured row: what was asked, of what operand, over what class of value, and the band of characters
    /// the row was measured over (F1): <see cref="JsonCharacterBand.None"/> for a row that names none, which holds over
    /// every string of its class, and for every other kind.
    /// </summary>
    internal readonly record struct MeasuredKey(CosmosServiceTest Test, CosmosValueClass Operand, CosmosValueClass Cell, JsonCharacterBand Band = JsonCharacterBand.None) : IComparable<MeasuredKey>
    {

        /// <summary>The key of a row that names no band, as a rule computes one.</summary>
        public static MeasuredKey Of(CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell) => new(test, operand, cell);

        /// <summary>The key of a row measured over one band of characters.</summary>
        public static MeasuredKey Of(CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell, JsonCharacterBand band) => new(test, operand, cell, band);

        /// <inheritdoc />
        public int CompareTo(MeasuredKey other) =>
            Test != other.Test ? Test.CompareTo(other.Test) : Operand != other.Operand ? Operand.CompareTo(other.Operand) : Cell != other.Cell ? Cell.CompareTo(other.Cell) : Band.CompareTo(other.Band);

        /// <summary>The key spelled <c>test/operand/cell</c>, and <c>/band</c> where it names one, as a missing one is named.</summary>
        public override string ToString() => Test + "/" + Operand + "/" + Cell + (Band == JsonCharacterBand.None ? "" : "/" + Band);

    }

}
