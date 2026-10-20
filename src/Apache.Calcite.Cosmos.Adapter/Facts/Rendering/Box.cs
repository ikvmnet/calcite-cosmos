using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One row of the agreement: a class lies in the box where Calcite's truths and the service's outcomes on a cell each
    /// lie within its own, and the rows that say a position reads those outcomes so.
    /// </summary>
    internal sealed record Box(Truths Calcite, ServiceOutcomes Service, ValueSet<string> Cites) : IComparable<Box>
    {

        /// <inheritdoc />
        public int CompareTo(Box? other) => other is null ? 1
            : Calcite != other.Calcite ? Calcite.CompareTo(other.Calcite)
            : Service != other.Service ? Service.CompareTo(other.Service)
            : string.CompareOrdinal(string.Join(",", Cites.AsEnumerable()), string.Join(",", other.Cites.AsEnumerable()));

    }

}
