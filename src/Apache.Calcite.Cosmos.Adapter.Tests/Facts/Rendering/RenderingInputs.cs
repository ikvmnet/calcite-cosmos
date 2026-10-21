using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;
using Apache.Calcite.Cosmos.Adapter.Tests.Planning;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rel;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.util;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Every statement the rendering check reads, each planned as the test that runs it plans it: the planner corpus, the
    /// differential's statements without parameters, ternary logic partitioning's, and the one kind probe the differential
    /// leaves out. Read off those tests' own lists, in a fixed order; the plan digests (<see cref="PlanDigests"/>) plan the
    /// same.
    /// </summary>
    internal static class RenderingInputs
    {

        /// <summary>Every input statement.</summary>
        /// <param name="partitionedPredicates">How many of the partition run's predicates to plan the statements of.</param>
        /// <param name="generations">The generations every container reasons in, or <c>null</c> for their own.</param>
        public static IEnumerable<Statement> All(int partitionedPredicates, JsonFactGenerations? generations = null)
        {
            foreach (var statement in Corpus(generations))
                yield return statement;

            var containers = CosmosDifferentialTests.Containers.Select(c => generations is null ? c : c.WithFactGenerations(generations)).ToArray();
            foreach (var statement in Differential("differential", CosmosDifferentialTests.Statements, containers))
                yield return statement;

            foreach (var statement in Differential("partitions", CosmosDifferentialTests.PartitionStatements(partitionedPredicates), containers))
                yield return statement;

            var kinds = containers.Single(c => c.Name == "kinds");
            foreach (var (name, where) in CosmosPushdownProbeTests.KindProbes.Where(p => p.Where.Contains("IS_DEFINED", StringComparison.Ordinal)))
            {
                var sql = $"SELECT c.\"id\" FROM kinds AS c WHERE {where}";
                yield return new("probe", name, sql, () => CosmosPushdownProbeTests.Plan(sql, kinds));
            }
        }

        /// <summary>The planner corpus, planned through one harness.</summary>
        /// <param name="generations">The generations every container reasons in, or <c>null</c> for their own.</param>
        public static IEnumerable<Statement> Corpus(JsonFactGenerations? generations = null)
        {
            var harness = new PlannerHarness(generations);
            foreach (var query in PlannerQueries.All)
                yield return new("corpus", query.Name, query.Sql, () => harness.PlanToAsync(query.Sql));
        }

        static IEnumerable<Statement> Differential(string input, IReadOnlyList<string> statements, CosmosContainerMetadata[] containers) =>
            statements.Select((sql, i) => new Statement(input, i.ToString(CultureInfo.InvariantCulture), sql, () => Differential(sql, containers)));

        /// <summary>
        /// The planning half of <c>CosmosDifferentialTests.Run</c> with the pushdown rules: the same schema, operators,
        /// traits, rules and calc pass, and nothing executed.
        /// </summary>
        static RelNode Differential(string sql, IReadOnlyList<CosmosContainerMetadata> containers) => PlannerHarness.Plan(sql, containers, SqlOperatorTables.chain(SqlStdOperatorTable.instance(),
            SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(java.util.EnumSet.of(SqlLibrary.MYSQL, SqlLibrary.SPARK, SqlLibrary.HIVE, SqlLibrary.BIG_QUERY))),
            collations: true, calc: true);

        /// <summary>An input statement: which input, its name or index, its text, and how to plan it.</summary>
        internal sealed record Statement(string Input, string Name, string Sql, Func<RelNode> Plan);

    }

}
