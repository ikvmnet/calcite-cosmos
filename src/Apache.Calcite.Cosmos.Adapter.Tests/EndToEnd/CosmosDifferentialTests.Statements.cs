using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;


namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// The statements the class runs, and the containers it runs them over, for a reader that plans them and never
    /// executes them: the plan digests (<see cref="Planning.PlanDigests"/>).
    /// </summary>
    /// <remarks>
    /// Read off the class's own lists rather than copied, so that a statement added to one is planned there too. Nothing
    /// here touches the fixture, which waits for a service that a planning-only reader does not need.
    /// </remarks>
    public partial class CosmosDifferentialTests
    {

        /// <summary>
        /// Every statement compared or run without parameters: the corpus with its three spreads of probes, the
        /// statements without an oracle, with stated rows, and completed by the reader.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            .. Corpus.Select(s => s.Sql),
            .. WithoutAnOracle.Select(s => s.Sql),
            .. WithStatedRows.Select(s => s.Sql),
            .. CompletedByTheReader.Select(s => s.Sql),
        ];

        /// <summary>
        /// The statements ternary logic partitioning pushes for the first <paramref name="count"/> predicates, each once,
        /// in the order the run first asks them.
        /// </summary>
        internal static IReadOnlyList<string> PartitionStatements(int count) =>
            PartitionedPredicatesOf(count).SelectMany(p => PartitionsOf(p.Vocabulary, p.Predicate).Select(x => x.Sql)).Distinct().ToArray();

        /// <summary>
        /// The metadata of the containers every statement is planned over, in the order their rules are registered.
        /// </summary>
        internal static IReadOnlyList<CosmosContainerMetadata> Containers => CorpusContainers.Select(c => c.Metadata).ToArray();

    }

}
