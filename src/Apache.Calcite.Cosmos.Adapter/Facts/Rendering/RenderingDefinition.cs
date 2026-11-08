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
        IReadOnlyDictionary<string, ReaderEdges> Edges,
        Func<PredicateAtom, ServiceForm, ReaderEdges, ValueSet<CutPoint>> Cut,
        Func<string, PathValues, ValueSet<string>, ValueSet<CutPoint>, CellPartition> Split,
        Func<CellPartition, PathValues, ValueSet<string>, ValueSet<CutPoint>, CellPartition> SetAside,
        Func<ServiceForm, CellPartition, ServiceTable, LeafReads, OutcomeVector> Leaf,
        Func<ServiceForm, OutcomeVector, OutcomeVector, ServiceTable, OutcomeVector> Compound,
        Func<PredicateAtom, RawTruths, OutcomeVector, CellOutcomes> Pair,
        IReadOnlyList<(TemplateId, RenderPosition)> Templates, IReadOnlyList<(TemplateId, string Shape)> Fits,
        IReadOnlyList<(string Name, bool Strict)> StrictOperators, IReadOnlyList<string> StrictReaders)
    {

        /// <summary>
        /// The function rows S reads at a computed place (h10): each key's answer on each target that measured it
        /// (<c>CosmosAxiomRows.ReadFunctions</c>). None in a definition that names none.
        /// </summary>
        public IReadOnlyList<(FunctionKey Key, RenderingTarget Target, CosmosFunctionAnswer Answer)> Functions { get; init; } = [];

        /// <summary>
        /// The readers each function's rows were measured through (<c>CosmosAxiomRows.ReadFunctionReads</c>): through what a
        /// gathered operand may be read for a key to hold of it.
        /// </summary>
        public IReadOnlyList<(string Function, IReadOnlyList<string> Readers)> FunctionReads { get; init; } = [];

        /// <summary>
        /// Each operand a function row is of (<c>CosmosAxiomRows.ReadFunctionSubjects</c>), one a row, which S's union gathers
        /// per function (F2): which operands a call's keys are read off.
        /// </summary>
        public IReadOnlyList<(string Function, long Subject)> FunctionSubjects { get; init; } = [];

        /// <summary>
        /// The names each function's rows are filed under, by the operator they are of and the SQL type they are keyed by,
        /// none where the rows are the function's alone (<c>CosmosAxiomRows.ReadFunctionNames</c>, G6): S's <c>rowname</c>
        /// and <c>rownamedalone</c>, from which its rule keys a call.
        /// </summary>
        public IReadOnlyList<(string Operator, string? Type, string Name)> FunctionNames { get; init; } = [];

        /// <summary>
        /// What Calcite answers over each function key, a value or null, as its row states (<c>CosmosAxiomRows.ReadFunctionYields</c>,
        /// G6): what S's image holds of a call whose keys agree.
        /// </summary>
        public IReadOnlyList<(FunctionKey Key, CalciteYield Yields)> FunctionYields { get; init; } = [];

        /// <summary>
        /// The kinds of value each SQL type holds (<c>CosmosRendering.ReadTypeKinds</c>, G6), a constant of Calcite's type
        /// system: what a computed place of the type may hold where it is a value. None in a definition that names none,
        /// where no call is imaged but at a point.
        /// </summary>
        public IReadOnlyList<(string Type, string Kind)> TypeKinds { get; init; } = [];

        /// <summary>
        /// The readers of a literal operand, its own cell (G6): read exactly whatever function reads it, so S licenses it
        /// beside the readers a function's rows were measured through, and keyed as a literal where the rows key one apart.
        /// </summary>
        public IReadOnlyList<string> LiteralReaders { get; init; } = [PathImport.LiteralReader];

        /// <summary>
        /// The service spellings a call of a function may take where more than one says what Calcite computes, each by its
        /// rank: one every call takes, at <see cref="FunctionKey.Every"/>, and one a call takes where its literal at an
        /// ordinal is a whole number of a class (<c>CosmosFunctionSpellings.Spellings</c>, G6). S's <c>callspelling</c> keeps
        /// the least a call takes, which the printer and the translator write.
        /// </summary>
        public IReadOnlyList<(string Function, long Spelling, long Ordinal, FunctionValueClass Class)> CallSpellings { get; init; } = [];

        /// <summary>
        /// What each raw column answers on each key (<c>CosmosAxiomRows.ReadColumns</c>), whose union S's rule takes (h12).
        /// None in a definition that names none, where a raw reading's cells read anything and a raise.
        /// </summary>
        public IReadOnlyList<(RawKey Key, string Column, Truths Truths)> Columns { get; init; } = [];

        /// <summary>
        /// Which bands each comparator ranks apart from Calcite's UTF-16 order (<c>CosmosAxiomRows.ReadParts</c>, F3), which
        /// S's parted rules read through the comparators a set's string order names. None in a definition that names none,
        /// where no literal parts an order.
        /// </summary>
        public IReadOnlyList<(CosmosAnswer Comparator, string Band, string Other)> Parts { get; init; } = [];

        /// <summary>
        /// Which band each comparator moves in order (<c>CosmosAxiomRows.ReadMoves</c>, F3), which S's parted rules read through
        /// the comparators a set's string order names (G1).
        /// </summary>
        public IReadOnlyList<(CosmosAnswer Comparator, string Band)> Moves { get; init; } = [];

        /// <summary>
        /// What a string written in a class is stored holding, on each target that stored it so (<c>CosmosAxiomRows.ReadStoredOn</c>,
        /// F3), which S reads where an affix's pattern holds what a target stores (G1).
        /// </summary>
        public IReadOnlyList<(RenderingTarget Target, string Written, string Stored)> StoredAs { get; init; } = [];

        /// <summary>
        /// Which tests read a string only to a band's first character, for an argument of a shape, on each target
        /// (<c>CosmosAxiomRows.ReadTruncations</c>, F3), which S reads of an affix on a target a set serves (G1).
        /// </summary>
        public IReadOnlyList<(RenderingTarget Target, CosmosServiceTest Test, string Shape, string Band)> Truncations { get; init; } = [];

        /// <summary>
        /// Which tests' case-insensitive forms match as the test over a fold, on each target, and the bands each row says
        /// nothing of (<c>CosmosAxiomRows.ReadFolds</c>, G6), which S reads of a <c>LIKE</c> over a fold (<c>foldedas</c>).
        /// None in a definition that names none, where no such <c>LIKE</c> is written case-insensitive.
        /// </summary>
        public IReadOnlyList<(RenderingTarget Target, CosmosServiceTest Test, string Function, string Band)> Folds { get; init; } = [];

        /// <summary>
        /// Which band each comparator's spelling rewrites (<c>CosmosAxiomRows.ReadRewrites</c>, G1): from which S derives the
        /// spelling a set's statements send, the paths it may write, and where an affix's cut of a range says the match. None
        /// in a definition that names none, where every set is sent as written.
        /// </summary>
        public IReadOnlyList<(CosmosAnswer Comparator, string Band)> Rewrites { get; init; } = [];

        /// <summary>
        /// The classes a parameter holding a character of is never sent on a target (<c>CosmosAxiomRows.ReadUnsent</c>, G1): what
        /// S's lifts read of each form's literals under a set serving it.
        /// </summary>
        public IReadOnlyList<(RenderingTarget Target, string Class)> Unsent { get; init; } = [];

        /// <summary>
        /// How a comparison of two paths reads against a literal (<c>CosmosAxiomRows.ReadPairReadings</c>, F3), which S's pairs
        /// rules read each set's pair table off (G3). None in a definition that names none, where a pair reads no key.
        /// </summary>
        public IReadOnlyList<(CosmosServiceTest Test, bool Swapped, CosmosServiceTest Reads)> PairReadings { get; init; } = [];

        /// <summary>
        /// How each comparator reads a whole number within int64, <c>value</c> or <c>double</c> (<c>CosmosAxiomRows.ReadWholeReadings</c>,
        /// G2), which S's roles rule hands each order entry's readers. None in a definition that names none, where a whole
        /// number lies anywhere.
        /// </summary>
        public IReadOnlyList<(CosmosAnswer Comparator, string Reading)> WholeReadings { get; init; } = [];

        /// <summary>
        /// What the leaf primitive evaluates each role's cells by, its test over its operand class beside each answer
        /// (<see cref="ServiceLeaf.Evaluates"/>): S's roles rule hands a key a reader only among them, so an answer the
        /// meanings give a role and the leaf cannot evaluate reads nothing. A plant that replaces the leaf names its own.
        /// </summary>
        public ValueSet<(CosmosServiceTest Test, CosmosValueClass Operand, CosmosAnswer Answer)> Evaluates { get; init; } = ServiceLeaf.Evaluates;

        /// <summary>The box each reading or kind of value is held in, cast to <c>VARIANT</c> (<c>CosmosAxiomRows.ReadBoxes</c>, F3); no rule reads it yet (G8).</summary>
        public IReadOnlyList<(string Kind, string Box)> Boxes { get; init; } = [];

        /// <summary>The comparisons that read the values inside one box (<c>CosmosAxiomRows.ReadSeesThrough</c>, F3); no rule reads it yet (G8).</summary>
        public IReadOnlyList<string> SeesThrough { get; init; } = [];

        /// <summary>The functions that answer a structure alone (<c>CosmosAxiomRows.ReadStructureOnly</c>, F3); no rule reads it yet (G8).</summary>
        public IReadOnlyList<string> StructureOnly { get; init; } = [];

        /// <summary>What a reading answers over each kind of stored value in process (<c>CosmosAxiomRows.ReadCalciteReadings</c>, F3); no rule reads it yet (G7).</summary>
        public IReadOnlyList<(string Reader, CosmosValueClass Kind, string Answer)> CalciteReadings { get; init; } = [];

        /// <summary>The character classes a reading's witnesses of a kind held (<c>CosmosAxiomRows.ReadCalciteBands</c>, F3); no rule reads it yet (G7).</summary>
        public IReadOnlyList<(string Reader, CosmosValueClass Kind, string Band)> CalciteBands { get; init; } = [];

        /// <summary>The casts a <c>Calcite</c> row measured, each citing its row (<c>CosmosAxiomRows.ReadCasts</c>, F3); no rule reads it yet (G8).</summary>
        public IReadOnlyList<(string Target, string Reader, string Row)> Casts { get; init; } = [];

        /// <summary>
        /// The readings that compare a number Calcite wrote cast with the number it denotes, by the reading a reader applies
        /// last, and whether they take one a cast that drops scale moved, each citing its row (<c>CosmosAxiomRows.ReadComparands</c>,
        /// G8): S's <c>takesconstant</c>.
        /// </summary>
        public IReadOnlyList<(string Reading, bool Moved, string Row)> Comparands { get; init; } = [];

        /// <summary>
        /// What each reading's order holds in process (<c>CosmosAxiomRows.ReadReaderOrders</c>, F3), which S's pair rule hands
        /// a comparison of two readings (G3). None in a definition that names none, where no pair is placed by its order.
        /// </summary>
        public IReadOnlyList<(string Reader, string Claim)> ReaderOrders { get; init; } = [];

        /// <summary>
        /// The kind each reading compares as, the family of the Calcite type it is of (<c>CosmosRendering</c>, G3): two
        /// readings compared are an atom of S's pair rule where both are of one kind. None in a definition that names none,
        /// where no comparison of two readings is an atom.
        /// </summary>
        public IReadOnlyList<(string Reader, string Kind)> ReaderKinds { get; init; } = [];

        /// <summary>The classes each one-place test is true of in process (<c>CosmosAxiomRows.ReadTestKinds</c>, F3); no rule reads it yet (G7).</summary>
        public IReadOnlyList<(CosmosServiceTest Test, CosmosValueClass Kind)> TestKinds { get; init; } = [];

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
            CutPoints.Of, PathCell.Split, PathCell.SetAside, ServiceLeaf.On, ServiceCompound.On, CellOutcomes.Of,
            RenderingTemplateRows.Templates, RenderingTemplateRows.Fits, [], []);

    }

}
