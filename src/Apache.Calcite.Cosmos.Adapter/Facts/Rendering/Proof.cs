using System;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Why a claim holds: the rows it rests on, and how many cells it was checked on.
    /// </summary>
    /// <remarks>
    /// Packing only: no verdict reads it. Ordered by its cells, then its rows' count, then its text, so the least of two
    /// proofs of one claim is one value whichever is found first.
    /// </remarks>
    internal sealed record Proof(ValueSet<string> Rows, int Cells) : IComparable<Proof>
    {

        /// <summary>A proof on one atom's cells: the rows both sides read, and the agreement's.</summary>
        public static Proof Of(CellOutcomes outcomes, ValueSet<string> cites) => new(outcomes.Rows.Union(cites), outcomes.Cells.Count);

        /// <summary>A proof of a junction from its sides'.</summary>
        public static Proof Join(Proof a, Proof b) => new(a.Rows.Union(b.Rows), a.Cells + b.Cells);

        /// <inheritdoc />
        public int CompareTo(Proof? other) => other is null ? 1
            : Cells != other.Cells ? Cells.CompareTo(other.Cells)
            : Rows.Count != other.Rows.Count ? Rows.Count.CompareTo(other.Rows.Count)
            : string.CompareOrdinal(Text, other.Text);

        string Text => string.Join("\n", Rows.AsEnumerable());

    }

}
