using System;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What both sides make of a pair on a cell: Calcite's truths and the service's outcomes. The gate decides on classes,
    /// never on cells.
    /// </summary>
    internal readonly record struct CellClass(Truths Calcite, ServiceOutcomes Service) : IComparable<CellClass>
    {

        /// <inheritdoc />
        public int CompareTo(CellClass other) => Calcite != other.Calcite ? ((int)Calcite).CompareTo((int)other.Calcite) : ((int)Service).CompareTo((int)other.Service);

        /// <summary>The class written <c>(U,t)</c>: Calcite's truths in capitals, the service's outcomes in small letters, a failure <c>E</c>.</summary>
        public override string ToString() => "(" + Letters((int)Calcite, "TFUR") + "," + Letters((int)Service, "tfuE") + ")";

        static string Letters(int flags, string letters)
        {
            var spelled = "";
            for (var i = 0; i < letters.Length; i++)
                if ((flags & (1 << i)) != 0)
                    spelled += letters[i];

            return spelled;
        }

    }

}
