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
    /// atom leaves a junction to its sibling, exactly; the position beneath a negation; a range of one path answering
    /// as its two orderings do, which a set must have measured before S reads one whose bounds part the orders pointwise;
    /// two paths compared as a path and a literal, which a set must have measured before S reads a pair (G3); the truths a
    /// side that may fail stands for (G4); and the constants each junction holds as its identity or its absorbing element,
    /// and the shape a case with a constant else takes at each position (G4).
    /// </summary>
    internal static class KleeneTables
    {

        static readonly CosmosValueClass[] Booleans = [True, False, Undefined];

        static readonly CosmosValueClass[] Operands = [True, False, Undefined, Absent, Null, Number, CosmosValueClass.String, Object, CosmosValueClass.Array];

        /// <summary>
        /// What a test answers of a value a reading reads as SQL null, raises on, or reads as a value, where what it reads
        /// decides it (G5): <c>IS NULL</c> TRUE of a null and FALSE of a value, FALSE or a raise of a parse, a comparison
        /// UNKNOWN beside a null, and any test raising where the read does. A comparison of a value it reads is the cell's to
        /// decide (<see cref="Compares"/>), which no row here says.
        /// </summary>
        public static IReadOnlyList<(CosmosServiceTest Test, string Read, Truths Truths)> Reads { get; } =
        [
            (CosmosServiceTest.IsNull, CalciteRead.Itself, Truths.False), (CosmosServiceTest.IsNull, CalciteRead.Renders, Truths.False), (CosmosServiceTest.IsNull, CalciteRead.Null, Truths.True),
            (CosmosServiceTest.IsNull, CalciteRead.Raises, Truths.Raises), (CosmosServiceTest.IsNull, CalciteRead.Parses, Truths.False | Truths.Raises),
            .. from test in new[] { CosmosServiceTest.Equal, CosmosServiceTest.Less, CosmosServiceTest.LessOrEqual, CosmosServiceTest.Greater, CosmosServiceTest.GreaterOrEqual }
               from read in new[] { (CalciteRead.Null, Truths.Unknown), (CalciteRead.Raises, Truths.Raises) }
               select (test, read.Item1, read.Item2),
        ];

        /// <summary>
        /// The comparisons a cell decides of a value a reading reads as itself, renders or parses (G7): the value read, a
        /// literal's or a parameter's comparand, answers them, so no truth of the read alone does.
        /// </summary>
        public static IReadOnlyList<(CosmosServiceTest Test, string Read)> Compares { get; } =
        [
            .. from test in new[] { CosmosServiceTest.Equal, CosmosServiceTest.Less, CosmosServiceTest.LessOrEqual, CosmosServiceTest.Greater, CosmosServiceTest.GreaterOrEqual }
               from read in new[] { CalciteRead.Itself, CalciteRead.Renders, CalciteRead.Parses }
               select (test, read),
        ];

        /// <summary>Kleene's answer on each key, for each target that may be handed its operands.</summary>
        public static IReadOnlyList<(RenderingTarget, MeasuredKey, CosmosAnswer)> Rows { get; } =
            [.. Over(RenderingTarget.Emulator, Operands), .. Over(RenderingTarget.Azure, Operands), .. Over(RenderingTarget.Calcite, Booleans),
             .. Pointwise(RenderingTarget.Emulator), .. Pointwise(RenderingTarget.Azure),
             .. Ternary(RenderingTarget.Emulator, Operands), .. Ternary(RenderingTarget.Azure, Operands), .. Ternary(RenderingTarget.Calcite, Booleans),
             .. Constants(RenderingTarget.Emulator), .. Constants(RenderingTarget.Azure),
             .. Pairing(RenderingTarget.Emulator), .. Pairing(RenderingTarget.Azure)];

        /// <summary>
        /// Each key a connective's side that may fail is keyed by (<see cref="Failing"/>), beside each key it stands for: the
        /// side stands for every truth it could have given, and fails the connective beside them (decision 2). A side of either
        /// junction, either side or both, over every operand the other may be; a negation's operand.
        /// </summary>
        public static IReadOnlyList<(MeasuredKey Failing, MeasuredKey Stands)> Failing { get; } =
            [.. from c in new[] { And, Or }
                from y in Operands.Append(CosmosValueClass.Failing)
                from x in Booleans
                from b in y == CosmosValueClass.Failing ? Booleans : new[] { y }
                select (MeasuredKey.Of(c, CosmosValueClass.Failing, y), MeasuredKey.Of(c, x, b)),
             .. from c in new[] { And, Or }
                from x in Operands
                from y in Booleans
                select (MeasuredKey.Of(c, x, CosmosValueClass.Failing), MeasuredKey.Of(c, x, y)),
             .. from x in Booleans
                select (MeasuredKey.Of(Not, None, CosmosValueClass.Failing), MeasuredKey.Of(Not, None, x))];

        /// <summary>
        /// Each position and truth value an else-arm leaves its case's rows at as a condition that is not TRUE does under a
        /// junction (spec §5.1): FALSE and UNKNOWN keep no row at <c>Pos</c>, TRUE and UNKNOWN none beneath a <c>NOT</c>;
        /// with the shape the case then is, its condition joined with its then-arm: <c>c AND a</c> at <c>Pos</c>,
        /// <c>NOT c OR a</c> at <c>Neg</c> (the connective, and whether the condition is negated).
        /// </summary>
        public static IReadOnlyList<(RenderPosition, string, CosmosServiceTest, bool)> AbsorbingElse { get; } =
            [(RenderPosition.Pos, "false", And, false), (RenderPosition.Pos, "unknown", And, false), (RenderPosition.Neg, "true", Or, true), (RenderPosition.Neg, "unknown", Or, true)];

        /// <summary>
        /// Each position and truth value an else-arm keeps every row at that a condition not TRUE leaves: TRUE at <c>Pos</c>,
        /// FALSE beneath a <c>NOT</c>; with the shape the case then is over a two-valued condition: <c>NOT c OR a</c> at
        /// <c>Pos</c>, <c>c AND a</c> at <c>Neg</c>.
        /// </summary>
        public static IReadOnlyList<(RenderPosition, string, CosmosServiceTest, bool)> OppositeElse { get; } = [(RenderPosition.Pos, "true", Or, true), (RenderPosition.Neg, "false", And, false)];

        /// <summary>
        /// Kleene's constants of each junction: TRUE is <c>AND</c>'s identity and <c>OR</c>'s absorbing element, FALSE the
        /// reverse; a junction holding its identity is the rest, one holding its absorbing element is that constant.
        /// </summary>
        public static IReadOnlyList<(CosmosServiceTest Connective, bool Constant, bool Absorbs)> Units { get; } =
            [(And, true, false), (Or, false, false), (And, false, true), (Or, true, true)];

        /// <summary>
        /// The connective a position joins a leaf's implied forms by, each a weakening of the leaf: <c>AND</c> at <c>Pos</c>,
        /// where each keeps every row the leaf keeps, and <c>OR</c> at <c>Neg</c>, where each drops every row it drops; the
        /// junction's identity (<see cref="Units"/>) where it implies none.
        /// </summary>
        public static IReadOnlyList<(RenderPosition, CosmosServiceTest)> Weakens { get; } = [(RenderPosition.Pos, And), (RenderPosition.Neg, Or)];

        /// <summary>
        /// The position a case's weakening reads its then-arm beneath its condition at, with the wide else-arm's, which holds
        /// beneath any condition: at <c>Pos</c>, the condition's weakening at <c>Pos</c> joined with the then-arm's by the
        /// first connective, and that with the else-arm's by the second, <c>(c AND a) OR b</c>, true wherever the condition
        /// and the then-arm both are, or the else-arm is.
        /// </summary>
        public static IReadOnlyList<(RenderPosition, CosmosServiceTest, CosmosServiceTest)> CaseThen { get; } = [(RenderPosition.Pos, And, Or)];

        /// <summary>
        /// The position a case's weakening joins its arms' weakenings at, its condition's aside: at <c>Neg</c>, <c>a AND b</c>,
        /// false wherever either arm is.
        /// </summary>
        public static IReadOnlyList<(RenderPosition, CosmosServiceTest)> CaseArms { get; } = [(RenderPosition.Neg, And)];

        /// <summary>Kleene's negation of each constant: the other.</summary>
        public static IReadOnlyList<(bool Constant, bool Negation)> Negations { get; } = [(true, false), (false, true)];

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

        /// <summary>
        /// Two paths compared answer as the left compared with the right's value written as a literal, the comparator mirrored
        /// where the right is no literal (<c>Service.TwoPathsCompareAsAPathAndALiteral</c>): the answer a set's rows must give
        /// for S to read a pair by its readings (G3).
        /// </summary>
        static IEnumerable<(RenderingTarget, MeasuredKey, CosmosAnswer)> Pairing(RenderingTarget target) =>
            [(target, MeasuredKey.Of(ComparePaths, None, None), CosmosAnswer.True)];

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
