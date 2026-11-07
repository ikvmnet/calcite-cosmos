using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

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

        /// <summary>The classes <see cref="Of(PathCell)"/> gives a cell: each one a side of a pair may hold.</summary>
        public static ValueSet<CosmosValueClass> Kinds { get; } = ValueSet.Of(CosmosValueClass.Absent, CosmosValueClass.Null, CosmosValueClass.True, CosmosValueClass.False,
            CosmosValueClass.Number, CosmosValueClass.String, CosmosValueClass.Object, CosmosValueClass.Array);

        /// <summary>The class of a truth value of the IR as its literal spells it: <c>true</c>, <c>false</c>, and any other undefined.</summary>
        public static CosmosValueClass OfTruth(string truth) => truth switch
        {
            "true" => CosmosValueClass.True,
            "false" => CosmosValueClass.False,
            _ => CosmosValueClass.Undefined,
        };

        /// <summary>
        /// Each class a constant is of, beside the constant: TRUE's <c>true</c>, FALSE's <c>false</c>, as <see cref="Of(string)"/>
        /// reads each boolean. What S's truth-value rule sends a truth value of the class as; UNKNOWN's class has none.
        /// </summary>
        public static IReadOnlyList<(CosmosValueClass Class, bool Constant)> Constants { get; } = [.. new[] { true, false }.Select(b => (Of(JsonValues.Of(b)), b))];

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
