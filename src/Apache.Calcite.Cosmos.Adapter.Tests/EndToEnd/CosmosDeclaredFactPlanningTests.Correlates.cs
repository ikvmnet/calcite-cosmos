using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Extensions.Adapter.Cursor;
using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using org.apache.calcite;
using org.apache.calcite.adapter.java;
using org.apache.calcite.jdbc;
using org.apache.calcite.schema;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    public partial class CosmosDeclaredFactPlanningTests
    {

        /// <summary>
        /// <c>kind</c> is <c>A</c> or <c>B</c>, <c>m</c> <c>"1"</c> or <c>"2"</c>, <c>body</c> a string and <c>tags</c> an
        /// array of strings, all required.
        /// </summary>
        internal const string Traversed = """{ "type": "object", "required": ["kind", "m", "body", "tags"], "properties": { "kind": { "enum": ["A", "B"] }, "m": { "enum": ["1", "2"] }, "body": { "type": "string" }, "tags": { "type": "array", "items": { "type": "string" } } } }""";

        /// <summary>What <see cref="Traversed"/> admits: an element <c>"x"</c> and a <c>body</c> of <c>"abc"</c>, which no cast to <c>INTEGER</c> reads.</summary>
        internal static readonly string[] TraversedDocuments =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","body":"abc","tags":["x","1"]}""",
            """{"id":"2","category":"a","kind":"B","m":"2","body":"7","tags":["2"]}""",
        ];

        /// <summary>
        /// A <c>WHERE</c> over a correlate, an <c>UNNEST</c> of <c>tags</c> or a lateral of <c>body</c>, one of whose conjuncts
        /// reads the document alone and another the correlate's right side beside it.
        /// </summary>
        internal static string BesideACorrelate(string name, string t)
        {
            var m = Json("c", "m");
            var unnest = $"""{t} AS c, UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t)""";
            var lateral = $"""{t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "body")} AS b2 FROM (VALUES (1)) AS v(x0)) AS l""";
            return name switch
            {
                "u18a" => $"""SELECT c."id", u.t FROM {unnest} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(u.t AS INTEGER) = 1)""",
                "u18ac" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(u.t AS INTEGER) = 1)""",
                "u18d" => $"""SELECT c."id", u.t FROM {unnest} WHERE {KindOf("c")} IS NOT NULL AND ({KindOf("c")} IS NOT NULL OR CAST(u.t AS INTEGER) = 1)""",
                "u18f" => $"""SELECT c."id", u.t FROM {unnest} WHERE {m} IS NOT NULL AND NOT ({m} IS NULL AND CAST(u.t AS INTEGER) = 1)""",
                "u18b" => $"""SELECT c."id", u.t FROM {unnest} WHERE {KindOf("c")} IN ('A', 'B') AND ({KindOf("c")} IN ('A', 'B') OR CAST(u.t AS INTEGER) = 1)""",
                "l18a" => $"""SELECT c."id" FROM {lateral} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(l.b2 AS INTEGER) = 1)""",
                "l18ac" => $"""SELECT COUNT(*) AS q FROM {lateral} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(l.b2 AS INTEGER) = 1)""",
                "w18f" => $"""SELECT c."id", u.t FROM {unnest} WHERE {m} IS NULL AND NOT ({m} IS NULL OR CAST(u.t AS INTEGER) = 1)""",
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such shape."),
            };
        }

        /// <summary>
        /// Above a correlate the part of a <c>WHERE</c> its rule leaves is what Calcite's simplification of the whole condition
        /// evaluates beside the conjuncts moved into a side, so u18a, u18b, u18d, u18f, l18a and their <c>COUNT</c>s cast no
        /// element and no <c>body</c>, and answer the rows the condition keeps, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("u18a", "1,x;1,1;2,2")]
        [InlineData("u18ac", "3")]
        [InlineData("u18b", "1,x;1,1;2,2")]
        [InlineData("u18d", "1,x;1,1;2,2")]
        [InlineData("u18f", "1,x;1,1;2,2")]
        [InlineData("l18a", "1;2")]
        [InlineData("l18ac", "2")]
        public async Task APartLeftAboveACorrelateIsReadBesideWhatItMoves(string name, string rows)
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var (declared, container) in new[] { ("declared", Declaring(Traversed)), ("undeclared", Declaring("""{ "type": "object" }""")) })
                {
                    var (answer, raised, plan) = await Answer(BesideACorrelate(name, "items"), container.WithServiceTarget(target), TraversedDocuments);
                    raised.Should().BeNull($"{name} {target} {declared}: " + plan);
                    answer.Should().BeEquivalentTo(rows.Split(';'), $"{name} {target} {declared}: " + plan);
                }
            }
        }

        /// <summary>
        /// Where Calcite's simplification of the whole condition reads it as false, the condition is kept as it stands above
        /// the conjuncts moved, which are not moved again: w18f plans within the bound, declared or not, under both targets,
        /// its cast in a node Calcite evaluates and no node holding a <c>FALSE</c>.
        /// </summary>
        [Fact]
        public void AConditionReadAsFalseIsKeptWholeAboveACorrelate()
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var (declared, container) in new[] { ("declared", Declaring(Traversed)), ("undeclared", Declaring("""{ "type": "object" }""")) })
                {
                    var text = PlanText(PlanToCosmos(BesideACorrelate("w18f", "items"), container.WithServiceTarget(target), out _, listener: new Infrastructure.SplitBound(SplitsBound)));
                    text.Split('\n').Where(l => l.TrimStart().StartsWith("ClrCursor")).Should().Contain(l => l.Contains(":INTEGER"), $"{target} {declared}: " + text);
                    text.Should().NotContain("condition=[false]", $"{target} {declared}: " + text);
                }
            }
        }

        /// <summary>
        /// Plans a statement in the host Chapter 21 describes, its calc pass after, and runs it over a container whose every
        /// request is answered with the same documents, each as the row a statement reading the document and its id is
        /// returned: the rows, each as its columns' text joined by commas, or what the run raised.
        /// </summary>
        static async Task<(List<string>? Rows, Exception? Raised, string Plan)> Answer(string sql, CosmosContainerMetadata container, string[] documents)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(container, new DocumentsExecutor(documents));
            var plan = "";
            try
            {
                var best = PlanOver(sql, table, typeFactory, out var rootSchema, windows: true, subqueries: true);

                var program = new org.apache.calcite.plan.hep.HepProgramBuilder();
                foreach (var rule in ClrCursorRules.CalcRules())
                    program.addRuleInstance(rule);

                var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
                hep.setRoot(best);
                best = hep.findBestExp();
                plan = PlanText(best);

                var factory = new ClrCursorRelImplementor(best.getCluster().getRexBuilder(), new java.util.HashMap()).ImplementRoot((ClrCursorRel)best, ClrCursorPrefer.Array);
                var rows = new List<string>();
                await using (var cursor = await factory.OpenAsync(new DocumentsContext(rootSchema.plus(), typeFactory), TestContext.Current.CancellationToken))
                    while (await cursor.ReadAsync(TestContext.Current.CancellationToken))
                        rows.Add(cursor.Current is object?[] columns ? string.Join(",", columns.Select(Text)) : Text(cursor.Current));

                return (rows, null, plan);
            }
            catch (Exception e)
            {
                return (null, e, plan);
            }

            static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }

        /// <summary>
        /// An executor that answers every request reading the document with the same documents, whatever its <c>WHERE</c>,
        /// each as <c>{ "DOC": c, "id": c.id }</c>, and refuses any other.
        /// </summary>
        sealed class DocumentsExecutor(string[] documents) : ICosmosQueryExecutor
        {

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default) =>
                query.Sql.StartsWith("SELECT VALUE { \"DOC\": c,", StringComparison.Ordinal) && query.Sql.Contains(" JOIN ", StringComparison.Ordinal) == false
                    ? new(Infrastructure.ListCursor.Documents(documents.Select(d => new JsonObject { ["DOC"] = JsonNode.Parse(d), ["id"] = JsonNode.Parse(d)!["id"]!.DeepClone() }.ToJsonString())))
                    : throw new InvalidOperationException("Not answered here: " + query.Sql);

        }

        /// <summary>
        /// The minimum a compiled plan needs to find its way back to the table it reads.
        /// </summary>
        sealed class DocumentsContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory) : DataContext
        {

            public SchemaPlus getRootSchema() => rootSchema;

            public JavaTypeFactory getTypeFactory() => typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => null!;

        }

    }

}
