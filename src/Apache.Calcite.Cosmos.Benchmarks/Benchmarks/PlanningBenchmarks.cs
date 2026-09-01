using System.Collections.Generic;

using Apache.Calcite.Cosmos.Benchmarks.Model;

using BenchmarkDotNet.Attributes;

namespace Apache.Calcite.Cosmos.Benchmarks.Benchmarks
{

    /// <summary>
    /// Plans one statement the way a host asks for it, and times the search.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The headline measurement. Everything before the search — parsing, validation, conversion, the
    /// sub-query rewrite — is inside the number too, because a caller cannot have one without the
    /// others; <see cref="PipelineBenchmarks"/> is what says how much of it is which, and it runs the
    /// same statements so the two tables can be read together.
    /// </para>
    /// <para>
    /// The convention asked for is the asynchronous one rather than the Cosmos one, which is the
    /// difference between measuring a planner and measuring a rule. Asked for Cosmos, the planner has
    /// one way to answer and takes it. Asked for the convention a host actually consumes, it has to
    /// reach both the pushed form and the in-process form, cost them against each other, and choose —
    /// and that comparison is most of the work, most of the risk, and all of the benefit.
    /// </para>
    /// <para>
    /// Split into classes by area rather than run as one parameter list of every statement, because
    /// the whole corpus is a long run and a change to the aggregate rules is not a reason to re-time
    /// the joins. <c>--filter '*Aggregate*'</c> picks one out.
    /// </para>
    /// </remarks>
    public abstract class PlanningBenchmarks
    {

        /// <summary>
        /// Gets the statements this class plans.
        /// </summary>
        public abstract IEnumerable<PlannerQuery> Statements { get; }

        /// <summary>
        /// Gets or sets the statement being planned.
        /// </summary>
        [ParamsSource(nameof(Statements))]
        public PlannerQuery Statement { get; set; } = null!;

        /// <summary>
        /// Gets the harness.
        /// </summary>
        protected PlannerHarness Harness { get; private set; } = null!;

        /// <summary>
        /// Builds the schema and plans the statement once before it is measured.
        /// </summary>
        /// <remarks>
        /// The one pass here is not thrown away for tidiness. Calcite's type initializers run on
        /// first touch and cost seconds, and the metadata handlers a rule asks for are generated and
        /// cached the first time they are asked — so an unwarmed first iteration measures Calcite
        /// starting up rather than the planner working. BenchmarkDotNet's own warmup would absorb it
        /// eventually; doing it here keeps it out of the warmup statistics as well.
        /// </remarks>
        [GlobalSetup]
        public void Setup()
        {
            Harness = new PlannerHarness();
            _ = Harness.PlanToAsync(Statement.Sql);
        }

        /// <summary>
        /// Plans the statement.
        /// </summary>
        /// <returns>The plan, returned so that nothing about it is elided.</returns>
        [Benchmark(Description = "plan")]
        public object Plan() => Harness.PlanToAsync(Statement.Sql);

    }

    /// <summary>
    /// Predicates and projections: which conjuncts reach the service, and what a relational view
    /// over a document costs to plan.
    /// </summary>
    public class FilterPlanningBenchmarks : PlanningBenchmarks
    {

        /// <inheritdoc />
        public override IEnumerable<PlannerQuery> Statements =>
            PlannerQueries.In(PlannerQueryCategory.Filter, PlannerQueryCategory.Projection);

    }

    /// <summary>
    /// Grouping and ordering: the two areas where the service expresses less than SQL does, and the
    /// plan has to make up the difference.
    /// </summary>
    public class AggregatePlanningBenchmarks : PlanningBenchmarks
    {

        /// <inheritdoc />
        public override IEnumerable<PlannerQuery> Statements =>
            PlannerQueries.In(PlannerQueryCategory.Aggregate, PlannerQueryCategory.Sort);

    }

    /// <summary>
    /// Array traversal and search: the pushdowns with no in-process equivalent worth having.
    /// </summary>
    public class TraversalPlanningBenchmarks : PlanningBenchmarks
    {

        /// <inheritdoc />
        public override IEnumerable<PlannerQuery> Statements =>
            PlannerQueries.In(PlannerQueryCategory.Unnest, PlannerQueryCategory.Search);

    }

    /// <summary>
    /// Everything that spans more than one container: joins, set operations and sub-queries.
    /// </summary>
    /// <remarks>
    /// The most expensive area to plan, because it is the only one where the planner is choosing an
    /// order rather than choosing whether a node converts.
    /// </remarks>
    public class JoinPlanningBenchmarks : PlanningBenchmarks
    {

        /// <inheritdoc />
        public override IEnumerable<PlannerQuery> Statements =>
            PlannerQueries.In(PlannerQueryCategory.Join, PlannerQueryCategory.SetOp, PlannerQueryCategory.Subquery);

    }

    /// <summary>
    /// Windows and writes: the two shapes that never enter the Cosmos convention, and whose cost is
    /// therefore the cost of the planner deciding that.
    /// </summary>
    public class WriteAndWindowPlanningBenchmarks : PlanningBenchmarks
    {

        /// <inheritdoc />
        public override IEnumerable<PlannerQuery> Statements =>
            PlannerQueries.In(PlannerQueryCategory.Window, PlannerQueryCategory.Dml);

    }

    /// <summary>
    /// Whole statements, the size an application actually writes.
    /// </summary>
    public class CompositePlanningBenchmarks : PlanningBenchmarks
    {

        /// <inheritdoc />
        public override IEnumerable<PlannerQuery> Statements =>
            PlannerQueries.In(PlannerQueryCategory.Composite);

    }

}
