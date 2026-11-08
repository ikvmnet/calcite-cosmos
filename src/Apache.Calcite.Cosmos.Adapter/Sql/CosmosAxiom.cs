using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// One measured fact a pushdown relies on.
    /// </summary>
    /// <param name="Id">A stable name, <c>Service.</c> or <c>Calcite.</c> by what was measured.</param>
    /// <param name="Statement">What holds, in a sentence or two, as the measurement asserts it.</param>
    /// <param name="MeasuredOn">Every target the measurement has been run against and passed.</param>
    /// <param name="Measured">When it was first measured.</param>
    /// <param name="PinnedBy">The test that measures it, relative to the test assembly's root
    /// namespace: <c>Measurements.CosmosJsonTypeMeasurementTests.IsPrimitiveIsFalseOverAnAbsentPath</c>.</param>
    /// <param name="Outcomes">What the measurement answered, key by key, as the rendering graph reads it
    /// (<see cref="CosmosAxiomRows"/>); each names the targets that ran it, within <paramref name="MeasuredOn"/>.</param>
    /// <param name="Subjects">What the statement holds of, by the names a reader takes as data: the functions or the
    /// readings of a strictness row (<c>Metadata.CosmosStrictness</c>), each one its pinning test measures.</param>
    /// <param name="FunctionOutcomes">What a function computed, key by key, against Calcite (<see cref="CosmosAxiomRows.ReadFunctions"/>).</param>
    /// <param name="Readers">The readers a function outcome's subject was measured through, Calcite's side of each witness
    /// (<see cref="CosmosAxiomRows.ReadFunctionReads"/>): a computed place over any other is no key of the row's.</param>
    /// <param name="Columns">What each raw column makes of each kind of value, a <c>Calcite</c> row's
    /// (<see cref="CosmosAxiomRows.ReadColumns"/>).</param>
    /// <param name="StoredAs">What a string written in a class was stored holding, a <c>Service</c> row's
    /// (<see cref="CosmosAxiomRows.ReadStored"/>, and by target <see cref="CosmosAxiomRows.ReadStoredOn"/>).</param>
    /// <param name="Parts">Which bands a comparator ranks apart from Calcite's order, a <c>Service</c> order row's
    /// (<see cref="CosmosAxiomRows.ReadParts"/>, F3).</param>
    /// <param name="Moves">Which band a comparator moves in order, a <c>Service</c> order row's (<see cref="CosmosAxiomRows.ReadMoves"/>).</param>
    /// <param name="Truncations">Which tests read a string only up to a band's first character (<see cref="CosmosAxiomRows.ReadTruncations"/>).</param>
    /// <param name="PairReadings">How a comparison of two paths reads against a literal, the mirror law (<see cref="CosmosAxiomRows.ReadPairReadings"/>).</param>
    /// <param name="Boxes">The box each reading or kind of value is held in, cast to <c>VARIANT</c> (<see cref="CosmosAxiomRows.ReadBoxes"/>).</param>
    /// <param name="SeesThrough">The comparisons that read the values inside one box, by the names <c>PredicateAtom</c> spells
    /// them (<see cref="CosmosAxiomRows.ReadSeesThrough"/>).</param>
    /// <param name="StructureOnly">The functions that answer a structure alone, null for anything else, by the name the IR
    /// reads each by (<see cref="CosmosAxiomRows.ReadStructureOnly"/>).</param>
    /// <param name="CalciteReadings">What a reading answers over each kind of stored value in process, a <c>Calcite</c> row's
    /// (<see cref="CosmosAxiomRows.ReadCalciteReadings"/>).</param>
    /// <param name="Casts">The casts a <c>Calcite</c> row measured, by target and the reading cast (<see cref="CosmosAxiomRows.ReadCasts"/>).</param>
    /// <param name="ReaderOrders">What a reading's order holds in process (<see cref="CosmosAxiomRows.ReadReaderOrders"/>).</param>
    /// <param name="Comparands">The readings a <c>Calcite</c> row says compare a number Calcite wrote cast with the number the cast
    /// denotes, moved or not (<see cref="CosmosAxiomRows.ReadComparands"/>, G8).</param>
    /// <param name="Rewrites">Which bands a comparator's spelling rewrites, a <c>Service</c> spelling row's (<see cref="CosmosAxiomRows.ReadRewrites"/>, G1).</param>
    /// <param name="Unsent">The classes a parameter holding a character of is never sent, on the row's targets
    /// (<see cref="CosmosAxiomRows.ReadUnsent"/>, G1).</param>
    /// <param name="WholeReadings">How a comparator reads a whole number within int64, by its value, its double or both, a
    /// <c>Service</c> order row's (<see cref="CosmosAxiomRows.ReadWholeReadings"/>, G2).</param>
    /// <param name="Folds">Which tests' case-insensitive forms match as the test over a fold, a <c>Service</c> row's
    /// (<see cref="CosmosAxiomRows.ReadFolds"/>, G6).</param>
    /// <param name="ReadAs">The readings a <c>Calcite</c> row says are read as another over each class both read alike
    /// (<see cref="CosmosAxiomRows.ReadCalciteReadAs"/>, G7).</param>
    /// <param name="Through">The tests of a reading a <c>Calcite</c> row says are its operand's (<see cref="CosmosAxiomRows.ReadCalciteThrough"/>, G7).</param>
    /// <param name="LikeUnits">What <c>LIKE</c> reads each unit of its pattern as on each target the row names: Calcite's
    /// (<see cref="CosmosAxiomRows.ReadLikeUnits"/>) and the service's (<see cref="CosmosAxiomRows.ReadServiceLikeUnits"/>), G7.</param>
    /// <param name="TestReads">The readings a one-place test's in-process body takes apart, and what it reads instead
    /// (<see cref="CosmosAxiomRows.ReadTestReads"/>, G8).</param>
    internal sealed record CosmosAxiom(string Id, string Statement, CosmosAxiomTarget MeasuredOn, DateOnly Measured, string PinnedBy,
        IReadOnlyList<CosmosOutcome>? Outcomes = null, IReadOnlyList<string>? Subjects = null,
        IReadOnlyList<CosmosFunctionOutcome>? FunctionOutcomes = null, IReadOnlyList<CosmosRawColumn>? Columns = null, IReadOnlyList<string>? Readers = null,
        IReadOnlyList<CosmosStoredClass>? StoredAs = null, IReadOnlyList<CosmosParting>? Parts = null, IReadOnlyList<CosmosMove>? Moves = null,
        IReadOnlyList<CosmosTruncation>? Truncations = null, IReadOnlyList<CosmosPairReading>? PairReadings = null, IReadOnlyList<CosmosBox>? Boxes = null,
        IReadOnlyList<string>? SeesThrough = null, IReadOnlyList<string>? StructureOnly = null, IReadOnlyList<CosmosCalciteReading>? CalciteReadings = null,
        IReadOnlyList<CosmosCast>? Casts = null, IReadOnlyList<CosmosReaderOrder>? ReaderOrders = null, IReadOnlyList<CosmosComparand>? Comparands = null,
        IReadOnlyList<CosmosRewrite>? Rewrites = null, IReadOnlyList<string>? Unsent = null, IReadOnlyList<CosmosWholeReading>? WholeReadings = null,
        IReadOnlyList<CosmosFold>? Folds = null, IReadOnlyList<CosmosCalciteReadAs>? ReadAs = null, IReadOnlyList<CosmosCalciteThrough>? Through = null,
        IReadOnlyList<CosmosLikeUnit>? LikeUnits = null, IReadOnlyList<CosmosTestRead>? TestReads = null);

}
