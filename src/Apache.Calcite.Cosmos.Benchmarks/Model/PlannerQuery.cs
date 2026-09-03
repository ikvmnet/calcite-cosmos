namespace Apache.Calcite.Cosmos.Benchmarks.Model
{

    /// <summary>
    /// What a statement in the corpus is for.
    /// </summary>
    /// <remarks>
    /// A benchmark filters on this rather than on a name, so that a run can ask for the aggregate
    /// rules without also planning every join.
    /// </remarks>
    public enum PlannerQueryCategory
    {

        /// <summary>Predicates: what pushes, what splits, and what pins a partition key.</summary>
        Filter,

        /// <summary>Projections, including the casts a relational view over documents is made of.</summary>
        Projection,

        /// <summary>Grouping and aggregation, including the forms the service cannot express whole.</summary>
        Aggregate,

        /// <summary>Ordering, paging, and the transposes that decide whether either pushes.</summary>
        Sort,

        /// <summary>Array traversal, and predicates over the traversed element.</summary>
        Unnest,

        /// <summary>Full text and vector search, and ordering by a score.</summary>
        Search,

        /// <summary>Joins, which leave the convention — the question is what reaches the statement.</summary>
        Join,

        /// <summary>Set operations, which Cosmos has none of and the plan performs here.</summary>
        SetOp,

        /// <summary>Subqueries, correlated and not, and the rewrites the converter makes of them.</summary>
        Subquery,

        /// <summary>Window functions, which never push and sit over whatever does.</summary>
        Window,

        /// <summary>Writes, which never enter the convention at all.</summary>
        Dml,

        /// <summary>Whole statements, the size an application actually writes.</summary>
        Composite,

    }

    /// <summary>
    /// One statement in the corpus.
    /// </summary>
    /// <param name="Name">A stable identifier, used as the benchmark's parameter label.</param>
    /// <param name="Category">What the statement is in the corpus for.</param>
    /// <param name="Sql">The statement.</param>
    /// <param name="Note">Why it is here — what about the planner it is meant to reach.</param>
    /// <remarks>
    /// <see cref="ToString"/> is the name, because BenchmarkDotNet labels a parameter column with it
    /// and a column of SQL is unreadable.
    /// </remarks>
    public sealed record PlannerQuery(string Name, PlannerQueryCategory Category, string Sql, string Note)
    {

        /// <inheritdoc />
        public override string ToString() => Name;

    }

}
