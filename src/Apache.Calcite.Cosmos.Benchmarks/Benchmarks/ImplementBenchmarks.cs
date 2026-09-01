using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Benchmarks.Model;

using BenchmarkDotNet.Attributes;

using org.apache.calcite.rel;

namespace Apache.Calcite.Cosmos.Benchmarks.Benchmarks
{

    /// <summary>
    /// Turns a chosen plan into the statement it executes, and times only that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rendering is not searching. The plan is fixed before the clock starts — it is planned once in
    /// setup and reused — so what is measured is the implementor walking a tree it has already been
    /// given: binding each node's output paths, translating every <c>RexNode</c> in it, collecting
    /// parameters, and deciding what the query can be executed as.
    /// </para>
    /// <para>
    /// Worth separating because it is the part that runs per statement rather than per plan shape,
    /// and because it is where the partition key extraction happens. A statement that renders slowly
    /// costs its caller on every execution that misses a plan cache, which is not true of the search
    /// above it.
    /// </para>
    /// </remarks>
    public class ImplementBenchmarks
    {

        /// <summary>
        /// Gets the statements that have a plan to render.
        /// </summary>
        public static IEnumerable<PlannerQuery> Statements => PlannerQueries.WhollyPushed;

        /// <summary>
        /// Gets or sets the statement being rendered.
        /// </summary>
        [ParamsSource(nameof(Statements))]
        public PlannerQuery Statement { get; set; } = null!;

        RelNode _plan = null!;
        CosmosContainerMetadata _container = null!;

        /// <summary>
        /// Plans the statement, so that only the rendering of it is measured.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            var harness = new PlannerHarness();

            _plan = harness.PlanToCosmos(Statement.Sql);
            _container = PlannerHarness.TableOf(_plan)!.Container;

            _ = PlannerHarness.Implement(_plan, _container);
        }

        /// <summary>
        /// Renders the plan.
        /// </summary>
        /// <returns>The query, whose statement, parameters and routing are all built by the call.</returns>
        [Benchmark(Description = "render")]
        public object Implement() => PlannerHarness.Implement(_plan, _container);

    }

}
