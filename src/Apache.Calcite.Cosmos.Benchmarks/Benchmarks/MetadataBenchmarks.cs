using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Benchmarks.Model;

using BenchmarkDotNet.Attributes;

using org.apache.calcite.jdbc;
using org.apache.calcite.rel.type;

namespace Apache.Calcite.Cosmos.Benchmarks.Benchmarks
{

    /// <summary>
    /// The questions the rules ask of a container, timed on their own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None of these is a plan. They are the lookups underneath one — is this path indexed, what is
    /// this table's row type, what does the service say about its size, does a composite index cover
    /// this ordering — and every rule that fires asks several of them. A microsecond here is
    /// multiplied by the number of rule invocations in a search, which is why they are worth seeing
    /// separately from the search that calls them.
    /// </para>
    /// <para>
    /// Two containers, because the answers come from different code. <c>products</c> declares an
    /// indexing policy and every question about a path is a pattern match against it;
    /// <c>customers</c> declares only inclusions, which is the short path. The default policy —
    /// nothing declared at all — never matches a pattern and answers yes immediately, and is not
    /// what a real container looks like.
    /// </para>
    /// </remarks>
    public class MetadataBenchmarks
    {

        static readonly IReadOnlyList<CosmosSortKey> CoveredOrdering = new[]
        {
            new CosmosSortKey("/category", false),
            new CosmosSortKey("/_ts", true),
        };

        static readonly IReadOnlyList<CosmosSortKey> UncoveredOrdering = new[]
        {
            new CosmosSortKey("/_etag", false),
            new CosmosSortKey("/id", false),
        };

        PlannerHarness _harness = null!;
        CosmosTable _products = null!;
        JavaTypeFactoryImpl _typeFactory = null!;
        RelDataType _rowType = null!;

        /// <summary>
        /// Builds the schema and does each measured operation once before it is measured.
        /// </summary>
        [GlobalSetup]
        public void Setup()
        {
            _harness = new PlannerHarness();
            _products = _harness.Tables["products"];
            _typeFactory = new JavaTypeFactoryImpl();
            _rowType = _products.getRowType(_typeFactory);

            _ = CosmosImplementor.BindFields(_rowType);
            _ = _products.getStatistic();
        }

        /// <summary>
        /// Asks whether an indexed path is indexed, against a policy with both inclusions and
        /// exclusions.
        /// </summary>
        /// <returns>The answer.</returns>
        [Benchmark(Baseline = true, Description = "IsPathIndexed, included")]
        public bool IsPathIndexedIncluded() => BenchmarkSchema.Products.IsPathIndexed("/inventory/quantity");

        /// <summary>
        /// Asks the same of a path the policy excludes, which is the branch that has to compare the
        /// specificity of two matching patterns.
        /// </summary>
        /// <returns>The answer.</returns>
        [Benchmark(Description = "IsPathIndexed, excluded")]
        public bool IsPathIndexedExcluded() => BenchmarkSchema.Products.IsPathIndexed("/payload/blob");

        /// <summary>
        /// Asks it of the container whose policy excludes nearly everything.
        /// </summary>
        /// <returns>The answer.</returns>
        [Benchmark(Description = "IsPathIndexed, cold container")]
        public bool IsPathIndexedArchive() => BenchmarkSchema.Archive.IsPathIndexed("/reason");

        /// <summary>
        /// Asks a composite index whether it answers an ordering it covers.
        /// </summary>
        /// <returns>The answer.</returns>
        [Benchmark(Description = "composite index, covered")]
        public bool CompositeCovered() => BenchmarkSchema.Products.CompositeIndexes[0].Supports(CoveredOrdering);

        /// <summary>
        /// Asks it about an ordering no index covers, which is the answer a sort rule needs before it
        /// may decline.
        /// </summary>
        /// <returns>The answer.</returns>
        [Benchmark(Description = "composite index, uncovered")]
        public bool CompositeUncovered() => BenchmarkSchema.Products.CompositeIndexes[0].Supports(UncoveredOrdering);

        /// <summary>
        /// Derives the table's row type.
        /// </summary>
        /// <remarks>
        /// Cheap on a warm type factory, which interns the result, and this measures it warm because
        /// that is how a planner meets it.
        /// </remarks>
        /// <returns>The row type.</returns>
        [Benchmark(Description = "row type")]
        public object RowType() => _products.getRowType(_typeFactory);

        /// <summary>
        /// Derives the table's statistic: its row count and the keys the declared metadata supports.
        /// </summary>
        /// <returns>The statistic.</returns>
        [Benchmark(Description = "statistic")]
        public object Statistic() => _products.getStatistic();

        /// <summary>
        /// Binds a row type to the document paths each of its columns reads.
        /// </summary>
        /// <remarks>
        /// What the implementor does first for every node it visits, and what every translated
        /// expression is resolved against.
        /// </remarks>
        /// <returns>The bound paths.</returns>
        [Benchmark(Description = "bind fields")]
        public object BindFields() => CosmosImplementor.BindFields(_rowType);

    }

}
