using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Everything S is defined from that a test may replace: the host rows its theory loads, and the evaluation primitives
    /// its rules call. A plant is a definition with one of them wrong, in a graph of its own.
    /// </summary>
    internal sealed record RenderingDefinition(
        IReadOnlyList<MeasuredOutcome> Rows, IReadOnlyDictionary<RenderingSet, ValueSet<RenderingTarget>> Serves, AnswerMeanings Means,
        IReadOnlyList<(RenderingTarget, MeasuredKey, CosmosAnswer)> Kleene, IReadOnlyDictionary<CosmosServiceTest, ValueSet<CellClass>> Identity,
        IReadOnlyList<(RenderPosition, Verdict, Box)> Allowing, IReadOnlyList<(RenderPosition, Verdict, Box)> Refuting, Truths Tolerated,
        IReadOnlyDictionary<string, ReaderEdges> Edges, IReadOnlyList<(string, string)> Parting,
        Func<PredicateAtom, ServiceForm, ReaderEdges, ValueSet<CutPoint>> Cut,
        Func<string, PathValues, ValueSet<string>, ValueSet<CutPoint>, CellPartition> Split,
        Func<ServiceForm, CellPartition, ServiceTable, ValueSet<string>, OutcomeVector> Leaf,
        Func<ServiceForm, OutcomeVector, OutcomeVector, ServiceTable, OutcomeVector> Compound,
        Func<PredicateAtom, OutcomeVector, AnswerMeanings, CellOutcomes> Pair)
    {

        /// <summary>
        /// S as measured: the rows given, the sets as the targets serve them, and Kleene's logic, the agreement, edges and
        /// primitives as written.
        /// </summary>
        public static RenderingDefinition Measured(IReadOnlyList<MeasuredOutcome> rows) => new(
            rows,
            new Dictionary<RenderingSet, ValueSet<RenderingTarget>>
            {
                [RenderingSet.Production] = ValueSet.Of(RenderingTarget.Azure),
                [RenderingSet.Portable] = ValueSet.Of(RenderingTarget.Azure, RenderingTarget.Emulator),
                [RenderingSet.Emulator] = ValueSet.Of(RenderingTarget.Emulator),
                [RenderingSet.Calcite] = ValueSet.Of(RenderingTarget.Calcite),
            },
            AnswerMeanings.Measured, KleeneTables.Rows, KleeneTables.Identity, AgreementRows.Allowing, AgreementRows.Refuting, Truths.Raises,
            ReaderEdgesTable.Rows,
            // U+E000 and above sorts after a surrogate by UTF-16 unit and before the character it writes by code point.
            [(JsonValues.SurrogateBand, JsonValues.HighBand), (JsonValues.HighBand, JsonValues.SurrogateBand)],
            CutPoints.Of, PathCell.Split, ServiceLeaf.On, ServiceCompound.On, CellOutcomes.Of);

    }

}
