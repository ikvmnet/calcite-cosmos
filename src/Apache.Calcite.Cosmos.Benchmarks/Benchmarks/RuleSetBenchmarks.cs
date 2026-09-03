using System.Linq;

using Apache.Calcite.Cosmos.Adapter;
using Apache.Calcite.Cosmos.Benchmarks.Model;

using BenchmarkDotNet.Attributes;

namespace Apache.Calcite.Cosmos.Benchmarks.Benchmarks
{

    /// <summary>
    /// What every plan pays before the search begins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A planner is built and its rules registered once per statement, not once per connection, so
    /// this is a fixed cost on the shortest query in the corpus as much as on the longest. It is also
    /// the cost that grows with the schema rather than with the statement: rules are per convention
    /// and a convention is per container, so a schema of two hundred containers registers two hundred
    /// times what one container does — before it has looked at the query.
    /// </para>
    /// <para>
    /// Separated into constructing the rules and registering them because the two are fixed by
    /// different things. Construction is the adapter's: <see cref="CosmosRules.GetRules"/> builds
    /// fresh instances each call, several of which capture their container's metadata. Registration
    /// is Volcano's, and its cost is in indexing each rule's operand pattern so that matches can be
    /// found later.
    /// </para>
    /// </remarks>
    public class RuleSetBenchmarks
    {

        PlannerHarness _harness = null!;
        CosmosConvention _convention = null!;

        /// <summary>
        /// Builds the schema and does each measured operation once before it is measured.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _harness = new PlannerHarness();
            _convention = _harness.Tables["products"].Convention;

            _ = _harness.PlanToAsync("""SELECT c."id" FROM products AS c WHERE c."category" = 'x'""");
            _ = CosmosRules.GetRules(_convention).ToList();
        }

        /// <summary>
        /// Builds one container's rule instances, registering none of them.
        /// </summary>
        /// <remarks>
        /// Typed <see cref="object"/> rather than a list of rules, as every benchmark here is.
        /// BenchmarkDotNet generates a C# file that calls the method and assigns its result, and
        /// that file is compiled against the benchmark assembly alone — a signature naming a type
        /// from one of the IKVM-produced assemblies does not resolve there, and the run fails at the
        /// generated build rather than at anything to do with the measurement.
        /// </remarks>
        /// <returns>The rules.</returns>
        [Benchmark(Description = "build one container's rules")]
        public object BuildRules() => CosmosRules.GetRules(_convention).ToList();

        /// <summary>
        /// Constructs a planner with the trait definitions and no rules.
        /// </summary>
        /// <returns>The planner.</returns>
        [Benchmark(Baseline = true, Description = "planner, no rules")]
        public object Planner() => PlannerHarness.CreatePlanner();

        /// <summary>
        /// Constructs a planner and registers every container's Cosmos rules.
        /// </summary>
        /// <returns>The planner.</returns>
        [Benchmark(Description = "planner + Cosmos rules")]
        public object PlannerWithCosmosRules()
        {
            var planner = PlannerHarness.CreatePlanner();
            _harness.AddCosmosRules(planner);
            return planner;
        }

        /// <summary>
        /// Constructs a planner and registers the asynchronous convention's rules.
        /// </summary>
        /// <returns>The planner.</returns>
        [Benchmark(Description = "planner + CLR rules")]
        public object PlannerWithAsyncRules()
        {
            var planner = PlannerHarness.CreatePlanner();
            PlannerHarness.AddAsyncRules(planner);
            return planner;
        }

        /// <summary>
        /// Constructs a planner and registers everything a plan is searched with.
        /// </summary>
        /// <returns>The planner.</returns>
        [Benchmark(Description = "planner + both")]
        public object PlannerWithEverything()
        {
            var planner = PlannerHarness.CreatePlanner();
            _harness.AddCosmosRules(planner);
            PlannerHarness.AddAsyncRules(planner);
            return planner;
        }

        /// <summary>
        /// Builds the schema, the type factory and the catalogue reader.
        /// </summary>
        /// <remarks>
        /// Once per connection rather than once per statement, so it is not on the path the rows
        /// above are — but it is what a short-lived process pays before its first query, and it is
        /// the other thing that grows with the number of containers.
        /// </remarks>
        /// <returns>The harness.</returns>
        [Benchmark(Description = "schema and catalogue")]
        public object Schema() => new PlannerHarness();

    }

}
