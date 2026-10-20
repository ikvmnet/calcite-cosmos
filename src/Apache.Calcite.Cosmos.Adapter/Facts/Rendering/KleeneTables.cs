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
    /// handed, which a set's measured connective rows must match for S to compose through that connective; the class of a
    /// cell on which an atom leaves a junction to its sibling; and what a position and a side of a junction read as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The service is handed values, Calcite truths alone.</b> The service reads a path that holds no boolean as unknown
    /// inside a connective (<c>Service.ConnectivesReadNonBooleansAsUnknown</c>), so each service target is expected to
    /// answer Kleene's over the six classes of such a value as well as the three truths; Calcite's connectives take
    /// booleans, so it is expected over the truths. A set is expected what every target it serves is.
    /// </para>
    /// <para>
    /// <b>An identity is exact.</b> A side whose cell is <c>(T,t)</c> leaves a conjunction to its sibling on both sides, and
    /// <c>(F,f)</c> a disjunction: nothing else may be given there. <c>Range</c> has no rows yet, so it is never licensed.
    /// </para>
    /// </remarks>
    internal static class KleeneTables
    {

        static readonly CosmosValueClass[] Booleans = [True, False, Undefined];

        static readonly CosmosValueClass[] Operands = [True, False, Undefined, Absent, Null, Number, CosmosValueClass.String, Object, CosmosValueClass.Array];

        /// <summary>Kleene's answer on each key, for each target that may be handed its operands.</summary>
        public static IReadOnlyList<(RenderingTarget, MeasuredKey, CosmosAnswer)> Rows { get; } =
            [.. Over(RenderingTarget.Emulator, Operands), .. Over(RenderingTarget.Azure, Operands), .. Over(RenderingTarget.Calcite, Booleans)];

        /// <summary>The class of a cell on which a side leaves each junction to its sibling.</summary>
        public static IReadOnlyDictionary<CosmosServiceTest, ValueSet<CellClass>> Identity { get; } = new Dictionary<CosmosServiceTest, ValueSet<CellClass>>
        {
            [And] = ValueSet.Of(new CellClass(Truths.True, ServiceOutcomes.True)),
            [Or] = ValueSet.Of(new CellClass(Truths.False, ServiceOutcomes.False)),
        };

        /// <summary>The position beneath a negation.</summary>
        public static IReadOnlyList<(RenderPosition, RenderPosition)> Opposite { get; } = [(RenderPosition.Pos, RenderPosition.Neg), (RenderPosition.Neg, RenderPosition.Pos)];

        /// <summary>The connective each side of a junction stands under.</summary>
        public static IReadOnlyDictionary<JunctionSide, CosmosServiceTest> ConnectiveOf { get; } = new Dictionary<JunctionSide, CosmosServiceTest>
        {
            [JunctionSide.AndLeft] = And,
            [JunctionSide.AndRight] = And,
            [JunctionSide.OrLeft] = Or,
            [JunctionSide.OrRight] = Or,
        };

        static IEnumerable<(RenderingTarget, MeasuredKey, CosmosAnswer)> Over(RenderingTarget target, CosmosValueClass[] operands) =>
            operands.SelectMany(x => operands.SelectMany(y => new[] { And, Or }.Select(c => (target, MeasuredKey.Of(c, x, y), Answer(c, x, y))))
                .Prepend((target, MeasuredKey.Of(Not, None, x), Answer(Not, x, x))));

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
