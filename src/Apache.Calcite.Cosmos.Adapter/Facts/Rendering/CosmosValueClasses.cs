using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>The class the rows key a value, a kind of value or a cell by: one mapping for every reader of the rows.</summary>
    internal static class CosmosValueClasses
    {

        /// <summary>The class of a kind of value, a JSON type or absent; <see cref="CosmosValueClass.None"/> for a boolean, which is two.</summary>
        public static CosmosValueClass OfKind(string kind) => kind switch
        {
            StringType => CosmosValueClass.String,
            IntegerType or FractionType => CosmosValueClass.Number,
            NullType => CosmosValueClass.Null,
            ObjectType => CosmosValueClass.Object,
            ArrayType => CosmosValueClass.Array,
            PathValues.AbsentKind => CosmosValueClass.Absent,
            _ => CosmosValueClass.None,
        };

        /// <summary>The class of a value in <see cref="JsonValues"/>' spelling.</summary>
        public static CosmosValueClass Of(string value) => TypeOf(value) == BooleanType
            ? value == JsonValues.Of(true) ? CosmosValueClass.True : CosmosValueClass.False
            : OfKind(TypeOf(value));

        /// <summary>The class of a cell; none for a cell of no one class.</summary>
        public static CosmosValueClass? Of(PathCell cell) => cell switch
        {
            PathCell.Absent => CosmosValueClass.Absent,
            PathCell.Null => CosmosValueClass.Null,
            PathCell.Structure s => OfKind(s.Type),
            PathCell.Strings => CosmosValueClass.String,
            PathCell.Numbers => CosmosValueClass.Number,
            PathCell.Point p => Of(p.Value),
            _ => null,
        };

    }

}
