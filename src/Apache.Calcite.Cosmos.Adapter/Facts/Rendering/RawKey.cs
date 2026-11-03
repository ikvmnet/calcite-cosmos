using System;

using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The key of a raw column's truths (h12): a raw reading, the kind of value a cell holds, the test, and the class of
    /// the literal beside it (<see cref="CosmosValueClass.None"/> for the null test), which says both whether a column
    /// raw beside strings alone is read and whether the literal is of the value's type.
    /// </summary>
    internal readonly record struct RawKey(string Reader, CosmosValueClass Kind, CosmosServiceTest Test, CosmosValueClass Operand) : IComparable<RawKey>
    {

        /// <summary>The key, as a rule computes one.</summary>
        public static RawKey Of(string reader, CosmosValueClass kind, CosmosServiceTest test, CosmosValueClass operand) => new(reader, kind, test, operand);

        /// <summary>Whether the literal is of another type than the value: across, where no column compares the two as one type.</summary>
        public bool Across => TypeOf(Kind) != TypeOf(Operand);

        /// <summary>A boolean is one type, and so is a structure.</summary>
        static CosmosValueClass TypeOf(CosmosValueClass c) => c switch { CosmosValueClass.False => CosmosValueClass.True, CosmosValueClass.Array => CosmosValueClass.Object, _ => c };

        /// <summary>
        /// The key a raw reading's test reads on a cell beside a literal: none for a cell of no one kind, a null read as one
        /// with an absent path, or for a test no column answers.
        /// </summary>
        public static RawKey? On(string reader, PathCell cell, string test, string? literal)
        {
            CosmosValueClass? kind = cell switch
            {
                PathCell.Absent or PathCell.Null => CosmosValueClass.Null,
                PathCell.Point { Value: var v } when JsonValues.TypeOf(v) != NullType => ClassOf(v),
                PathCell.Strings => CosmosValueClass.String,
                PathCell.Numbers => CosmosValueClass.Number,
                PathCell.Structure { Type: var t } => t == ArrayType ? CosmosValueClass.Array : CosmosValueClass.Object,
                _ => null,
            };
            CosmosServiceTest? asked = test switch
            {
                PredicateAtom.Equal => CosmosServiceTest.Equal,
                PredicateAtom.Less => CosmosServiceTest.Less,
                PredicateAtom.LessOrEqual => CosmosServiceTest.LessOrEqual,
                PredicateAtom.Greater => CosmosServiceTest.Greater,
                PredicateAtom.GreaterOrEqual => CosmosServiceTest.GreaterOrEqual,
                PredicateAtom.NullTest => CosmosServiceTest.IsNull,
                _ => null,
            };
            return kind is { } k && asked is { } a ? new(reader, k, a, literal is null ? CosmosValueClass.None : ClassOf(literal)) : null;
        }

        /// <summary>The class of a value in <see cref="JsonValues"/>' spelling.</summary>
        static CosmosValueClass ClassOf(string value) => JsonValues.TypeOf(value) switch
        {
            StringType => CosmosValueClass.String,
            IntegerType or FractionType => CosmosValueClass.Number,
            BooleanType => value == JsonValues.Of(true) ? CosmosValueClass.True : CosmosValueClass.False,
            ObjectType => CosmosValueClass.Object,
            ArrayType => CosmosValueClass.Array,
            _ => CosmosValueClass.Null,
        };

        /// <inheritdoc />
        public int CompareTo(RawKey other) => string.CompareOrdinal(Reader, other.Reader) is var r and not 0 ? r
            : Kind != other.Kind ? Kind.CompareTo(other.Kind) : Test != other.Test ? Test.CompareTo(other.Test) : Operand.CompareTo(other.Operand);

        /// <summary>The key spelled <c>reader/kind/test/operand</c>.</summary>
        public override string ToString() => Reader + "/" + Kind + "/" + Test + "/" + Operand;

    }

}
