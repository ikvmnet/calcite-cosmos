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
            (CosmosValueClass.String, FunctionValueClass.WithinRepetitionBound, FunctionValueClass.WithinRepetitionBound),
            (CosmosValueClass.String, FunctionValueClass.PastRepetitionBound, FunctionValueClass.PastRepetitionBound),
        ];

        /// <summary>
        /// The atoms a call's result is keyed by where its function's rows key one (<see cref="FunctionKey.Result"/>): a
        /// string's length against <see cref="RepetitionBound"/>, apart from the atoms an operand is keyed by, which
        /// <see cref="FunctionValueClass.Any"/> holds.
        /// </summary>
        public static readonly IReadOnlyList<(CosmosValueClass Kind, FunctionValueClass Atom)> ResultAtoms =
        [
            (CosmosValueClass.String, FunctionValueClass.WithinRepetitionBound),
            (CosmosValueClass.String, FunctionValueClass.PastRepetitionBound),
        ];

        /// <summary>
        /// The most code points a result of <see cref="FunctionValueClass.WithinRepetitionBound"/> holds, inclusive: what
        /// Azure's <c>REPLICATE</c> makes before it answers undefined (<c>Service.ReplicateStopsAtTenThousandCodePoints</c>,
        /// <c>ab</c> 5,000 times defined and 5,001 times not). The class's definition, as 1 is <see cref="FunctionValueClass.AtLeastOne"/>'s;
        /// which target answers what past it is the rows'.
        /// </summary>
        public const long RepetitionBound = 10_000;

        /// <summary>
        /// The atoms of a result a string of at most so many code points is in, every one where nothing bounds it: a test of
        /// the class on a concrete bound, a primitive (d).
        /// </summary>
        public static IEnumerable<FunctionValueClass> ResultAtomsOf(long? most) => most is long m && m <= RepetitionBound
            ? [FunctionValueClass.WithinRepetitionBound]
            : ResultAtoms.Select(a => a.Atom);

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
            FunctionValueClass.WithinRepetitionBound => IsString(value) && CodePoints(StringOf(value)) <= RepetitionBound,
            FunctionValueClass.PastRepetitionBound => IsString(value) && CodePoints(StringOf(value)) > RepetitionBound,
            _ => true,
        };

        /// <summary>The code points a string holds, a pair one and a lone surrogate one, as the service counts them.</summary>
        public static long CodePoints(string text)
        {
            var count = 0L;
            for (var i = 0; i < text.Length; i++, count++)
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;

            return count;
        }

        // A number's test of its value, read exactly; one held approximately passes every test, as AtLeast and AtMost keep it.
        static bool Number(string value, Func<decimal, bool> test) => IsNumber(value) && (TryNumberOf(value, out var x) == false || test(x));

    }

}
