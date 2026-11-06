using System;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The key of a function row (F2): a function by the name the IR reads it by, the operand the row is of by the loader's
    /// ordinals (<see cref="Every"/> where the function reads each alike, <see cref="Literal"/> where a literal operand is
    /// keyed apart from a stored one), that operand's kind, for a string one band of its
    /// characters (<see cref="JsonCharacterBand.None"/> for every string and for any other kind), and the class of its
    /// value the row was measured over (<see cref="FunctionValueClass.Any"/> where the row names none). A result key is
    /// the same struct, with the result's kind and band.
    /// </summary>
    internal readonly record struct FunctionKey(string Function, long Ordinal, CosmosValueClass Kind, JsonCharacterBand Band, FunctionValueClass Value) : IComparable<FunctionKey>
    {

        /// <summary>A function row's operand standing for every operand: a concatenation and an arithmetic operator read each.</summary>
        public const long Every = -1;

        /// <summary>
        /// A function row's operand standing for a literal operand, whatever its ordinal: one the statement sends as a value
        /// of its own, which a function's rows key apart where they were measured over a stored operand, the literal beside
        /// it held fixed (a concatenation's, a repetition's). A literal is keyed at it as well as at its ordinal and at
        /// <see cref="Every"/>, each where the rows name it.
        /// </summary>
        public const long Literal = -2;

        /// <summary>The key, as a rule computes one.</summary>
        public static FunctionKey Of(string function, long ordinal, CosmosValueClass kind, JsonCharacterBand band, FunctionValueClass value = FunctionValueClass.Any) =>
            new(function, ordinal, kind, band, value);

        /// <summary>The key's function, as a rule reads it.</summary>
        public static string FunctionOf(FunctionKey key) => key.Function;

        /// <summary>The key's kind, as a rule reads it.</summary>
        public static CosmosValueClass KindOf(FunctionKey key) => key.Kind;

        /// <summary>The key's class of value, as a rule reads it.</summary>
        public static FunctionValueClass ValueOf(FunctionKey key) => key.Value;

        /// <summary>The key with another class of value, as a rule makes one.</summary>
        public static FunctionKey WithValue(FunctionKey key, FunctionValueClass value) => key with { Value = value };

        /// <summary>By function, kind and band as before the operand was keyed, then by operand and class of value.</summary>
        public int CompareTo(FunctionKey other) =>
            string.CompareOrdinal(Function, other.Function) is var f and not 0 ? f
            : Kind != other.Kind ? Kind.CompareTo(other.Kind)
            : Band != other.Band ? Band.CompareTo(other.Band)
            : Ordinal != other.Ordinal ? Ordinal.CompareTo(other.Ordinal)
            : Value.CompareTo(other.Value);

        /// <summary>The key spelled <c>function/operand/kind/band</c>, and <c>/class</c> where it names one, as a run record names it.</summary>
        public override string ToString() =>
            Function + "/" + (Ordinal == Every ? "each" : Ordinal == Literal ? "literal" : Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "/" + Kind + "/" + Band
            + (Value == FunctionValueClass.Any ? "" : "/" + Value);

    }

}
