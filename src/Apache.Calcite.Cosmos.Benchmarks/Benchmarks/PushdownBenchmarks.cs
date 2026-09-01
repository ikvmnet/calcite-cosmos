using System.Collections.Generic;

using Apache.Calcite.Cosmos.Benchmarks.Model;

using BenchmarkDotNet.Attributes;

namespace Apache.Calcite.Cosmos.Benchmarks.Benchmarks
{

    /// <summary>
    /// Plans with the in-process alternative removed, so that only the adapter's rules are timed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked for the Cosmos convention alone, the planner has one family of rules to work with and
    /// no CLR nodes to cost against them. The number is therefore what the adapter costs, separated
    /// from what the comparison costs — and the difference between this and the same statement in
    /// <see cref="PlanningBenchmarks"/> is the price of having an alternative at all.
    /// </para>
    /// <para>
    /// Restricted to the statements that have such a plan. Most do not, and for those the planner
    /// throws rather than answering, which would be a benchmark of an exception.
    /// </para>
    /// </remarks>
    public class PushdownBenchmarks
    {

        /// <summary>
        /// Gets the statements that plan wholly inside the convention.
        /// </summary>
        public static IEnumerable<PlannerQuery> Statements => PlannerQueries.WhollyPushed;

        /// <summary>
        /// Gets or sets the statement being planned.
        /// </summary>
        [ParamsSource(nameof(Statements))]
        public PlannerQuery Statement { get; set; } = null!;

        PlannerHarness _harness = null!;

        /// <summary>
        /// Builds the schema and plans the statement once before it is measured.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _harness = new PlannerHarness();
            _ = _harness.PlanToCosmos(Statement.Sql);
        }

        /// <summary>
        /// Plans the statement for the Cosmos convention.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Description = "push")]
        public object Plan() => _harness.PlanToCosmos(Statement.Sql);

    }

}
