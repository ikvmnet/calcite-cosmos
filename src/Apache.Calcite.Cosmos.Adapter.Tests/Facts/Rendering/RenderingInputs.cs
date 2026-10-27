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

        /// <summary>The differential's containers, each in a reasoning of its own: what the plants and the recall plan over.</summary>
        public static readonly CosmosContainerMetadata[] Containers = [.. CosmosDifferentialTests.Containers.Select(c => c.WithReasoning(new CosmosReasoning()))];

        /// <summary>Every input statement.</summary>
        /// <param name="partitionedPredicates">How many of the partition run's predicates to plan the statements of.</param>
        /// <param name="reasoning">The reasoning every container is bound to, or <c>null</c> for the suite's shared one.</param>
        public static IEnumerable<Statement> All(int partitionedPredicates, CosmosReasoning? reasoning = null)
        {
            foreach (var statement in Corpus(reasoning))
                yield return statement;

            var containers = CosmosDifferentialTests.Containers.Select(c => reasoning is null ? c : c.WithReasoning(reasoning)).ToArray();
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

        /// <summary>
        /// A parameter atom left residual beside an admitted equality (spec g05), whose weakening is presence alone: the
        /// check run's, beside <see cref="All"/>, which the plan digests plan and so does not grow. A text inequality over
        /// <c>typed</c>'s undeclared label, which the translator does not push against a parameter; over the accessor, whose
        /// text types the parameter, where a raw accessor's <c>ANY</c> leaves it untyped and the statement fails to plan.
        /// </summary>
        /// <param name="reasoning">The reasoning every container is bound to, or <c>null</c> for the suite's shared one.</param>
        public static Statement Parameterised(CosmosReasoning? reasoning = null)
        {
            var containers = CosmosDifferentialTests.Containers.Select(c => reasoning is null ? c : c.WithReasoning(reasoning)).ToArray();
            return Differential("probe", [ParameterisedSql], containers).Single() with { Name = ParameterisedResidual };
        }

        internal const string ParameterisedResidual = "a parameterised residual",
            ParameterisedSql = "SELECT c.\"id\" FROM typed AS c WHERE c.\"$.category\" = 'a' AND JSON_VALUE(c.\"DOC\", '$.label') <> ?";

        /// <summary>The planner corpus, planned through one harness.</summary>
        /// <param name="reasoning">The reasoning every container is bound to, or <c>null</c> for the suite's shared one.</param>
        public static IEnumerable<Statement> Corpus(CosmosReasoning? reasoning = null)
        {
            var harness = new PlannerHarness(reasoning);
            foreach (var query in PlannerQueries.All)
                yield return new("corpus", query.Name, query.Sql, () => harness.PlanToAsync(query.Sql));
        }

        /// <summary>Statements over the given containers, planned as the differential plans them, each named by its index.</summary>
        internal static IEnumerable<Statement> Differential(string input, IReadOnlyList<string> statements, CosmosContainerMetadata[] containers) =>
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
