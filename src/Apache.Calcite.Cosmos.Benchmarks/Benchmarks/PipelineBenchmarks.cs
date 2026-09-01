using System.Collections.Generic;

using Apache.Calcite.Cosmos.Benchmarks.Model;

using BenchmarkDotNet.Attributes;

namespace Apache.Calcite.Cosmos.Benchmarks.Benchmarks
{

    /// <summary>
    /// Where the time between a statement and a plan actually goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each measurement is a prefix of the one below it — parse, then parse and validate, then also
    /// convert, then also rewrite, then also search — so a stage's own cost is the difference between
    /// two adjacent rows. Prefixes rather than isolated stages because the stages are not separable
    /// in the way that would allow it: validation rewrites the parse tree in place, so validating a
    /// cached tree twice does not validate the same tree twice, and the honest way to hold that
    /// constant is to start from the text every time.
    /// </para>
    /// <para>
    /// It matters which is which. A planner that looks slow because Calcite's parser is slow needs no
    /// work on its rules, and a statement whose cost is all in the search is the one where a rule
    /// that fires needlessly is worth finding. Without this table every improvement is attributed to
    /// whatever was changed last.
    /// </para>
    /// </remarks>
    public class PipelineBenchmarks
    {

        /// <summary>
        /// Gets the statements the stages are measured over.
        /// </summary>
        /// <remarks>
        /// One from each end of the corpus rather than all of it: the proportions are what is being
        /// read here, and they do not change between two statements that reach the same rules. A
        /// short predicate, a wholly pushed page, an aggregate the service cannot express, a
        /// multi-container join, and the largest statement in the corpus.
        /// </remarks>
        public static IEnumerable<PlannerQuery> Statements => new[]
        {
            PlannerQueries.Get("Filter.PartitionPin"),
            PlannerQueries.Get("Filter.DeepConjunction"),
            PlannerQueries.Get("Project.OverFilterAndSort"),
            PlannerQueries.Get("Aggregate.Rollup"),
            PlannerQueries.Get("Join.FourWayWithFilters"),
            PlannerQueries.Get("Subquery.ScalarCorrelated"),
            PlannerQueries.Get("Composite.CatalogueReport"),
        };

        /// <summary>
        /// Gets or sets the statement being measured.
        /// </summary>
        [ParamsSource(nameof(Statements))]
        public PlannerQuery Statement { get; set; } = null!;

        PlannerHarness _harness = null!;

        /// <summary>
        /// Builds the schema and runs the statement through once before it is measured.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _harness = new PlannerHarness();
            _ = _harness.PlanToAsync(Statement.Sql);
        }

        /// <summary>
        /// Parses the statement.
        /// </summary>
        /// <returns>The parse tree.</returns>
        [Benchmark(Baseline = true, Description = "parse")]
        public object Parse() => _harness.Parse(Statement.Sql);

        /// <summary>
        /// Parses and validates the statement.
        /// </summary>
        /// <returns>The validated tree.</returns>
        [Benchmark(Description = "+ validate")]
        public object Validate() => _harness.Validate(_harness.Parse(Statement.Sql));

        /// <summary>
        /// Parses, validates and converts the statement to a logical tree.
        /// </summary>
        /// <returns>The logical tree.</returns>
        [Benchmark(Description = "+ convert")]
        public object Convert() => _harness.ToRel(Statement.Sql);

        /// <summary>
        /// Adds the rewrites a host applies before the search.
        /// </summary>
        /// <returns>The tree the search is given.</returns>
        [Benchmark(Description = "+ rewrite")]
        public object Rewrite() => _harness.ToLogical(Statement.Sql);

        /// <summary>
        /// Adds the search itself, which is the whole of what a host pays.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Description = "+ search")]
        public object Search() => _harness.PlanToAsync(Statement.Sql);

    }

}
