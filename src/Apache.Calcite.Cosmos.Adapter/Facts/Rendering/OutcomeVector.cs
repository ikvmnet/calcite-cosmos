using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What the service makes of a form on each cell of a partition, in the cells' order, and the rows it read.
    /// </summary>
    /// <remarks>
    /// Equal by content: S keeps one vector for each partition and form, and a second evaluation must be the same value.
    /// </remarks>
    internal sealed record OutcomeVector(CellPartition Cells, IReadOnlyList<CellOutcome> Outcomes, ValueSet<string> Rows)
    {

        /// <inheritdoc />
        public bool Equals(OutcomeVector? other) =>
            other is not null && Cells.Equals(other.Cells) && Outcomes.SequenceEqual(other.Outcomes) && Rows.Equals(other.Rows);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Cells, Outcomes.Count, Rows);

    }

}
