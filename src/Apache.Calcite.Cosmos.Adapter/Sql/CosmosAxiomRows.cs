using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The registry's one reader into the rendering graph: each atomic outcome of each row, one measured row per cell and
    /// per target it names. It merges nothing; which targets a key is settled under, and what two that differ mean
    /// together, are S's rules (<c>DESIGN.md</c>, <i>The registry, read into S</i>).
    /// </summary>
    internal static class CosmosAxiomRows
    {

        static readonly (CosmosAxiomTarget Flag, RenderingTarget Target)[] Targets =
            [(CosmosAxiomTarget.Emulator, RenderingTarget.Emulator), (CosmosAxiomTarget.Azure, RenderingTarget.Azure), (CosmosAxiomTarget.Calcite, RenderingTarget.Calcite)];

        /// <summary>One row per cell per target of every outcome that is no composite, citing its axiom.</summary>
        public static IReadOnlyList<MeasuredOutcome> Read(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. from axiom in axioms
               from outcome in axiom.Outcomes ?? []
               where outcome.Formula is null
               from target in Targets
               where outcome.On.HasFlag(target.Flag)
               from cell in outcome.Cells
               select new MeasuredOutcome(axiom.Id, MeasuredKey.Of(outcome.Test, outcome.Operand, cell.Cell), target.Target, cell.Answer),
        ];

        /// <summary>
        /// One row per key per target of every function outcome: what the target computed against Calcite, which S reads as
        /// constant raw facts (<c>DESIGN.md</c>, <i>The rows the next families read, and function rows</i>). It merges nothing either.
        /// </summary>
        public static IReadOnlyList<(FunctionKey Key, RenderingTarget Target, CosmosFunctionAnswer Answer)> ReadFunctions(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. from axiom in axioms
               from outcome in axiom.FunctionOutcomes ?? []
               from target in Targets
               where outcome.On.HasFlag(target.Flag)
               select (FunctionKey.Of(outcome.Function, outcome.Operand, outcome.Band), target.Target, outcome.Answer),
        ];

        /// <summary>
        /// What each raw column a <c>Calcite</c> row states answers on each key, one row a column a key, whose union S's rule
        /// takes (h12): the raw reading's comparisons beside a literal of each class and its null test, and those of its cast
        /// to text, each its column's answer for the kind. A column comparing the value as its type answers TRUE or FALSE,
        /// which the cell decides; across types an equality is unequal and an ordering raises; a column with no ordering
        /// raises on every one; a null is SQL null (<c>Calcite.RawNullIsSqlNull</c>, <c>Calcite.CastOfARawValueToText</c>).
        /// </summary>
        public static IReadOnlyList<(RawKey Key, string Column, Truths Truths)> ReadColumns(IEnumerable<CosmosAxiom> axioms) =>
            [.. from axiom in axioms where axiom.MeasuredOn == CosmosAxiomTarget.Calcite from column in axiom.Columns ?? [] from row in Rows(column) select (row.Key, column.Name, row.Truths)];

        static readonly CosmosValueClass[] Kinds = [CosmosValueClass.String, CosmosValueClass.Number, CosmosValueClass.True, CosmosValueClass.False,
            CosmosValueClass.Object, CosmosValueClass.Array, CosmosValueClass.Null];

        static readonly CosmosServiceTest[] Comparisons = [CosmosServiceTest.Equal, CosmosServiceTest.Less, CosmosServiceTest.LessOrEqual, CosmosServiceTest.Greater,
            CosmosServiceTest.GreaterOrEqual];

        static IEnumerable<(RawKey Key, Truths Truths)> Rows(CosmosRawColumn column)
        {
            const Truths T = Truths.True, F = Truths.False, U = Truths.Unknown, R = Truths.Raises;
            var cast = PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader);
            foreach (var kind in Kinds)
            {
                var (read, text) = KindOf(kind) is { } k ? (column.Read[k], column.AsText[k]) : (CosmosRawColumn.Answer.Null, CosmosRawColumn.Answer.Null);
                yield return (RawKey.Of(PredicateAtom.RawReader, kind, CosmosServiceTest.IsNull, CosmosValueClass.None), Answered(read, F, T));
                yield return (RawKey.Of(cast, kind, CosmosServiceTest.IsNull, CosmosValueClass.None), Answered(text, F, T));
                foreach (var test in Comparisons)
                {
                    foreach (var operand in Kinds.Where(o => column.BesideStringsAlone == false || o == CosmosValueClass.String))
                    {
                        var key = RawKey.Of(PredicateAtom.RawReader, kind, test, operand);
                        yield return (key, test != CosmosServiceTest.Equal && column.Orders == false ? R : Answered(read, key.Across ? test == CosmosServiceTest.Equal ? F : R : T | F, U));
                    }

                    yield return (RawKey.Of(cast, kind, test, CosmosValueClass.String), Answered(text, T | F, U));
                }
            }
        }

        static Truths Answered(CosmosRawColumn.Answer answer, Truths itself, Truths nulled) =>
            answer switch { CosmosRawColumn.Answer.Itself => itself, CosmosRawColumn.Answer.Null => nulled, _ => Truths.Raises };

        /// <summary>The column's kind of a class; a null's none.</summary>
        static string? KindOf(CosmosValueClass kind) => kind switch
        {
            CosmosValueClass.String => CosmosRawColumn.String,
            CosmosValueClass.Number => CosmosRawColumn.Number,
            CosmosValueClass.True or CosmosValueClass.False => CosmosRawColumn.Boolean,
            CosmosValueClass.Object or CosmosValueClass.Array => CosmosRawColumn.Structure,
            _ => null,
        };

        /// <summary>
        /// The cells of a key some outcome answers for a target that no outcome covers for it: unmeasured there, so no
        /// row, and S reads the service as answering anything. Declared, so that a cell is owed on purpose and one
        /// dropped from a vector is caught. None is: the probe account has run every witness the emulator runs (D7).
        /// </summary>
        public static IReadOnlyList<(MeasuredKey Key, RenderingTarget Target)> Owed { get; } = [];

    }

}
