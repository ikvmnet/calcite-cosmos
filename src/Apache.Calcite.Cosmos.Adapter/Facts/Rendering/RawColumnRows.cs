using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The raw columns as a <c>Calcite</c> row states them, one fact a field (h12, G5): a reading through a column, <c>raw</c>
    /// or its cast to text, by the column's name. S's <c>raw</c> rules read them into each key's answer; none is a law.
    /// </summary>
    /// <param name="Reads">What a column's reading reads a stored value of each kind as (<see cref="CalciteRead"/>).</param>
    /// <param name="Beside">The literal classes a column's reading is compared beside.</param>
    /// <param name="Compiles">Whether each comparison compiles over a column's reading.</param>
    /// <param name="AsType">Whether a column's reading compares a value as its type, or as the text it renders.</param>
    /// <param name="Across">What a comparison a column compiles answers of a value it reads as itself beside a literal of another type.</param>
    internal sealed record RawColumnRows(
        IReadOnlyList<(string Reading, string Column, CosmosValueClass Kind, string Answer)> Reads,
        IReadOnlyList<(string Reading, string Column, CosmosValueClass Operand)> Beside,
        IReadOnlyList<(string Reading, string Column, CosmosServiceTest Test, bool Compiles)> Compiles,
        IReadOnlyList<(string Reading, string Column, bool AsType)> AsType,
        IReadOnlyList<(string Reading, string Column, CosmosServiceTest Test, Truths Truths)> Across)
    {

        /// <summary>No column: a raw reading's cells then read anything and a raise.</summary>
        public static RawColumnRows None { get; } = new([], [], [], [], []);

        /// <summary>The rows with one column's left out, as a planted defect drops it.</summary>
        public RawColumnRows Without(string column) => new([.. Reads.Where(r => r.Column != column)], [.. Beside.Where(r => r.Column != column)],
            [.. Compiles.Where(r => r.Column != column)], [.. AsType.Where(r => r.Column != column)], [.. Across.Where(r => r.Column != column)]);

        /// <summary>The columns the rows name.</summary>
        public IEnumerable<string> Columns => Reads.Select(r => r.Column).Distinct();

    }

}
