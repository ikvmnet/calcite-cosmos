using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Data;
using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// Statements that plan through the connection the README opens, its host's planner with Calcite's default rules,
    /// explained and never run: two the reduce once masked, which did not plan once nothing stood for an empty answer, and
    /// the shapes the reading of Calcite's plan answered with no rows and no request where Calcite fails.
    /// </summary>
    /// <remarks>
    /// The same container is planned declared and declared nothing. The table answers no request: nothing here is sent.
    /// </remarks>
    public class CosmosConnectionPlanningTests
    {

        /// <summary>An executor that is never asked: these statements are explained, not run.</summary>
        sealed class NoExecutor : ICosmosQueryExecutor
        {

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Nothing here is run.");

        }

        /// <summary><c>kind</c> is <c>A</c> or <c>B</c>, <c>body</c> any string, <c>n</c> 1 or 2.</summary>
        const string Schema = """{ "type": "object", "required": ["kind", "body", "n"], "properties": { "kind": { "enum": ["A", "B"] }, "body": { "type": "string" }, "n": { "enum": [1, 2] } } }""";

        static string Explain(string sql, bool declared, string schema = Schema)
        {
            var metadata = TestReasoning.Bind(new CosmosContainerMetadata("items", ["/category"]));
            if (declared)
                metadata = metadata.WithFacts(CosmosSchemaRecognition.ReadFrom(JsonNode.Parse(schema)!));

            var table = new CosmosTable(metadata, new NoExecutor());
            using var source = new CalciteDataSourceBuilder(new CalciteConnectionStringBuilder
            {
                Model = "inline:{\"version\":\"1.0\",\"defaultSchema\":\"adhoc\",\"schemas\":[{\"name\":\"adhoc\"}]}",
                Schema = "adhoc",
                CaseSensitive = true,
            }.ConnectionString)
                .ConfigureRootSchema(root => root.add("items", table))
                .Build();

            using var connection = source.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql.Replace(" items AS ", " \"items\" AS ");
            return ((string?)command.ExecuteScalar() ?? "").Trim().Replace("\r\n", "\n");
        }

        /// <summary>
        /// A scalar sub-query in a select list nothing above reads: the host's field trimmer drops its column and keeps the
        /// correlation variable its projection declares, and no rule converted a projection declaring one, Calcite's merge
        /// refusing it too, so the statement did not plan, declared or not, and through the reduce's empty answer only
        /// seemed to. The two projections are merged, declaring none (<c>CosmosUncorrelatedProjectMergeRule</c>), and the
        /// statement plans as any other: declared, its <c>WHERE</c> keeps nothing and raises nowhere, and it sends nothing.
        /// </summary>
        [Fact]
        public void AScalarSubQueryNothingReadsPlans()
        {
            const string Gb4 = """SELECT y."id" FROM (SELECT x."id", x."DOC" FROM (SELECT c."id", c."DOC", (SELECT CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER) FROM (VALUES (1)) AS v(x0)) AS w FROM items AS c) AS x WHERE JSON_VALUE(x."DOC", '$.n') IN ('1', '2')) AS y WHERE JSON_VALUE(y."DOC", '$.kind') = 'C'""";

            var declared = Explain(Gb4, declared: true);
            declared.Should().Contain("CosmosToClrCursorConverter(sends=[nothing])", declared);

            var plain = Explain(Gb4, declared: false);
            plain.Should().Contain("CosmosToClrCursorConverter", plain).And.NotContain("sends=[nothing]", plain);
        }

        const string Sent = "sent";

        const string Nothing = "nothing";

        const string Empty = "empty";

        /// <summary>
        /// The shapes <see cref="CosmosDeclaredFactPlanningTests.EachShapeIsPlainPushdown"/> pins in the host Chapter 21
        /// describes, planned through this connection, declared and not, as plain pushdown: nothing the declaration says
        /// stands for an empty answer, and a statement sends nothing only where the <c>WHERE</c> it sends keeps no document
        /// the declaration admits and nothing in it can raise, here the run over a lateral whose filters on the left side
        /// Calcite merges and pushes. Calcite's own rules read most of the rest as false before anything is pushed, a
        /// condition such as <c>k2 IN ('A', 'B')</c> beside <c>k2 = 'C'</c> being contradictory whatever is declared, and
        /// plan an empty <c>Values</c> in its place, declared and not alike, a traversal's element filter among them; the
        /// others send their request, the cast Calcite's.
        /// </summary>
        [Theory]
        [InlineData("pc1", Sent, Sent)]
        [InlineData("pc2", Sent, Sent)]
        [InlineData("pc4", Sent, Sent)]
        [InlineData("pc5", Sent, Sent)]
        [InlineData("pr1", Empty, Empty)]
        [InlineData("pr2", Empty, Empty)]
        [InlineData("pr9", Empty, Empty)]
        [InlineData("dlat1", Empty, Empty)]
        [InlineData("gb5", Empty, Empty)]
        [InlineData("gb6", Empty, Empty)]
        [InlineData("gb7", Empty, Empty)]
        [InlineData("gc3", Empty, Empty)]
        [InlineData("gc4", Sent, Sent)]
        [InlineData("gc10", Empty, Empty)]
        [InlineData("gc11", Empty, Empty)]
        [InlineData("gc13", Empty, Empty)]
        [InlineData("gc15", Empty, Empty)]
        [InlineData("gc20", Empty, Empty)]
        [InlineData("gc21", Nothing, Sent)]
        [InlineData("lc1", Empty, Empty)]
        [InlineData("lc1b", Empty, Empty)]
        [InlineData("lc1w", Empty, Empty)]
        [InlineData("lc2w", Empty, Empty)]
        [InlineData("un9", Sent, Sent)]
        [InlineData("un9b", Sent, Sent)]
        [InlineData("sq9", Empty, Empty)]
        [InlineData("gk4", Empty, Empty)]
        [InlineData("gk9", Empty, Empty)]
        [InlineData("dk1", Empty, Empty)]
        [InlineData("dk4", Empty, Empty)]
        [InlineData("dk6", Empty, Empty)]
        [InlineData("sf3w", Empty, Empty)]
        public void EachShapeIsPlainPushdown(string name, string declared, string undeclared)
        {
            var empties = new System.Collections.Generic.List<int>();
            foreach (var (label, declares, expected) in new[] { ("declared", true, declared), ("undeclared", false, undeclared) })
            {
                var plan = Explain(CosmosDeclaredFactPlanningTests.Shape(name, "items"), declares, CosmosDeclaredFactPlanningTests.Tagged);
                empties.Add(System.Text.RegularExpressions.Regex.Matches(plan, System.Text.RegularExpressions.Regex.Escape("Values(tuples=[[]])")).Count);
                switch (expected)
                {
                    case Empty:
                        plan.Should().Contain("Values(tuples=[[]])", $"{name} {label}: Calcite's own rules read it as false: " + plan)
                            .And.NotContain("CosmosToClrCursorConverter", plan);
                        break;
                    case Nothing:
                        plan.Should().Contain("CosmosToClrCursorConverter(sends=[nothing])", $"{name} {label}: the WHERE sent keeps nothing and raises nowhere: " + plan);
                        break;
                    default:
                        plan.Should().Contain("CosmosToClrCursorConverter", $"{name} {label}: pushed as any statement is: " + plan)
                            .And.NotContain("sends=[nothing]", $"{name} {label}: the request is sent: " + plan);
                        break;
                }
            }

            empties[0].Should().Be(empties[1], $"{name}: the declaration makes no empty Values, Calcite's own rules alone do");
        }

        /// <summary>
        /// Three filters stacked, the cast of the text <c>body</c> to <c>INTEGER</c> in the middle one over a projection:
        /// the reduce's walk of the filters beneath kept the planner from finishing, declared and not. It plans, each, as
        /// plain pushdown; the cast is Calcite's, over what the request returns.
        /// </summary>
        [Fact]
        public void AStackOfThreeFiltersPlans()
        {
            const string Ff3mid = """SELECT y."id" FROM (SELECT x."id", x."DOC" FROM (SELECT c."id", c."DOC" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.n') = '1') AS x WHERE CAST(JSON_VALUE(x."DOC", '$.body') AS INTEGER) = 1) AS y WHERE JSON_VALUE(y."DOC", '$.kind') = 'C'""";

            foreach (var declared in new[] { true, false })
            {
                var plan = Explain(Ff3mid, declared);
                plan.Should().Contain("CosmosToClrCursorConverter", plan).And.Contain("CAST(", "the cast is Calcite's: " + plan);
            }
        }

    }

}
