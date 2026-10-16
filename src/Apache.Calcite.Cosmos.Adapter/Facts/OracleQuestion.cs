namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What a question about a filter is for, which decides the facts it may be answered from.
    /// </summary>
    internal enum OracleQuestion
    {

        /// <summary>Whether a conjunct renders, and how: the stratum before the one that admitted it (L2).</summary>
        RenderConjunct,

        /// <summary>Whether a conjunct can be deleted: every sibling, never itself (L3).</summary>
        DeleteConjunct,

        /// <summary>Whether a conjunct says nothing its siblings do not: every sibling, never itself (L3).</summary>
        Redundant,

        /// <summary>
        /// What holds of every row the filter keeps, which a strengthening may read — the filter made <c>FALSE</c> where
        /// nothing satisfies it: every conjunct, admitted or not.
        /// </summary>
        Kept,

        /// <summary>
        /// Inside the arm of a <c>CASE</c> in a conjunct: every sibling, and the conditions the arm is read under, never
        /// the conjunct itself.
        /// </summary>
        Arm,

        /// <summary>What a point read may rely on, which applies no predicate: the pins alone.</summary>
        PointRead,

        /// <summary>A projection: the rows the pushed filter kept (L1).</summary>
        Select,

        /// <summary>A sort: the rows the pushed filter kept (L1).</summary>
        OrderBy,

        /// <summary>A grouping: the rows the pushed filter kept (L1).</summary>
        GroupBy,

        /// <summary>A residual filter, evaluated in process: what holds of every document (L0).</summary>
        Residual,

        /// <summary>Routing to partitions: what holds of every document (L0).</summary>
        Route,

    }

}
