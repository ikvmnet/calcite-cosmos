using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What the facts a licence admits say of a predicate.
    /// </summary>
    /// <param name="Contradictory">The facts admit no document at all, so the predicate keeps nothing and anything may be said of it.</param>
    /// <param name="AlwaysTrue">The predicate is TRUE of every document.</param>
    /// <param name="AlwaysFalse">The predicate is FALSE, never UNKNOWN, of every document: its negation keeps them all.</param>
    /// <param name="NeverTrue">The predicate is FALSE or UNKNOWN of every document: it keeps none.</param>
    internal readonly record struct OracleVerdict(bool Contradictory, bool AlwaysTrue, bool AlwaysFalse, bool NeverTrue)
    {

        /// <summary>The verdict where nothing is known.</summary>
        public static OracleVerdict Unknown { get; } = default;

        /// <summary>The verdict of facts no document satisfies.</summary>
        public static OracleVerdict Contradicted { get; } = new(true, false, false, true);

        /// <summary>The verdict of a predicate true of every document.</summary>
        public static OracleVerdict Always { get; } = new(false, true, false, false);

        /// <summary>
        /// Gets the verdict as a planner reads it: a predicate that keeps every row, one that keeps none, or neither.
        /// </summary>
        public Truth Truth => Contradictory || NeverTrue ? Truth.Never : AlwaysTrue ? Truth.Always : Truth.Unknown;

    }

}
