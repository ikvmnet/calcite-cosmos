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

        /// <summary>
        /// A <c>WHERE</c> that may hold a comparison of a path with a parameter whose exactness at the service the value
        /// decides: where it is no anchor, the reader sends a weaker comparison and keeps only the rows its conjunct
        /// keeps (<see cref="Sql.CosmosRecheck"/>). So the rows the service returns have to be the rows the
        /// <c>WHERE</c> filtered, one each, read by the converter: a grouping, a <c>DISTINCT</c>, a rank and a lookup
        /// cannot be folded onto it, and decline.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A one-way approximation, by design.</b> Said of every such comparison the declaration does not prove exact
        /// (<see cref="Sql.CosmosRexTranslator.MayRecheck"/>), which is more than renders anchored: a sibling conjunct may
        /// prove more in its stratum, and reading the strata in the walk would cost a scope per walk. A consumer declined
        /// where it need not have been costs that consumer's clause in process; one folded where it should not have been
        /// would answer wrongly, so the walk errs one way.
        /// </para>
        /// <para>
        /// <b>Where it is asked, and where it is enforced.</b> Each rule that folds a consumer onto the rows of a filter
        /// declines on it — the aggregate's, the rank's and the lookup join's — and the filter's own rule declines onto a
        /// <c>DISTINCT</c> for the same reason, asking the condition as written. Implementation enforces it once more,
        /// where breaking it would answer wrongly rather than fail: a program that would recheck the rows of a grouping,
        /// a <c>DISTINCT</c> or a rank throws (<c>CosmosImplementor.Build</c>), as does the converter over a lookup's
        /// statement. A new consumer of a filter's rows has to ask it too.
        /// </para>
        /// </remarks>
        Recheck = 1 << 4,

    }

}
