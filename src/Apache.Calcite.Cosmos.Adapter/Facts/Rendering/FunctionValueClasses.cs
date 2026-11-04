using System.Collections.Generic;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The classes of value a function row may be keyed by (<see cref="FunctionValueClass"/>), and the one test of each on a
    /// concrete value: a primitive, (d). Which classes an operand's keys are split by is the rows' (<c>RenderingImage.Classed</c>).
    /// </summary>
    internal static class FunctionValueClasses
    {

        /// <summary>The families, each a partition of its kind: a number's sign, a number against 1, a string's emptiness.</summary>
        public static readonly IReadOnlyList<IReadOnlyList<FunctionValueClass>> Families =
        [
            [FunctionValueClass.Negative, FunctionValueClass.Zero, FunctionValueClass.Positive],
            [FunctionValueClass.BelowOne, FunctionValueClass.AtLeastOne],
            [FunctionValueClass.Empty, FunctionValueClass.NonEmpty],
        ];

        /// <summary>Whether a value, in <see cref="Saturation.JsonValues"/>' spelling, is of a class; every value is of <see cref="FunctionValueClass.Any"/>.</summary>
        public static bool Holds(FunctionValueClass @class, string value) => @class switch
        {
            FunctionValueClass.Negative => IsNumber(value) && NumberOf(value) < 0,
            FunctionValueClass.Zero => IsNumber(value) && NumberOf(value) == 0,
            FunctionValueClass.Positive => IsNumber(value) && NumberOf(value) > 0,
            FunctionValueClass.BelowOne => IsNumber(value) && NumberOf(value) < 1,
            FunctionValueClass.AtLeastOne => IsNumber(value) && NumberOf(value) >= 1,
            FunctionValueClass.Empty => IsString(value) && StringOf(value).Length == 0,
            FunctionValueClass.NonEmpty => IsString(value) && StringOf(value).Length > 0,
            _ => true,
        };

    }

}
