using System;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What both sides make of a pair on a cell: Calcite's truths and the service's outcomes, as each side gives them; whether
    /// the service's are of a value that is no boolean, which S's rule reads as a whole condition reads one; and whether only
    /// a document an earlier round drops holds the cell, which S's rules set aside (G2). The gate decides on classes, never
    /// on cells.
    /// </summary>
    internal readonly record struct CellClass(Truths Calcite, ServiceOutcomes Service, bool NonBoolean = false, bool Outside = false) : IComparable<CellClass>
    {

        /// <inheritdoc />
        public int CompareTo(CellClass other) =>
            Calcite != other.Calcite ? ((int)Calcite).CompareTo((int)other.Calcite)
            : Service != other.Service ? ((int)Service).CompareTo((int)other.Service)
            : NonBoolean != other.NonBoolean ? NonBoolean.CompareTo(other.NonBoolean)
            : Outside.CompareTo(other.Outside);

        /// <summary>
        /// The class written <c>(U,t)</c>: Calcite's truths in capitals, the service's outcomes in small letters, a failure
        /// <c>E</c>; then <c> whole</c> where the value is no boolean, and <c> aside</c> where the cell is set aside.
        /// </summary>
        public override string ToString() => "(" + Letters((int)Calcite, "TFUR") + "," + Letters((int)Service, "tfuE") + ")" + (NonBoolean ? " whole" : "") + (Outside ? " aside" : "");

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
