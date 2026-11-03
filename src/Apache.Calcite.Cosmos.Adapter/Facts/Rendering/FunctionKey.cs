using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The key of a function row: a function by the name the IR reads it by, over an operand of one kind and, for a
    /// string, one band of characters (<see cref="JsonCharacterBand.None"/> for ASCII and for any other kind). A result
    /// key is the same struct, with the result's kind and band.
    /// </summary>
    internal readonly record struct FunctionKey(string Function, CosmosValueClass Kind, JsonCharacterBand Band) : IComparable<FunctionKey>
    {

        /// <summary>The key, as a rule computes one.</summary>
        public static FunctionKey Of(string function, CosmosValueClass kind, JsonCharacterBand band) => new(function, kind, band);

        /// <summary>The key's function, as a rule reads it.</summary>
        public static string FunctionOf(FunctionKey key) => key.Function;

        /// <inheritdoc />
        public int CompareTo(FunctionKey other) =>
            string.CompareOrdinal(Function, other.Function) is var f and not 0 ? f : Kind != other.Kind ? Kind.CompareTo(other.Kind) : Band.CompareTo(other.Band);

        /// <summary>The key spelled <c>function/kind/band</c>, as a run record names it.</summary>
        public override string ToString() => Function + "/" + Kind + "/" + Band;

    }

}
