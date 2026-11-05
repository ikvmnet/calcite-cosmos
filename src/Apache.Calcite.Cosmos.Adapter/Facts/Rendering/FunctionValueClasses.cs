using System;
using System.Collections.Generic;
using System.Linq;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The classes of value a function row may be keyed by (<see cref="FunctionValueClass"/>): which atoms each class holds
    /// of a kind, data S reads (<c>valueclassholds</c>), and the one test of a class on a concrete value, a primitive (d).
    /// Which rows apply to a cell is S's: a measured key covers each atom its class holds, and a call's keys are its cells'
    /// atoms (<c>DESIGN.md</c>, <em>The foundation's rows</em>).
    /// </summary>
    internal static class FunctionValueClasses
    {

        /// <summary>
        /// The atoms each class holds of each kind of operand: a number's are its sign's and its place against 1 met
        /// (below 0, 0, above 0 and below 1, from 1), a string's its emptiness, and every other kind's its own
        /// <see cref="FunctionValueClass.Any"/>. A class holds itself where it is an atom.
        /// </summary>
        public static readonly IReadOnlyList<(CosmosValueClass Kind, FunctionValueClass Class, FunctionValueClass Atom)> Holding =
        [
            (CosmosValueClass.Number, FunctionValueClass.Any, FunctionValueClass.Negative),
            (CosmosValueClass.Number, FunctionValueClass.Any, FunctionValueClass.Zero),
            (CosmosValueClass.Number, FunctionValueClass.Any, FunctionValueClass.PositiveBelowOne),
            (CosmosValueClass.Number, FunctionValueClass.Any, FunctionValueClass.AtLeastOne),
            (CosmosValueClass.Number, FunctionValueClass.Negative, FunctionValueClass.Negative),
            (CosmosValueClass.Number, FunctionValueClass.Zero, FunctionValueClass.Zero),
            (CosmosValueClass.Number, FunctionValueClass.Positive, FunctionValueClass.PositiveBelowOne),
            (CosmosValueClass.Number, FunctionValueClass.Positive, FunctionValueClass.AtLeastOne),
            (CosmosValueClass.Number, FunctionValueClass.BelowOne, FunctionValueClass.Negative),
            (CosmosValueClass.Number, FunctionValueClass.BelowOne, FunctionValueClass.Zero),
            (CosmosValueClass.Number, FunctionValueClass.BelowOne, FunctionValueClass.PositiveBelowOne),
            (CosmosValueClass.Number, FunctionValueClass.PositiveBelowOne, FunctionValueClass.PositiveBelowOne),
            (CosmosValueClass.Number, FunctionValueClass.AtLeastOne, FunctionValueClass.AtLeastOne),
            (CosmosValueClass.String, FunctionValueClass.Any, FunctionValueClass.Empty),
            (CosmosValueClass.String, FunctionValueClass.Any, FunctionValueClass.NonEmpty),
            (CosmosValueClass.String, FunctionValueClass.Empty, FunctionValueClass.Empty),
            (CosmosValueClass.String, FunctionValueClass.NonEmpty, FunctionValueClass.NonEmpty),
            (CosmosValueClass.Absent, FunctionValueClass.Any, FunctionValueClass.Any),
            (CosmosValueClass.Null, FunctionValueClass.Any, FunctionValueClass.Any),
            (CosmosValueClass.True, FunctionValueClass.Any, FunctionValueClass.Any),
            (CosmosValueClass.False, FunctionValueClass.Any, FunctionValueClass.Any),
            (CosmosValueClass.Object, FunctionValueClass.Any, FunctionValueClass.Any),
            (CosmosValueClass.Array, FunctionValueClass.Any, FunctionValueClass.Any),
        ];

        /// <summary>The atoms of a kind: those <see cref="FunctionValueClass.Any"/> holds of it.</summary>
        public static IEnumerable<FunctionValueClass> AtomsOf(CosmosValueClass kind) =>
            Holding.Where(h => h.Kind == kind && h.Class == FunctionValueClass.Any).Select(h => h.Atom);

        /// <summary>
        /// Whether a value, in <see cref="Saturation.JsonValues"/>' spelling, is of a class; every value is of
        /// <see cref="FunctionValueClass.Any"/>, and a number held approximately, whose place no bound reads, of every class of a number.
        /// </summary>
        public static bool Holds(FunctionValueClass @class, string value) => @class switch
        {
            FunctionValueClass.Negative => Number(value, x => x < 0),
            FunctionValueClass.Zero => Number(value, x => x == 0),
            FunctionValueClass.Positive => Number(value, x => x > 0),
            FunctionValueClass.BelowOne => Number(value, x => x < 1),
            FunctionValueClass.AtLeastOne => Number(value, x => x >= 1),
            FunctionValueClass.PositiveBelowOne => Number(value, x => x > 0 && x < 1),
            FunctionValueClass.Empty => IsString(value) && StringOf(value).Length == 0,
            FunctionValueClass.NonEmpty => IsString(value) && StringOf(value).Length > 0,
            _ => true,
        };

        // A number's test of its value, read exactly; one held approximately passes every test, as AtLeast and AtMost keep it.
        static bool Number(string value, Func<decimal, bool> test) => IsNumber(value) && (TryNumberOf(value, out var x) == false || test(x));

    }

}
