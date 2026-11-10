using System;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What raw columns answer on a key (h12), as S's <c>raw</c> rules state it: the truths stated whole, a null's, a raise's,
    /// a comparison across types; and those of a comparison of the value as one type with the literal, TRUE or FALSE, which
    /// the cell decides. The evaluator on a cell joins the two, the second met with the comparison it evaluates there.
    /// </summary>
    /// <param name="Whole">The truths taken as they are.</param>
    /// <param name="Compared">The truths a comparison on the cell decides among; none where no column compares the value so.</param>
    internal readonly record struct RawAnswer(Truths Whole, Truths Compared)
    {

        /// <summary>The comparison of the value as one type with the literal, TRUE or FALSE, which the cell decides.</summary>
        public static RawAnswer OnTheCell { get; } = new(Truths.None, Truths.True | Truths.False);

        /// <summary>A comparison its column does not compile: the statement fails.</summary>
        public static RawAnswer Fails { get; } = new(Truths.Raises, Truths.None);

        /// <summary>Where no column answers: anything, a raise among it, the lattice's top.</summary>
        public static RawAnswer Unanswered { get; } = new(Truths.Any | Truths.Raises, Truths.None);

        /// <summary>Truths stated whole, as a rule hands them.</summary>
        public static RawAnswer Stated(Truths truths) => new(truths, Truths.None);

        /// <summary>Two columns' answers on one key, joined.</summary>
        public static RawAnswer Join(RawAnswer a, RawAnswer b) => new(a.Whole | b.Whole, a.Compared | b.Compared);

        /// <summary>The answer on a cell: the whole truths, and those compared met with the comparison on the cell.</summary>
        public Truths On(Func<Truths> compared) => Whole | (Compared == Truths.None ? Truths.None : Compared & compared());

        /// <summary>The answer spelled <c>whole</c>, or <c>whole+compared?</c> where the cell decides some.</summary>
        public override string ToString() => Compared == Truths.None ? Whole.ToString() : Whole + "+" + Compared + "?";

    }

}
