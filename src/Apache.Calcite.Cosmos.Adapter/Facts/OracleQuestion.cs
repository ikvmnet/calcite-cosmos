namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What a question about a filter is for, which decides the facts it may be answered from.
    /// </summary>
    internal enum OracleQuestion
    {

        /// <summary>Whether a conjunct renders, and how: the stratum before the one that admitted it (L2).</summary>
        RenderConjunct,

        /// <summary>
        /// Whether a conjunct can be deleted: the conjuncts kept beside it, never itself, and of those, where it reaches the
        /// service, only the ones that reach it too (L3).
        /// </summary>
        DeleteConjunct,

        /// <summary>
        /// What a rewrite inside a conjunct may lean on, a comparison lowered onto its stored form: every sibling, never
        /// the conjunct itself (L3).
        /// </summary>
        Siblings,

        /// <summary>
        /// What holds of every row the filter keeps, which a strengthening may read — the filter made <c>FALSE</c> where
        /// nothing satisfies it, a statement that applies it routed to the partitions that can hold such a row, and
        /// counted as one document where it pins one: every conjunct, admitted or not.
        /// </summary>
        Kept,

        /// <summary>
        /// Inside the arm of a <c>CASE</c> in a conjunct: every sibling, and the conditions the arm is read under, never
        /// the conjunct itself.
        /// </summary>
        Arm,

        /// <summary>What a point read may rely on, which applies no predicate: the pins alone.</summary>
        PointRead,

        /// <summary>
        /// Whether a weakening adds nothing to what the statement applies beside it: every admitted conjunct, and what
        /// the filters beneath and the weakenings already pushed apply.
        /// </summary>
        Applied,

        /// <summary>A projection: the rows the pushed filter kept (L1).</summary>
        Select,

        /// <summary>A sort: the rows the pushed filter kept (L1).</summary>
        OrderBy,

        /// <summary>A grouping: the rows the pushed filter kept (L1).</summary>
        GroupBy,

        /// <summary>
        /// A key a join restricts the rows by, written beside the pushed filter: the rows it kept (L1). The restriction
        /// has to keep every row the filter keeps whose key a batch could match, and nothing it drops is asked about.
        /// </summary>
        Key,

        /// <summary>
        /// What holds of every document whatever reads it (L0): a residual, evaluated in process, and a question one
        /// node asks of another, which both walks of a plan must answer alike.
        /// </summary>
        Declaration,

    }

}
