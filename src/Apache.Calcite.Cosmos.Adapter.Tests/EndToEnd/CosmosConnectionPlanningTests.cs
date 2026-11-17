using System;
using System.Linq;
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

        /// <summary>
        /// An executor that answers every request with the same documents, whatever its <c>WHERE</c>, each as the row a
        /// statement reading the document and its id is returned: <c>{ "DOC": c, "id": c.id }</c>.
        /// </summary>
        sealed class DocumentsExecutor(string[] documents) : ICosmosQueryExecutor
        {

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default) =>
                new(Infrastructure.ListCursor.Documents(documents.Select(d => new JsonObject { ["DOC"] = JsonNode.Parse(d), ["id"] = JsonNode.Parse(d)!["id"]!.DeepClone() }.ToJsonString())));

        }

        static string Explain(string sql, bool declared, string schema = Schema, CosmosServiceTarget? target = null, Infrastructure.SplitBound? bound = null)
        {
            using var source = Open(declared ? schema : null, new NoExecutor(), target);
            using var hook = bound is null ? null : org.apache.calcite.runtime.Hook.PLANNER.addThread(bound);
            using var connection = source.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql.Replace(" items AS ", " \"items\" AS ");
            return ((string?)command.ExecuteScalar() ?? "").Trim().Replace("\r\n", "\n");
        }

        /// <summary>
        /// Runs a statement over a container declared nothing whose every request is answered with the same documents: the
        /// plan implemented, and its first column read.
        /// </summary>
        static System.Collections.Generic.List<string> Answer(string sql, string[] documents)
        {
            using var source = Open(null, new DocumentsExecutor(documents), null);
            using var connection = source.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql.Replace(" items AS ", " \"items\" AS ");
            using var reader = command.ExecuteReader();
            var rows = new System.Collections.Generic.List<string>();
            while (reader.Read())
                rows.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);

            return rows;
        }

        static System.Data.Common.DbDataSource Open(string? schema, ICosmosQueryExecutor executor, CosmosServiceTarget? target)
        {
            var metadata = TestReasoning.Bind(new CosmosContainerMetadata("items", ["/category"]));
            if (schema is not null)
                metadata = metadata.WithFacts(CosmosSchemaRecognition.ReadFrom(JsonNode.Parse(schema)!));
            if (target is { } t)
                metadata = metadata.WithServiceTarget(t);

            var table = new CosmosTable(metadata, executor);
            return new CalciteDataSourceBuilder(new CalciteConnectionStringBuilder
            {
                Model = "inline:{\"version\":\"1.0\",\"defaultSchema\":\"adhoc\",\"schemas\":[{\"name\":\"adhoc\"}]}",
                Schema = "adhoc",
                CaseSensitive = true,
            }.ConnectionString)
                .ConfigureRootSchema(root => root.add("items", table))
                .Build();
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

        /// <summary>
        /// A range over a path the schema lets hold a number or a string, a bound alone or nothing declared, is not ruled
        /// out, and through <c>CAST(… AS INTEGER)</c> plans and is sent. Through <c>CAST(… AS DOUBLE)</c>, which statements
        /// fail to plan with the simplifier's <c>ClassCastException</c> and which plan, as measured (<c>TODO.md</c>,
        /// <em>Two weakenings over one path still share one raw value</em>): with a bound alone, nothing declared, the types
        /// and the <c>enum</c>s listed, and with nothing declared <c>= 5.5</c> and <c>IN (5)</c> fail; <c>= 4.6</c>,
        /// <c>IN (5, 6)</c>, <c>BETWEEN 4 AND 6</c> and <c>NOT BETWEEN 4 AND 6</c> plan, the cast Calcite's.
        /// </summary>
        [Fact]
        public void ARangeOverAPathThatMayHoldANumberOrAStringIsSentThroughACastToInteger()
        {
            const string Bound = """{ "type": "object", "properties": { "n": { "maximum": 100 } } }""";
            const string Both = """{ "type": "object", "properties": { "n": { "type": ["string", "integer"], "maximum": 100 } } }""";
            const string String = """{ "type": "object", "properties": { "n": { "type": "string" } } }""";
            const string ThroughInteger = """SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER) > 500""";
            const string ThroughDouble = """SELECT c."id" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS DOUBLE) > 500""";

            foreach (var declared in new[] { true, false })
            {
                var plan = Explain(ThroughInteger, declared, Bound);
                plan.Should().Contain("CosmosToClrCursorConverter", plan).And.NotContain("sends=[nothing]", "a string there is not ruled out: " + plan);

                FluentActions.Invoking(() => Explain(ThroughDouble, declared, Bound)).Should().Throw<Exception>()
                    .Where(e => e.ToString().Contains("ReduceExpressionsRule"), "recorded: the simplifier meets the bound's DECIMAL with the DOUBLE");
            }

            FluentActions.Invoking(() => Explain(ThroughDouble, declared: true, Both)).Should().Throw<Exception>()
                .Where(e => e.ToString().Contains("ReduceExpressionsRule"), "a type naming a number and a string leaves the bound's weaker form beside the DOUBLE");

            static string Of(string n) => $$"""{ "type": "object", "properties": { "n": {{n}} } }""";

            foreach (var other in new[]
            {
                """{ "type": ["number", "boolean"] }""", """{ "type": ["integer", "object"] }""",
                """{ "anyOf": [{ "type": "string" }, { "type": "integer", "maximum": 100 }] }""", """{ "enum": [5, "5", "600"] }""",
            })
                FluentActions.Invoking(() => Explain(ThroughDouble, declared: true, Of(other))).Should().Throw<Exception>()
                    .Where(e => e.ToString().Contains("ReduceExpressionsRule"), $"{other} leaves the bound's weaker form beside the DOUBLE");

            foreach (var whole in new[] { """{ "enum": [5, "600"] }""", """{ "enum": [5, true] }""", """{ "enum": [1, 2, 3, "XL"] }""", """{ "type": ["number", "null"] }""" })
            {
                var plan = Explain(ThroughDouble, declared: true, Of(whole));
                plan.Should().Contain("CosmosToClrCursorConverter", plan).And.NotContain("sends=[nothing]", $"{whole}: " + plan);
                plan[..plan.IndexOf("CosmosToClrCursorConverter", StringComparison.Ordinal)].Should().NotContain("CAST(", $"over {whole} the comparison is sent whole: " + plan);
            }

            FluentActions.Invoking(() => Explain(ThroughDouble.Replace("> 500", "<> 600"), declared: true, Of("""{ "enum": [5, "600"] }"""))).Should().Throw<Exception>()
                .Where(e => e.ToString().Contains("ReduceExpressionsRule"), "over the same enum <> 600 is split, its weaker form holding 600");

            foreach (var failing in new[] { "= 5.5", "IN (5)" })
                FluentActions.Invoking(() => Explain(ThroughDouble.Replace("> 500", failing), declared: false)).Should().Throw<Exception>()
                    .Where(e => e.ToString().Contains("ReduceExpressionsRule"), $"with nothing declared {failing} fails to plan");

            foreach (var presence in new[] { "= 4.6", "IN (5, 6)", "BETWEEN 4 AND 6", "NOT BETWEEN 4 AND 6" })
            {
                var plan = Explain(ThroughDouble.Replace("> 500", presence), declared: false);
                plan.Should().Contain("CosmosToClrCursorConverter", plan).And.Contain("IS_DEFINED(", $"{presence} plans: " + plan);
                plan[..plan.IndexOf("CosmosToClrCursorConverter", StringComparison.Ordinal)].Should().Contain("CAST(", $"{presence}: the cast is Calcite's: " + plan);
            }

            var text = Explain(ThroughDouble, declared: true, String);
            text.Should().Contain("CosmosToClrCursorConverter", text).And.NotContain("sends=[nothing]", "a string the cast reads may keep a document: " + text);
            text[..text.IndexOf("CosmosToClrCursorConverter", StringComparison.Ordinal)].Should().Contain("CAST(", "the cast is Calcite's, over the path's presence: " + text);

            foreach (var type in new[] { "integer", "number" })
                Explain(ThroughDouble, declared: true, $$"""{ "type": "object", "properties": { "n": { "type": "{{type}}", "maximum": 100 } } }""")
                    .Should().Contain("CosmosToClrCursorConverter(sends=[nothing])", $"a {type} no greater than 100 keeps nothing");
        }

        /// <summary>
        /// <see cref="CosmosDeclaredFactPlanningTests.ASiblingSentLeavesNothingInCalcite"/> through this connection, whose
        /// simplifier reads the whole <c>WHERE</c> before anything is pushed: the cast of <c>body</c> a sibling makes go
        /// unread is in no node, declared or not, but in the <c>CASE</c> Calcite's simplifier keeps.
        /// </summary>
        [Theory]
        [InlineData("s1", false)]
        [InlineData("s2", false)]
        [InlineData("s3", false)]
        [InlineData("s4", false)]
        [InlineData("s5", false)]
        [InlineData("s6", true)]
        [InlineData("s7", false)]
        [InlineData("s8", false)]
        [InlineData("s9", false)]
        [InlineData("s10", false)]
        [InlineData("s11", false)]
        [InlineData("s12", false)]
        [InlineData("s13", false)]
        public void ASiblingSentLeavesNothingInCalcite(string name, bool caseKept)
        {
            foreach (var declared in new[] { true, false })
            {
                var plan = Explain(CosmosDeclaredFactPlanningTests.ImpliedBySibling(name, "items"), declared, CosmosDeclaredFactPlanningTests.Implied);
                plan.Should().Contain("CosmosToClrCursorConverter", plan);
                if (caseKept)
                    plan[..plan.IndexOf("CosmosToClrCursorConverter", StringComparison.Ordinal)].Should().Contain("CASE(", $"{name}: the CASE is Calcite's: " + plan);
                else
                    plan.Should().NotContain("'$.body'", $"{name}: " + plan);
            }
        }

        /// <summary>
        /// Through this connection the filter above the half a split sends is reduced by Calcite's own rules with what that
        /// half holds, read as a predicate pulled up from it, which Calcite in process, over no such half, never reads: beside
        /// a boolean sent, a part kept that tests the boolean's nulls is read as decided. So x3 is an empty <c>Values</c>,
        /// no rows and no request, and x4 keeps nothing of <c>body</c>, declared or not, where Calcite in process, through
        /// this connection too, fails on a <c>body</c> that is no number. Recorded, older than "fail like Calcite" and the
        /// same on <c>main</c> (<c>TODO.md</c>, <em>Through a connection, a filter above the half sent is reduced with what
        /// that half holds</em>); in the host Chapter 21 describes the cast is Calcite's
        /// (<see cref="CosmosDeclaredFactPlanningTests.APartCalciteEvaluatesBesideThePartSentIsEvaluated"/>).
        /// </summary>
        [Fact]
        public void AFilterAboveTheHalfSentIsReducedWithWhatItHolds()
        {
            foreach (var declared in new[] { true, false })
            {
                var x3 = Explain(CosmosDeclaredFactPlanningTests.EvaluatedBesideASent("x3", "items"), declared, CosmosDeclaredFactPlanningTests.Lowered);
                x3.Should().Contain("Values(tuples=[[]])", $"declared {declared}: " + x3).And.NotContain("CosmosToClrCursorConverter", x3);

                var x4 = Explain(CosmosDeclaredFactPlanningTests.EvaluatedBesideASent("x4", "items"), declared, CosmosDeclaredFactPlanningTests.Lowered);
                x4.Should().Contain("CosmosToClrCursorConverter", x4).And.NotContain("'$.body'", $"declared {declared}: " + x4);
            }
        }

        /// <summary>
        /// <c>code</c> and <c>lst</c> hold the digits a cast to <c>INTEGER</c> reads and <c>kind</c> is <c>A</c> or <c>B</c>, so
        /// nothing in the statements below raises.
        /// </summary>
        static readonly string[] Codes =
        [
            """{"id":"1","category":"a","kind":"A","body":"9","code":"5","lst":"6"}""",
            """{"id":"2","category":"a","kind":"B","body":"1","code":"7","lst":"7"}""",
            """{"id":"3","category":"b","kind":"A","body":"7","code":"5","lst":"6"}""",
        ];

        /// <summary>
        /// A text equality beside a cast of the same path to <c>INTEGER</c>, nothing declared: what Calcite evaluates holds no
        /// Cosmos type test, which it has no body for. This connection's filter merge carried a split's weaker forms up
        /// beside the conjuncts they weaken, a split of that left them above, and the statement failed with "cannot
        /// translate call IS_NUMBER" where Calcite answers. Each runs over documents on which nothing raises.
        /// </summary>
        [Theory]
        [InlineData("g1", """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.code') = '5' AND (CAST(JSON_VALUE(c."DOC", '$.code') AS INTEGER) = 5 OR JSON_VALUE(c."DOC", '$.kind') = 'A')""", "1,3")]
        [InlineData("g4", """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.code') = '5' AND NOT (JSON_VALUE(c."DOC", '$.kind') = 'B' AND CAST(JSON_VALUE(c."DOC", '$.code') AS INTEGER) = 7)""", "1,3")]
        [InlineData("g9", """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.lst') = '6' AND (CAST(JSON_VALUE(c."DOC", '$.lst') AS INTEGER) = 6 OR JSON_VALUE(c."DOC", '$.kind') = 'B')""", "1,3")]
        [InlineData("g10", """SELECT COUNT(*) AS q FROM items AS c WHERE JSON_VALUE(c."DOC", '$.code') = '5' AND (CAST(JSON_VALUE(c."DOC", '$.code') AS INTEGER) = 5 OR JSON_VALUE(c."DOC", '$.kind') = 'A')""", "2")]
        public void NoWeakerFormIsCalcites(string name, string sql, string rows)
        {
            var plan = Explain(sql, declared: false);
            plan.Should().Contain("CosmosToClrCursorConverter", plan);
            foreach (var line in plan.Split('\n').Where(l => l.TrimStart().StartsWith("ClrCursor")))
                line.Should().NotContain("IS_NUMBER(", $"{name}: " + plan).And.NotContain("IS_DEFINED(", $"{name}: " + plan);

            Answer(sql, Codes).Should().BeEquivalentTo(rows.Split(','), $"{name}: the plan implements, and Calcite evaluates the condition as written over every document answered: " + plan);
        }

        /// <summary>
        /// Declared, with the functions a Cosmos schema names: <c>IS_STRING</c> of the promoted <c>c."$.category"</c> beside
        /// a <c>kind</c> the schema rules out sends nothing, and the statement fails as it is implemented, no request made.
        /// </summary>
        [Fact]
        public void APartKeptThatFailsBeforeItReadsARowFailsBesideAPartSentThatKeepsNothing()
        {
            var metadata = TestReasoning.Bind(new CosmosContainerMetadata("items", ["/category"])).WithFacts(CosmosSchemaRecognition.ReadFrom(JsonNode.Parse(Schema)!));
            var table = new CosmosTable(metadata, new NoExecutor());
            using var source = new CalciteDataSourceBuilder(new CalciteConnectionStringBuilder
            {
                Model = "inline:{\"version\":\"1.0\",\"defaultSchema\":\"adhoc\",\"schemas\":[{\"name\":\"adhoc\"}]}",
                Schema = "adhoc",
                CaseSensitive = true,
            }.ConnectionString)
                .ConfigureRootSchema(root =>
                {
                    root.add("items", table);
                    for (var entries = Adapter.Sql.CosmosSchemaFunctions.Instance.entries().iterator(); entries.hasNext();)
                    {
                        var entry = (java.util.Map.Entry)entries.next();
                        root.add((string)entry.getKey(), (org.apache.calcite.schema.Function)entry.getValue());
                    }
                })
                .Build();

            const string TypeTest = """SELECT c."id" FROM "items" AS c WHERE JSON_VALUE(c."DOC", '$.kind') = 'C' AND IS_STRING(c."$.category")""";
            using var connection = source.OpenConnection();
            using var explain = connection.CreateCommand();
            explain.CommandText = "EXPLAIN PLAN FOR " + TypeTest;
            var plan = ((string?)explain.ExecuteScalar() ?? "").Replace("\r\n", "\n");
            plan.Should().Contain("CosmosToClrCursorConverter(sends=[nothing])", plan);

            using var command = connection.CreateCommand();
            command.CommandText = TypeTest;
            var run = () =>
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    continue;
            };

            run.Should().Throw<Exception>().Which.ToString().Should().Contain("Unable to implement", plan).And.Contain("IS_STRING(", plan).And.NotContain("Nothing here is run", plan);
        }

        /// <summary>
        /// <see cref="CosmosDeclaredFactPlanningTests.APartLeftAboveACorrelateIsReadBesideWhatItMoves"/> through this
        /// connection: u18a, u18d, u18f, l18a and their <c>COUNT</c>s plan with no cast, declared and not.
        /// </summary>
        [Theory]
        [InlineData("u18a")]
        [InlineData("u18ac")]
        [InlineData("u18d")]
        [InlineData("u18f")]
        [InlineData("l18a")]
        [InlineData("l18ac")]
        public void APartLeftAboveACorrelateIsReadBesideWhatItMoves(string name)
        {
            foreach (var declared in new[] { true, false })
            {
                var plan = Explain(CosmosDeclaredFactPlanningTests.BesideACorrelate(name, "items"), declared, CosmosDeclaredFactPlanningTests.Traversed);
                plan.Should().Contain("CosmosToClrCursorConverter", plan).And.NotContain("CAST(", $"{name} declared {declared}: " + plan);
            }
        }

        /// <summary>
        /// <see cref="CosmosDeclaredFactPlanningTests.AConditionKeptAboveTheHalfSentIsNotSplitAgain"/> through this
        /// connection: each plans within the same bound, declared and not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("q121")]
        [InlineData("t3")]
        [InlineData("q170")]
        [InlineData("q32")]
        [InlineData("q119")]
        public void AConditionKeptAboveTheHalfSentIsNotSplitAgain(string name)
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var declared in new[] { true, false })
                {
                    var plan = Explain(CosmosDeclaredFactPlanningTests.SplitAgain(name, "items"), declared, CosmosDeclaredFactPlanningTests.Drawn, target, new Infrastructure.SplitBound(CosmosDeclaredFactPlanningTests.SplitsBound));
                    plan.Should().NotBeEmpty($"{name} {target} declared {declared}");
                }
            }
        }

        /// <summary>
        /// <see cref="CosmosDeclaredFactPlanningTests.AConjunctAdmittedBesideASiblingStaysInCalciteBesideAKindRuledOut"/>
        /// through this connection: <c>UPPER(body) = 'ABC'</c>, sent only beside <c>body = 'abc'</c>, stays in Calcite over a
        /// statement that sends nothing where the declaration rules out its <c>kind</c>, and is sent whole beside a
        /// <c>kind</c> it admits and with nothing declared.
        /// </summary>
        [Fact]
        public void AConjunctAdmittedBesideASiblingStaysInCalciteBesideAKindRuledOut()
        {
            const string Sql = """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.kind') = '{0}' AND UPPER(JSON_VALUE(c."DOC", '$.body')) = 'ABC' AND JSON_VALUE(c."DOC", '$.body') = 'abc'""";
            const string Converter = "CosmosToClrCursorConverter";

            var ruledOut = Explain(string.Format(Sql, "C"), declared: true, CosmosDeclaredFactPlanningTests.KindOnly);
            ruledOut.Should().Contain(Converter + "(sends=[nothing])", ruledOut);
            ruledOut[..ruledOut.IndexOf(Converter, StringComparison.Ordinal)].Should().Contain("UPPER(", "the UPPER is Calcite's: " + ruledOut);
            ruledOut[ruledOut.IndexOf(Converter, StringComparison.Ordinal)..].Should().NotContain("UPPER(", ruledOut);

            foreach (var (label, kind, declared) in new[] { ("undeclared", "C", false), ("admitted", "A", true), ("admitted, undeclared", "A", false) })
            {
                var plan = Explain(string.Format(Sql, kind), declared, CosmosDeclaredFactPlanningTests.KindOnly);
                plan.Should().StartWith(Converter, $"{label}: pushed whole: " + plan).And.NotContain("sends=[nothing]", $"{label}: " + plan);
                plan.Should().Contain("UPPER(", $"{label}: " + plan);
            }
        }

    }

}
