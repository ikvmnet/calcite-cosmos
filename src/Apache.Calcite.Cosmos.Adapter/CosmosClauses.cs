using System;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// The clauses a subtree has already written into the statement it is building.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A statement has one of each and Cosmos has no derived table to nest a second in, so what a
    /// subtree has written is what decides which operators may still be folded onto it. Derived on
    /// the same walk as the binding and for the same reason: a rule answering this some other way
    /// converts an operator the implementor then refuses — after the planner has chosen the plan,
    /// so the query fails rather than falling back — or, where the clauses merely reorder instead
    /// of colliding, one it renders into a statement that answers a different question.
    /// </para>
    /// <para>
    /// There is no member for <c>GROUP BY</c>. A grouping aggregate's output is computed and binds
    /// nothing, so the walk declines it and no subtree that binds at all can be carrying one; an
    /// aggregate that computes nothing is a <c>DISTINCT</c>, which has its own member.
    /// </para>
    /// </remarks>
    [Flags]
    public enum CosmosClauses
    {

        /// <summary>
        /// The subtree has written nothing; anything may still be folded onto it.
        /// </summary>
        None = 0,

        /// <summary>
        /// The <c>SELECT</c> list, written by a projection or by a <c>DISTINCT</c>.
        /// </summary>
        Projection = 1 << 0,

        /// <summary>
        /// <c>SELECT DISTINCT</c>, which de-duplicates what that <c>SELECT</c> constructs and so
        /// implies <see cref="Projection"/>.
        /// </summary>
        Distinct = 1 << 1,

        /// <summary>
        /// <c>ORDER BY</c>.
        /// </summary>
        OrderBy = 1 << 2,

        /// <summary>
        /// <c>OFFSET</c>/<c>LIMIT</c>, applied after <c>ORDER BY</c> and before nothing.
        /// </summary>
        RowLimit = 1 << 3,

    }

}
