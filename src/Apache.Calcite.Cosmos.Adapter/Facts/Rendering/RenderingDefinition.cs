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
    /// <param name="Rows">The measured rows, one per key, target and axiom.</param>
    /// <param name="Serves">The targets each set holds a statement to.</param>
    /// <param name="Means">What each answer means to a <c>WHERE</c>.</param>
    /// <param name="Kleene">What each target must answer on a connective's keys for a set to compose through it.</param>
    /// <param name="Identity">The class of a cell on which a side leaves each junction to its sibling.</param>
    /// <param name="Allowing">The boxes a class must lie in for a verdict to hold.</param>
    /// <param name="Refuting">The boxes in which a class definitely refutes a verdict.</param>
    /// <param name="Tolerated">What an allowing box sets aside of Calcite's truths: its raise (decision 2).</param>
    /// <param name="Edges">Where each reader's answer changes.</param>
    /// <param name="Parting">The pairs of character classes whose meeting parts the service's order of strings from Calcite's.</param>
    /// <param name="Cut">Where a pair's cells are cut (<see cref="CutPoints.Of"/>).</param>
    /// <param name="Split">One import cut into cells (<see cref="PathCell.Split"/>).</param>
    /// <param name="Leaf">One test on each cell (<see cref="ServiceLeaf.On"/>).</param>
    /// <param name="Compound">One connective over two vectors (<see cref="ServiceCompound.On"/>).</param>
    /// <param name="Pair">Calcite's atom beside the service on each cell (<see cref="CellOutcomes.Of"/>).</param>
    internal sealed record RenderingDefinition(
        IReadOnlyList<MeasuredOutcome> Rows, IReadOnlyDictionary<RenderingSet, ValueSet<RenderingTarget>> Serves, AnswerMeanings Means,
        IReadOnlyList<(RenderingTarget, MeasuredKey, CosmosAnswer)> Kleene, IReadOnlyDictionary<CosmosServiceTest, ValueSet<CellClass>> Identity,
        IReadOnlyList<(RenderPosition, Verdict, Box)> Allowing, IReadOnlyList<(RenderPosition, Verdict, Box)> Refuting, Truths Tolerated,
        IReadOnlyDictionary<string, ReaderEdges> Edges, IReadOnlyList<(string, string)> Parting,
        Func<PredicateAtom, ServiceForm, ReaderEdges, ValueSet<CutPoint>> Cut,
        Func<PathFacts, ValueSet<CutPoint>, CellPartition> Split,
        Func<ServiceForm, CellPartition, ServiceTable, ValueSet<string>, OutcomeVector> Leaf,
        Func<ServiceForm, OutcomeVector, OutcomeVector, ServiceTable, OutcomeVector> Compound,
        Func<PredicateAtom, OutcomeVector, CellOutcomes> Pair)
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
