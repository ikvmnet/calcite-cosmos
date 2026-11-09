using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The <c>Calcite</c> rows of readings, as both graphs' readings vocabulary reads them (<see cref="ReadingVocabulary"/>,
    /// G7): read once from the registry by the adapter.
    /// </summary>
    /// <param name="Reads">What each reading reads each class of value as.</param>
    /// <param name="Bands">The character classes each reading's witnesses of a class held.</param>
    /// <param name="Rows">Each row naming a reading, beside the reading: what its answers rest on.</param>
    /// <param name="ReadAs">The readings a row says are read as another over each class both read alike.</param>
    /// <param name="Through">The tests of a reading a row says are its operand's.</param>
    /// <param name="LikeUnits">What <c>LIKE</c> reads each unit of its pattern as, each citing its row.</param>
    /// <param name="TestKinds">The classes each one-place test is TRUE of, each citing its row.</param>
    /// <param name="TestReads">The readings a one-place test's in-process body takes apart, with the reading it reads instead (G8).</param>
    /// <param name="Exact">The readings the facts graph reads as Calcite does beside a literal of a class, <c>Number</c> or <c>None</c> for any other.</param>
    /// <param name="Boxes">The box each reading or kind of value is held in, cast to <c>VARIANT</c> (G8).</param>
    /// <param name="SeesThrough">The comparisons that read the values inside one box (G8).</param>
    /// <param name="NullThrough">The SQL types whose box is null where what it holds is (G8).</param>
    /// <param name="Comparands">The readings that compare a number Calcite wrote cast with what it denotes, by the reading a
    /// reader applies last, and whether they take one a cast that drops scale moved, each citing its row (G8).</param>
    /// <param name="StrictOperators">Each operator by its name in the IR, and whether Calcite answers null for it over a null operand.</param>
    internal sealed record CalciteRows(
        IReadOnlyList<(string Reader, CosmosValueClass Kind, string Answer)> Reads,
        IReadOnlyList<(string Reader, CosmosValueClass Kind, string Band)> Bands,
        IReadOnlyList<(string Reader, string Row)> Rows,
        IReadOnlyList<(string Reader, string As)> ReadAs,
        IReadOnlyList<(string Reader, CosmosServiceTest Test, string Operand)> Through,
        IReadOnlyList<(LikeUnit Unit, string Row)> LikeUnits,
        IReadOnlyList<(CosmosServiceTest Test, CosmosValueClass Kind, string Row)> TestKinds,
        IReadOnlyList<(string Reader, string As)> TestReads,
        IReadOnlyList<(string Reader, CosmosValueClass Beside)> Exact,
        IReadOnlyList<(string Kind, string Box)> Boxes,
        IReadOnlyList<string> SeesThrough,
        IReadOnlyList<string> NullThrough,
        IReadOnlyList<(string Reading, bool Moved, string Row)> Comparands,
        IReadOnlyList<(string Name, bool Strict)> StrictOperators)
    {

        /// <summary>No row.</summary>
        public static readonly CalciteRows None = new([], [], [], [], [], [], [], [], [], [], [], [], [], []);

    }

}
