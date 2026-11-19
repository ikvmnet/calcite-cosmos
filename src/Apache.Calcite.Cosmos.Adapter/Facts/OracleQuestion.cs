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
        /// What holds of every row the filter keeps, which a strengthening may read — a statement that applies it routed
        /// to the partitions that can hold such a row, and counted as one document where it pins one: every conjunct,
        /// admitted or not. A row Calcite raises on is kept at no truth value, so its contradiction says only that no row
        /// is TRUE, and a request is left unsent under <see cref="Reduce"/>.
        /// </summary>
        Kept,

        /// <summary>
        /// Whether a statement's WHERE keeps nothing and fails on nothing, so that its request need not be sent and the text
        /// may be made <c>FALSE</c>: the WHERE as one conjunction, assumed by a rule only where the gate raises on no
        /// document the declaration admits. The gate is the WHERE, or for a projected <c>CASE</c> condition every predicate
        /// of the projected expression.
        /// </summary>
        Reduce,

        /// <summary>
        /// Inside the arm of a <c>CASE</c> in a conjunct: every sibling, and the conditions the arm is read under, never
        /// the conjunct itself; of them, only what the facts graph reads exactly.
        /// </summary>
        Arm,

        /// <summary>
        /// Whether an arm of a <c>CASE</c> in a conjunct is never read, so that it may be written <c>FALSE</c>: what
        /// <see cref="Arm"/> assumes of the statement as written, a conjunct the deletions drop among it, each assumed by
        /// the same rule only where the gate raises on no document the declaration admits: the WHERE as written, or for a
        /// projected <c>CASE</c> condition every predicate of the projected expression.
        /// </summary>
        NeverRead,

        /// <summary>
        /// An arm of a <c>CASE</c> S writes: the stratum its conjunct renders from, and the conditions it is read under, so
        /// that no sibling's facts reach it and #117's circle stays closed.
        /// </summary>
        RenderArm,

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
