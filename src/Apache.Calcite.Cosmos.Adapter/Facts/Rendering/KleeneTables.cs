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
    /// atom leaves a junction to its sibling, exactly; and the position beneath a negation. <c>Range</c> has no rows yet.
    /// </summary>
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
