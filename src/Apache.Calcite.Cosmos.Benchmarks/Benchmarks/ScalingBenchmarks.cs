using Apache.Calcite.Cosmos.Benchmarks.Model;

using BenchmarkDotNet.Attributes;

namespace Apache.Calcite.Cosmos.Benchmarks.Benchmarks
{

    /// <summary>
    /// How planning time grows with the size of a predicate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four shapes that all get bigger and get bigger differently. A conjunction is a list the rules
    /// walk; a disjunction is one expression that has to be translated whole or refused whole; an
    /// <c>IN</c> list is a conjunct the converter expands into an OR ladder before any rule sees it;
    /// and a projection widens the row type every node above it carries. A regression in any one of
    /// them is invisible in the others.
    /// </para>
    /// <para>
    /// The statements are built in setup, so what is measured is planning and not string
    /// concatenation.
    /// </para>
    /// </remarks>
    public class PredicateScalingBenchmarks
    {

        /// <summary>
        /// Gets or sets how many of whatever the statement is made of.
        /// </summary>
        [Params(1, 4, 16, 64)]
        public int Size { get; set; }

        PlannerHarness _harness = null!;
        string _conjuncts = null!;
        string _disjuncts = null!;
        string _pointReads = null!;
        string _projections = null!;

        /// <summary>
        /// Builds the statements and plans each of them once before they are measured.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _harness = new PlannerHarness();

            _conjuncts = PlannerQueryGenerator.Conjuncts(Size);
            _disjuncts = PlannerQueryGenerator.Disjuncts(Size);
            _pointReads = PlannerQueryGenerator.PointReadSet(Size);
            _projections = PlannerQueryGenerator.Projections(Size);

            _ = _harness.PlanToAsync(_conjuncts);
            _ = _harness.PlanToAsync(_disjuncts);
            _ = _harness.PlanToAsync(_pointReads);
            _ = _harness.PlanToAsync(_projections);
        }

        /// <summary>
        /// Plans a predicate of that many translatable conjuncts.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Baseline = true, Description = "conjuncts")]
        public object Conjuncts() => _harness.PlanToAsync(_conjuncts);

        /// <summary>
        /// Plans a predicate of that many disjoined branches.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Description = "disjuncts")]
        public object Disjuncts() => _harness.PlanToAsync(_disjuncts);

        /// <summary>
        /// Plans a point-read set over that many ids.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Description = "point reads")]
        public object PointReads() => _harness.PlanToAsync(_pointReads);

        /// <summary>
        /// Plans a projection of that many typed columns.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Description = "projections")]
        public object Projections() => _harness.PlanToAsync(_projections);

    }

    /// <summary>
    /// How planning time grows with the shape of a statement rather than the size of an expression.
    /// </summary>
    /// <remarks>
    /// These are the dimensions that add nodes rather than terms, and they are the ones that grow
    /// fastest: each level of nesting is a subtree the merge rules revisit, each traversal is a
    /// correlate the planner has to convert and cost, and each union branch is an input whose
    /// alternatives are enumerated independently.
    /// </remarks>
    public class ShapeScalingBenchmarks
    {

        /// <summary>
        /// Gets or sets how many levels, traversals or branches.
        /// </summary>
        [Params(2, 4, 8, 16)]
        public int Size { get; set; }

        PlannerHarness _harness = null!;
        string _nesting = null!;
        string _unnests = null!;
        string _union = null!;

        /// <summary>
        /// Builds the statements and plans each of them once before they are measured.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _harness = new PlannerHarness();

            _nesting = PlannerQueryGenerator.Nesting(Size);
            _unnests = PlannerQueryGenerator.Unnests(Size);
            _union = PlannerQueryGenerator.UnionBranches(Size);

            _ = _harness.PlanToAsync(_nesting);
            _ = _harness.PlanToAsync(_unnests);
            _ = _harness.PlanToAsync(_union);
        }

        /// <summary>
        /// Plans that many nested derived tables.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Baseline = true, Description = "nesting")]
        public object Nesting() => _harness.PlanToAsync(_nesting);

        /// <summary>
        /// Plans that many array traversals of one document.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Description = "traversals")]
        public object Unnests() => _harness.PlanToAsync(_unnests);

        /// <summary>
        /// Plans a union of that many pushed branches.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Description = "union branches")]
        public object UnionBranches() => _harness.PlanToAsync(_union);

    }

    /// <summary>
    /// How planning time grows with the number of containers joined, with and without the planner
    /// being allowed to swap a join's sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pair is the measurement. On a host's own rule set the order of a chain is whatever the
    /// statement said and the planner only chooses an implementation per join, which is linear;
    /// registering commutation makes each join a choice and the chain a product of them.
    /// </para>
    /// <para>
    /// For this adapter that choice is not cosmetic. A join whose build side is a container keyed by
    /// the joined column can be answered by fetching the keys the probe side supplies, and a join
    /// with the sides the other way round reads the container — so the difference between the two
    /// rows below is what it would cost to let the planner find that, on a schema where it is worth
    /// finding.
    /// </para>
    /// </remarks>
    public class JoinScalingBenchmarks
    {

        /// <summary>
        /// Gets or sets how many containers are in the chain.
        /// </summary>
        [Params(2, 3, 4, 5, 6)]
        public int Size { get; set; }

        PlannerHarness _harness = null!;
        string _chain = null!;

        /// <summary>
        /// Builds the statement and plans it once each way before it is measured.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _harness = new PlannerHarness();
            _chain = PlannerQueryGenerator.Joins(Size);

            _ = _harness.PlanToAsync(_chain);
            _ = _harness.PlanToAsync(_chain, reorderJoins: true);
        }

        /// <summary>
        /// Plans the chain with the rule set a host has.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Baseline = true, Description = "chain")]
        public object Chain() => _harness.PlanToAsync(_chain);

        /// <summary>
        /// Plans the chain with the sides of every join free to swap.
        /// </summary>
        /// <returns>The plan.</returns>
        [Benchmark(Description = "chain, sides free")]
        public object ChainCommuted() => _harness.PlanToAsync(_chain, reorderJoins: true);

    }

}
