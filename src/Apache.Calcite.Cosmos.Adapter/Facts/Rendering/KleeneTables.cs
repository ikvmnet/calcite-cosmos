using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;
using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The logic composition rests on, as host data: what Kleene's connectives answer on every operand a target can be
    /// handed — the service values, among them the six classes of one that is no boolean, Calcite truths alone — which a
    /// set's measured connective rows must match for S to compose through that connective; the class of a cell on which an
    /// atom leaves a junction to its sibling, exactly; the position beneath a negation; and a range of one path answering
    /// as its two orderings do, which a set must have measured before S reads one whose bounds part the orders pointwise.
    /// </summary>
    internal static class KleeneTables
    {

        static readonly CosmosValueClass[] Booleans = [True, False, Undefined];

        static readonly CosmosValueClass[] Operands = [True, False, Undefined, Absent, Null, Number, CosmosValueClass.String, Object, CosmosValueClass.Array];

        /// <summary>Kleene's answer on each key, for each target that may be handed its operands.</summary>
        public static IReadOnlyList<(RenderingTarget, MeasuredKey, CosmosAnswer)> Rows { get; } =
            [.. Over(RenderingTarget.Emulator, Operands), .. Over(RenderingTarget.Azure, Operands), .. Over(RenderingTarget.Calcite, Booleans),
             .. Pointwise(RenderingTarget.Emulator), .. Pointwise(RenderingTarget.Azure),
             .. Ternary(RenderingTarget.Emulator, Operands), .. Ternary(RenderingTarget.Azure, Operands), .. Ternary(RenderingTarget.Calcite, Booleans),
             .. Constants(RenderingTarget.Emulator), .. Constants(RenderingTarget.Azure)];

        /// <summary>
        /// Each position and truth value an else-arm leaves its case's rows at as a condition that is not TRUE does under a
        /// junction (spec §5.1): FALSE and UNKNOWN keep no row at <c>Pos</c>, TRUE and UNKNOWN none beneath a <c>NOT</c>.
        /// </summary>
        public static IReadOnlyList<(RenderPosition, string)> AbsorbingElse { get; } =
            [(RenderPosition.Pos, "false"), (RenderPosition.Pos, "unknown"), (RenderPosition.Neg, "true"), (RenderPosition.Neg, "unknown")];

        /// <summary>Each position and truth value an else-arm keeps every row at that a condition not TRUE leaves: TRUE at <c>Pos</c>, FALSE beneath a <c>NOT</c>.</summary>
        public static IReadOnlyList<(RenderPosition, string)> OppositeElse { get; } = [(RenderPosition.Pos, "true"), (RenderPosition.Neg, "false")];

        /// <summary>The class of a cell on which a side leaves each junction to its sibling.</summary>
        public static IReadOnlyDictionary<CosmosServiceTest, ValueSet<CellClass>> Identity { get; } = new Dictionary<CosmosServiceTest, ValueSet<CellClass>>
        {
            [And] = ValueSet.Of(new CellClass(Truths.True, ServiceOutcomes.True)),
            [Or] = ValueSet.Of(new CellClass(Truths.False, ServiceOutcomes.False)),
        };

        /// <summary>The position beneath a negation.</summary>
        public static IReadOnlyList<(RenderPosition, RenderPosition)> Opposite { get; } = [(RenderPosition.Pos, RenderPosition.Neg), (RenderPosition.Neg, RenderPosition.Pos)];

        static IEnumerable<(RenderingTarget, MeasuredKey, CosmosAnswer)> Over(RenderingTarget target, CosmosValueClass[] operands) =>
            operands.SelectMany(x => operands.SelectMany(y => new[] { And, Or }.Select(c => (target, MeasuredKey.Of(c, x, y), Answer(c, x, y))))
                .Prepend((target, MeasuredKey.Of(Not, None, x), Answer(Not, x, x))));

        /// <summary>
        /// A range kept where a value lies between its bounds and kept by <c>NOT</c> where outside, whether they part the
        /// orders or not, and undefined over a value of another type or none, as both its orderings are.
        /// </summary>
        static IEnumerable<(RenderingTarget, MeasuredKey, CosmosAnswer)> Pointwise(RenderingTarget target) =>
            from bounds in new[] { Parted, Agreeing }
            from cell in new[] { (Between, CosmosAnswer.True), (Outside, CosmosAnswer.False) }
                .Concat(new[] { Absent, Null, True, False, Number, Object, CosmosValueClass.Array }.Select(kind => (kind, CosmosAnswer.Undefined)))
            select (target, MeasuredKey.Of(CosmosServiceTest.Range, bounds, cell.Item1), cell.Item2);

        /// <summary>
        /// A ternary takes its then-arm where its condition is TRUE and its else-arm over every other operand, as Calcite's
        /// <c>CASE</c> does: the answer <c>true</c> where it takes the then-arm, <c>false</c> where the else-arm.
        /// </summary>
        static IEnumerable<(RenderingTarget, MeasuredKey, CosmosAnswer)> Ternary(RenderingTarget target, CosmosValueClass[] operands) =>
            operands.Select(x => (target, MeasuredKey.Of(Choose, None, x), x == True ? CosmosAnswer.True : CosmosAnswer.False));

        /// <summary>A constant written as a condition is its own truth over every kind of value a path holds, and none.</summary>
        static IEnumerable<(RenderingTarget, MeasuredKey, CosmosAnswer)> Constants(RenderingTarget target) =>
            from constant in new[] { True, False }
            from cell in new[] { Absent, Null, True, False, Number, CosmosValueClass.String, Object, CosmosValueClass.Array }
            select (target, MeasuredKey.Of(Constant, constant, cell), constant == True ? CosmosAnswer.True : CosmosAnswer.False);

        /// <summary>Kleene's connectives, an operand that is no truth read as unknown.</summary>
        static CosmosAnswer Answer(CosmosServiceTest connective, CosmosValueClass x, CosmosValueClass y)
        {
            var (a, b) = (x is True or False ? x : Undefined, y is True or False ? y : Undefined);
            var answer = connective switch
            {
                Not => a == True ? False : a == False ? True : Undefined,
                And => a == False || b == False ? False : a == True && b == True ? True : Undefined,
                _ => a == True || b == True ? True : a == False && b == False ? False : Undefined,
            };

            return answer == True ? CosmosAnswer.True : answer == False ? CosmosAnswer.False : CosmosAnswer.Undefined;
        }

    }

}
