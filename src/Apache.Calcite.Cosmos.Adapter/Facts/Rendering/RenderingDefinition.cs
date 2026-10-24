using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Everything S is defined from that a test may replace: the host rows its theory loads, and the evaluation primitives
    /// its rules call. A plant is a definition with one of them wrong, in a graph of its own.
    /// </summary>
    /// <remarks>
    /// Generate mode's rows are the templates asked at each position and the shapes each fits, and what Calcite answers
    /// null for where an operand or the path it reads is null: each operator by the name the IR reads it by, strict or not,
    /// and the strict readers. The host reads those from measured rows and Calcite's declared policy, one row an operator;
    /// a definition that names none is strict in nothing, so a weakening keeps no presence.
    /// </remarks>
    internal sealed record RenderingDefinition(
        IReadOnlyList<MeasuredOutcome> Rows, IReadOnlyDictionary<RenderingSet, ValueSet<RenderingTarget>> Serves, AnswerMeanings Means,
        IReadOnlyList<(RenderingTarget, MeasuredKey, CosmosAnswer)> Kleene, IReadOnlyDictionary<CosmosServiceTest, ValueSet<CellClass>> Identity,
        IReadOnlyList<(RenderPosition, Verdict, Box)> Allowing, IReadOnlyList<(RenderPosition, Verdict, Box)> Refuting, Truths Tolerated,
        IReadOnlyDictionary<string, ReaderEdges> Edges, IReadOnlyList<(string, string)> Parting,
        Func<PredicateAtom, ServiceForm, ReaderEdges, ValueSet<CutPoint>> Cut,
        Func<string, PathValues, ValueSet<string>, ValueSet<CutPoint>, CellPartition> Split,
        Func<CellPartition, PathValues, ValueSet<string>, ValueSet<CutPoint>, CellPartition> SetAside,
        Func<ServiceForm, CellPartition, ServiceTable, ValueSet<string>, OutcomeVector> Leaf,
        Func<ServiceForm, OutcomeVector, OutcomeVector, ServiceTable, OutcomeVector> Compound,
        Func<PredicateAtom, OutcomeVector, AnswerMeanings, Truths, CellOutcomes> Pair,
        IReadOnlyList<(TemplateId, RenderPosition)> Templates, IReadOnlyList<(TemplateId, string Shape)> Fits,
        IReadOnlyList<(string Name, bool Strict)> StrictOperators, IReadOnlyList<string> StrictReaders)
    {

        /// <summary>The parting bands as one set, which <c>sendable</c> reads.</summary>
        public ValueSet<string> PartingBands => ValueSet.From(Parting.SelectMany(p => new[] { p.Item1, p.Item2 }));

        /// <summary>
        /// S as measured: the rows given, the sets as the targets serve them, and Kleene's logic, the agreement, edges,
        /// templates and primitives as written; strict in nothing until the host names what is.
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
            CutPoints.Of, PathCell.Split, PathCell.SetAside, ServiceLeaf.On, ServiceCompound.On, CellOutcomes.Of,
            RenderingTemplateRows.Templates, RenderingTemplateRows.Fits, [], []);

    }

}
