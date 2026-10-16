using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A question's licence: what it is for, and the conjunct or the pins it is about. The oracle chooses the facts a
    /// question may be answered from by this, and nothing else, so no caller chooses facts.
    /// </summary>
    /// <param name="Question">What the question is for.</param>
    /// <param name="Conjunct">The conjunct a render, deletion, rewrite or arm question is about, by its index in the statement.</param>
    /// <param name="Pins">The paths a point read is keyed on.</param>
    /// <param name="Given">The conditions an arm of a <c>CASE</c> is read under, or what lies beneath a weakening.</param>
    /// <param name="Beside">The conjuncts a deletion is asked beside: those still kept, or every one.</param>
    internal readonly record struct OracleLicence(OracleQuestion Question, int Conjunct, IReadOnlyList<JsonDocumentPath>? Pins, IReadOnlyList<SqlPredicate>? Given = null, IReadOnlyList<int>? Beside = null)
    {

        /// <summary>Whether a conjunct renders: from the stratum before the one that admitted it.</summary>
        public static OracleLicence RenderConjunct(int conjunct) => new(OracleQuestion.RenderConjunct, conjunct, null);

        /// <summary>
        /// Whether a conjunct can be deleted beside others still kept: from them, never from itself; and where it reaches
        /// the service, only from those that reach it too.
        /// </summary>
        /// <remarks>
        /// A deletion is sound on what any sibling implies, and keeps what the service applies only on what the service
        /// applies: <c>label IS NOT NULL AND label &lt;&gt; 'zzz'</c> over a path declared nothing has the second go down
        /// as a weakening, which does not imply the first, and deleted for the second the exact null test stopped
        /// reaching the service. A conjunct left above deletes as it always did.
        /// </remarks>
        /// <param name="conjunct">The conjunct.</param>
        /// <param name="beside">The conjuncts still kept, the deleted conjunct among them or not; <c>null</c> for every one.</param>
        public static OracleLicence DeleteConjunct(int conjunct, IReadOnlyList<int>? beside = null) => new(OracleQuestion.DeleteConjunct, conjunct, null, null, beside);

        /// <summary>What a rewrite inside a conjunct may lean on: every sibling, never itself.</summary>
        public static OracleLicence Siblings(int conjunct) => new(OracleQuestion.Siblings, conjunct, null);

        /// <summary>What holds of every row the filter keeps: every conjunct, for a strengthening and for routing.</summary>
        public static OracleLicence Kept { get; } = new(OracleQuestion.Kept, -1, null);

        /// <summary>
        /// What the arm of a <c>CASE</c> inside a conjunct may lean on: the siblings, and the conditions it is read under.
        /// </summary>
        /// <remarks>
        /// The arm is read only where its conditions hold, and the conjunct it is part of is decided only where the siblings
        /// do; so a rewrite of the arm on facts that hold there changes the filter on no row it keeps. The conjunct's own
        /// facts are not among them, for the reason a deletion's are not.
        /// </remarks>
        public static OracleLicence Arm(int conjunct, IReadOnlyList<SqlPredicate> conditions) =>
            new(OracleQuestion.Arm, conjunct, null, conditions ?? throw new ArgumentNullException(nameof(conditions)));

        /// <summary>What a point read keyed on these paths may rely on: the conjuncts that pin them, and no other.</summary>
        public static OracleLicence PointRead(IReadOnlyList<JsonDocumentPath> pins) => new(OracleQuestion.PointRead, -1, pins ?? throw new ArgumentNullException(nameof(pins)));

        /// <summary>
        /// What a weakening pushed beside the statement is asked against: every admitted conjunct, and what is applied
        /// beneath them and beside them.
        /// </summary>
        /// <param name="beneath">What the filters beneath apply, and the weakenings already pushed.</param>
        public static OracleLicence Applied(IReadOnlyList<SqlPredicate> beneath) =>
            new(OracleQuestion.Applied, -1, null, beneath ?? throw new ArgumentNullException(nameof(beneath)));

        /// <summary>A projection, over the rows the pushed filter kept.</summary>
        public static OracleLicence Select { get; } = new(OracleQuestion.Select, -1, null);

        /// <summary>A sort, over the rows the pushed filter kept.</summary>
        public static OracleLicence OrderBy { get; } = new(OracleQuestion.OrderBy, -1, null);

        /// <summary>A grouping, over the rows the pushed filter kept.</summary>
        public static OracleLicence GroupBy { get; } = new(OracleQuestion.GroupBy, -1, null);

        /// <summary>A key a join restricts the rows the pushed filter kept by.</summary>
        public static OracleLicence Key { get; } = new(OracleQuestion.Key, -1, null);

        /// <summary>What holds of every document, whatever reads it.</summary>
        public static OracleLicence Declaration { get; } = new(OracleQuestion.Declaration, -1, null);

        /// <summary>
        /// Gets whether the question is a clause's after the filter — a projection, a sort, a grouping, a key — which reads
        /// the rows the filter kept: every admitted conjunct (L1).
        /// </summary>
        public bool ReadsKeptRows => Question is OracleQuestion.Select or OracleQuestion.OrderBy or OracleQuestion.GroupBy or OracleQuestion.Key;

    }

}
