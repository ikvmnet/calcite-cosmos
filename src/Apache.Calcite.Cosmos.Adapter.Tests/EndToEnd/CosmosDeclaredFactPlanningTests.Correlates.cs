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
                _ => BesideACorrelateMore(name, t),
            };
        }

        /// <summary>
        /// More of <see cref="BesideACorrelate"/>: a conjunct of both sides the whole condition reads down to one side, beside
        /// a conjunct moved, and a whole read as false.
        /// </summary>
        static string BesideACorrelateMore(string name, string t)
        {
            var k = KindOf("c");
            var m = Json("c", "m");
            var cb = $"CAST({Json("c", "body")} AS INTEGER)";
            var flag = """JSON_VALUE(c."DOC", '$.flag' RETURNING BOOLEAN)""";
            var unnest = $"""{t} AS c, UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t)""";
            var lateral = $"""{t} AS c CROSS JOIN LATERAL (SELECT {Json("c", "body")} AS b2 FROM (VALUES (1)) AS v(x0)) AS l""";
            var some = $"""{t} AS c CROSS JOIN LATERAL (SELECT {k} AS k2 FROM (VALUES (1)) AS v(x0) WHERE {m} = '1') AS l""";
            var x3 = $"(({k} = 'B' AND u.t = 'x') OR {cb} = 7)";
            var z189 = $"((u.t = 'x' AND {cb} = 7) OR CAST(u.t AS INTEGER) = 1) AND u.t = '1' AND CASE WHEN {m} = '1' THEN u.t <> '1' ELSE NOT {flag} END";
            return name switch
            {
                "x3" => $"""SELECT c."id", u.t FROM {unnest} WHERE {k} = 'A' AND {x3}""",
                "x9" => $"""SELECT c."id", u.t FROM {unnest} WHERE u.t = '1' AND ((u.t = '2' AND {k} = 'B') OR {cb} = 7)""",
                "x9b" => $"""SELECT c."id", u.t FROM {unnest} WHERE u.t = '1' AND ((u.t = '2' AND {k} = 'B') OR {cb} > 0)""",
                "x8" => $"""SELECT c."id" FROM {some} WHERE {k} = 'A' AND (({k} = 'B' AND l.k2 = 'x') OR {cb} = 7)""",
                "x8c" => $"""SELECT COUNT(*) AS q FROM {some} WHERE {k} = 'A' AND (({k} = 'B' AND l.k2 = 'x') OR {cb} = 7)""",
                "mx1" => $"""SELECT c."id", u.t FROM {unnest} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(u.t AS INTEGER) = 1) AND {k} = 'A' AND {x3}""",
                "mx1c" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {m} IS NOT NULL AND ({m} IS NOT NULL OR CAST(u.t AS INTEGER) = 1) AND {k} = 'A' AND {x3}""",
                "z189u" => $"""SELECT c."id", u.t FROM {unnest} WHERE {z189}""",
                "z189uc" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {z189}""",
                "w1" => $"""SELECT c."id", u.t FROM {unnest} WHERE CAST(u.t AS INTEGER) > 0 AND u.t = '1' AND ((u.t = '2' AND {k} = 'B') OR {cb} = 7)""",
                "w1c" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE CAST(u.t AS INTEGER) > 0 AND u.t = '1' AND ((u.t = '2' AND {k} = 'B') OR {cb} = 7)""",
                "v0" => $"""SELECT c."id", u.t FROM {unnest} WHERE {cb} = 7 AND NOT ({cb} = 7 OR CAST(u.t AS INTEGER) = 1)""",
                "v0c" => $"""SELECT COUNT(*) AS q FROM {unnest} WHERE {cb} = 7 AND NOT ({cb} = 7 OR CAST(u.t AS INTEGER) = 1)""",
                "v2" => $"""SELECT c."id" FROM {lateral} WHERE {cb} = 7 AND NOT ({cb} = 7 OR CAST(l.b2 AS INTEGER) = 1)""",
                "v3" => $"""SELECT c."id", u.t FROM {t} AS c LEFT JOIN UNNEST(JSON_QUERY(c."DOC", '$.tags' RETURNING VARCHAR ARRAY)) AS u(t) ON TRUE WHERE {cb} = 7 AND NOT ({cb} = 7 OR CAST(u.t AS INTEGER) = 1)""",
                "u6" => $"""SELECT c."id", u.t FROM {unnest} WHERE {k} = 'A' AND NOT ({k} = 'A' OR CAST(u.t AS INTEGER) = 1)""",
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such shape."),
            };
        }

        /// <summary>
        /// What <see cref="Traversed"/> admits: a document with no element and a <c>body</c> of <c>"q"</c>, which no cast to
        /// <c>INTEGER</c> reads, beside one with an element <c>"x"</c>, and a lateral that returns no row for the first.
        /// </summary>
        internal static readonly string[] BesideAnEmptySide =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","flag":true,"body":"7","tags":["1","x"]}""",
            """{"id":"2","category":"a","kind":"A","m":"2","flag":true,"body":"q","tags":[]}""",
            """{"id":"3","category":"b","kind":"A","m":"1","flag":false,"body":"3","tags":["2"]}""",
        ];

        /// <summary><see cref="Traversed"/> with <c>body</c> one of <c>"5"</c> and <c>"6"</c>.</summary>
        internal const string TraversedFixed = """{ "type": "object", "required": ["kind", "m", "body", "tags"], "properties": { "kind": { "enum": ["A", "B"] }, "m": { "enum": ["1", "2"] }, "body": { "enum": ["5", "6"] }, "tags": { "type": "array", "items": { "type": "string" } } } }""";

        /// <summary>What <see cref="TraversedFixed"/> admits: an element <c>"x"</c>, and a document with no element.</summary>
        internal static readonly string[] FixedBesideAnEmptySide =
        [
            """{"id":"1","category":"a","kind":"A","m":"1","body":"5","tags":["1","x"]}""",
            """{"id":"2","category":"a","kind":"B","m":"2","body":"6","tags":["1"]}""",
            """{"id":"3","category":"b","kind":"A","m":"1","body":"5","tags":[]}""",
        ];

        /// <summary>
        /// Above a correlate a conjunct of both sides that the whole condition reads down to one side is left as written, where
        /// a later firing of the rule would move the term into that side and evaluate it on a document the correlate returns
        /// no row for: x3, x9, x9b and the lateral x8 cast no <c>body</c> of a document with no element or no lateral row, and
        /// answer the rows the condition keeps, as does mx1, which beside such a conjunct holds one the whole reads away, and
        /// z189u and w1 fail on the element <c>"x"</c> as Calcite does, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("x3", "1,1;1,x")]
        [InlineData("x9", "1,1")]
        [InlineData("x9b", "1,1")]
        [InlineData("x8", "1")]
        [InlineData("x8c", "1")]
        [InlineData("mx1", "1,1;1,x")]
        [InlineData("mx1c", "2")]
        [InlineData("z189u", "raises x")]
        [InlineData("z189uc", "raises x")]
        [InlineData("w1", "raises x")]
        [InlineData("w1c", "raises x")]
        public async Task ATermTheWholeReadsIntoOneSideStaysAboveACorrelate(string name, string rows)
        {
            var (schema, documents) = name.StartsWith('w') ? (TraversedFixed, FixedBesideAnEmptySide) : (Traversed, BesideAnEmptySide);
            await Answers(name, rows, schema, documents);
        }

        /// <summary>
        /// Where Calcite's simplification of the whole condition above a correlate reads it as false, nothing is moved into a
        /// side, where a conjunct that can fail would be evaluated alone: v0, its <c>COUNT</c>, the lateral v2 and the left
        /// traversal v3 answer no rows over a <c>body</c> of <c>"abc"</c>, declared or not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("v0", "")]
        [InlineData("v0c", "0")]
        [InlineData("v2", "")]
        [InlineData("v3", "")]
        [InlineData("u6", "")]
        public async Task AConditionReadAsFalseMovesNothingIntoACorrelatesSide(string name, string rows)
        {
            await Answers(name, rows, Traversed, TraversedDocuments);
        }

        /// <summary>
        /// Plans and runs a shape of <see cref="BesideACorrelate"/> over documents, declared and not, under both targets: the rows
        /// given, separated by <c>;</c>, or, written <c>raises x</c>, a failure on the text <c>"x"</c>.
        /// </summary>
        static async Task Answers(string name, string rows, string schema, string[] documents)
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var (declared, container) in new[] { ("declared", Declaring(schema)), ("undeclared", Declaring("""{ "type": "object" }""")) })
                {
                    var (answer, raised, plan) = await Answer(BesideACorrelate(name, "items"), container.WithServiceTarget(target), documents);
                    if (rows.StartsWith("raises ", StringComparison.Ordinal))
                    {
                        var inner = raised;
                        while (inner?.InnerException is not null)
                            inner = inner.InnerException;

                        inner.Should().NotBeNull($"{name} {target} {declared}: " + plan);
                        inner!.Message.Should().Contain($"\"{rows["raises ".Length..]}\"", $"{name} {target} {declared}: " + plan);
                    }
                    else
                    {
                        raised.Should().BeNull($"{name} {target} {declared}: " + plan);
                        answer.Should().BeEquivalentTo(rows.Length == 0 ? [] : rows.Split(';'), $"{name} {target} {declared}: " + plan);
                    }
                }
            }
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
        /// Where Calcite's simplification of the whole condition above a correlate reads it as false, nothing moves into a
        /// side: u6 plans within the bound, declared or not, under both targets, its condition, cast and all, in the node above
        /// the correlate, and no node beneath the correlate holds a condition.
        /// </summary>
        [Fact]
        public void AConditionReadAsFalseIsKeptWholeAboveACorrelate()
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var (declared, container) in new[] { ("declared", Declaring(Traversed)), ("undeclared", Declaring("""{ "type": "object" }""")) })
                {
                    var text = PlanText(PlanToCosmos(BesideACorrelate("u6", "items"), container.WithServiceTarget(target), out _, listener: new Infrastructure.SplitBound(SplitsBound)));
                    var lines = text.Split('\n');
                    var correlate = System.Array.FindIndex(lines, l => l.Contains("Correlate(", StringComparison.Ordinal));
                    correlate.Should().BePositive($"{target} {declared}: " + text);
                    lines[..correlate].Should().Contain(l => l.Contains(":INTEGER", StringComparison.Ordinal) && l.Contains("'$.kind'", StringComparison.Ordinal), $"{target} {declared}: " + text);
                    lines[correlate..].Should().NotContain(l => l.Contains("condition", StringComparison.OrdinalIgnoreCase), $"{target} {declared}: " + text);
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
