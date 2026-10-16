using System;
using System.Collections.Generic;
using System.Linq;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A question's licence: what it is for, and the conjunct or the pins it is about. The oracle chooses the facts a
    /// question may be answered from by this, and nothing else, so no caller chooses facts.
    /// </summary>
    /// <param name="Question">What the question is for.</param>
    /// <param name="Conjunct">The conjunct a render, deletion, redundancy or arm question is about, by its index in the statement.</param>
    /// <param name="Pins">The paths a point read is keyed on.</param>
    /// <param name="Given">The conditions an arm of a <c>CASE</c> is read under.</param>
    internal readonly record struct OracleLicence(OracleQuestion Question, int Conjunct, IReadOnlyList<JsonDocumentPath>? Pins, IReadOnlyList<SqlPredicate>? Given = null)
    {

        /// <summary>Whether a conjunct renders: from the stratum before the one that admitted it.</summary>
        public static OracleLicence RenderConjunct(int conjunct) => new(OracleQuestion.RenderConjunct, conjunct, null);

        /// <summary>Whether a conjunct can be deleted: from every sibling, never from itself.</summary>
        public static OracleLicence DeleteConjunct(int conjunct) => new(OracleQuestion.DeleteConjunct, conjunct, null);

        /// <summary>Whether a conjunct is redundant: from every sibling, never from itself.</summary>
        public static OracleLicence Redundant(int conjunct) => new(OracleQuestion.Redundant, conjunct, null);

        /// <summary>What holds of every row the filter keeps: every conjunct, for a strengthening.</summary>
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

        /// <summary>A projection, over the rows the pushed filter kept.</summary>
        public static OracleLicence Select { get; } = new(OracleQuestion.Select, -1, null);

        /// <summary>A sort, over the rows the pushed filter kept.</summary>
        public static OracleLicence OrderBy { get; } = new(OracleQuestion.OrderBy, -1, null);

        /// <summary>A grouping, over the rows the pushed filter kept.</summary>
        public static OracleLicence GroupBy { get; } = new(OracleQuestion.GroupBy, -1, null);

        /// <summary>A residual filter: what holds of every document.</summary>
        public static OracleLicence Residual { get; } = new(OracleQuestion.Residual, -1, null);

        /// <summary>Routing: what holds of every document.</summary>
        public static OracleLicence Route { get; } = new(OracleQuestion.Route, -1, null);

    }

}
