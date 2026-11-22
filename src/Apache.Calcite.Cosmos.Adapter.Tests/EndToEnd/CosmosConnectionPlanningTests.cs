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
        /// statement reading the document and its id is returned: <c>{ "DOC": c, "id": c.id }</c>; and keeps each request
        /// it is sent in <paramref name="sent"/>, where one is given.
        /// </summary>
        sealed class DocumentsExecutor(string[] documents, System.Collections.Generic.List<CosmosQuery>? sent = null) : ICosmosQueryExecutor
        {

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                if (sent is not null)
                    lock (sent)
                        sent.Add(query);

                return new(Infrastructure.ListCursor.Documents(documents.Select(d => new JsonObject { ["DOC"] = JsonNode.Parse(d), ["id"] = JsonNode.Parse(d)!["id"]!.DeepClone() }.ToJsonString())));
            }

        }

        /// <summary>An executor that answers every request with no document, and keeps each request's text and parameters.</summary>
        sealed class RecordingExecutor(System.Collections.Generic.List<string> sent) : ICosmosQueryExecutor
        {

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                lock (sent)
                    sent.Add(query.Sql + string.Concat(query.Parameters.Select((p, i) => (i == 0 ? " | " : ", ") + p.Name + "=" + Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture))));

                return new(Infrastructure.ListCursor.Documents([]));
            }

        }

        /// <summary>
        /// Runs a statement over a container that holds no document, with each planner the host makes given to <paramref
        /// name="bound"/>: each request sent, with its parameters, or <c>nothing</c>.
        /// </summary>
        static string Sends(string sql, bool declared, string schema, CosmosServiceTarget? target, java.util.function.Consumer? bound)
        {
            var sent = new System.Collections.Generic.List<string>();
            using var source = Open(declared ? schema : null, new RecordingExecutor(sent), target);
            using var hook = bound is null ? null : org.apache.calcite.runtime.Hook.PLANNER.addThread(bound);
            using var connection = source.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql.Replace(" items AS ", " \"items\" AS ");
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    continue;

            return sent.Count == 0 ? "nothing" : string.Join("; ", sent.Distinct());
        }

        static string Explain(string sql, bool declared, string schema = Schema, CosmosServiceTarget? target = null, java.util.function.Consumer? bound = null)
        {
            using var source = Open(declared ? schema : null, new NoExecutor(), target);
            using var hook = bound is null ? null : org.apache.calcite.runtime.Hook.PLANNER.addThread(bound);
            using var connection = source.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql.Replace(" items AS ", " \"items\" AS ");
            return ((string?)command.ExecuteScalar() ?? "").Trim().Replace("\r\n", "\n");
        }

        /// <summary>
        /// Runs a statement over a container declared nothing, or declared <paramref name="schema"/>, whose every request
        /// is answered with the same documents: the plan implemented, each <c>?</c> bound in order to a value of
        /// <paramref name="parameters"/>, and its first column read; each request sent is kept in
        /// <paramref name="sent"/>, where one is given.
        /// </summary>
        static System.Collections.Generic.List<string> Answer(string sql, string[] documents, object?[]? parameters = null, System.Collections.Generic.List<CosmosQuery>? sent = null, string? schema = null)
        {
            using var source = Open(schema, new DocumentsExecutor(documents, sent), null);
            using var connection = source.OpenConnection();
            return Rows(connection, sql.Replace(" items AS ", " \"items\" AS "), parameters ?? []);
        }

        /// <summary>A statement's first column, each <c>?</c> bound in order, <c>null</c> as SQL null.</summary>
        static System.Collections.Generic.List<string> Rows(System.Data.Common.DbConnection connection, string sql, object?[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var value in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            using var reader = command.ExecuteReader();
            var rows = new System.Collections.Generic.List<string>();
            while (reader.Read())
                rows.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);

            return rows;
        }

        /// <summary>
        /// An executor that answers every request with the documents in the order given, which is the order the service
        /// returns them in for a request sorted on the stored text where a test lists them so, each projected as the
        /// request's <c>SELECT VALUE { … }</c> names it: the document, a path, or a path guarded to its scalars. A request
        /// with a <c>WHERE</c>, or projecting anything else, is refused.
        /// </summary>
        sealed class StoredOrderExecutor(string[] documents) : ICosmosQueryExecutor
        {

            static readonly System.Text.RegularExpressions.Regex Shape = new(@"^SELECT VALUE \{ (?<items>.*) \} FROM items c( ORDER BY [^@]*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            static readonly System.Text.RegularExpressions.Regex Item = new(@"""(?<name>[^""]+)"": (?:\(IS_PRIMITIVE\((?<guarded>c(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\) \? \k<guarded> : null\)|(?<path>c(?:\.[A-Za-z_][A-Za-z0-9_]*)*))(, |$)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            public ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                var shape = Shape.Match(query.Sql);
                if (shape.Success == false)
                    throw new InvalidOperationException("Not a request this executor answers: " + query.Sql);

                var items = Item.Matches(shape.Groups["items"].Value);
                if (string.Concat(items.Select(m => m.Value)) != shape.Groups["items"].Value)
                    throw new InvalidOperationException("Not a projection this executor answers: " + query.Sql);

                var rows = documents.Select(d =>
                {
                    var document = JsonNode.Parse(d)!;
                    var row = new JsonObject();
                    foreach (var item in items.Cast<System.Text.RegularExpressions.Match>())
                    {
                        var guarded = item.Groups["guarded"].Success;
                        var value = At(document, guarded ? item.Groups["guarded"].Value : item.Groups["path"].Value);
                        if (value.Found)
                            row[item.Groups["name"].Value] = guarded && value.Node is JsonObject or JsonArray ? null : value.Node?.DeepClone();
                    }

                    return row.ToJsonString();
                });

                return new(Infrastructure.ListCursor.Documents(rows.ToList()));
            }

            static (bool Found, JsonNode? Node) At(JsonNode document, string path)
            {
                JsonNode? at = document;
                foreach (var step in path.Split('.').Skip(1))
                {
                    if (at is not JsonObject o || o.TryGetPropertyValue(step, out at) == false)
                        return (false, null);
                }

                return (true, at);
            }

        }

        /// <summary>
        /// Runs a statement over a container whose every request is answered with the same documents in the order given,
        /// declared <paramref name="schema"/> or nothing, on a target: the plan implemented, and its first column read.
        /// </summary>
        static System.Collections.Generic.List<string> AnswerInStoredOrder(string sql, string? schema, CosmosServiceTarget target, string[] documents)
        {
            using var source = Open(schema, new StoredOrderExecutor(documents), target);
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
        /// A constant Calcite's planning folds to a NaN or an infinity, compared or added into a projected column, is
        /// computed in process, as Calcite answers it, and nothing sent holds it: no JSON number holds one, and the SDK
        /// writes it as a JSON string, which the service reads as a string.
        /// </summary>
        /// <remarks>
        /// Bound as a literal, <c>&lt;&gt; NaN</c> was pushed whole as <c>c.n != @p0</c> with <c>@p0</c> the string
        /// <c>"NaN"</c>, which keeps no stored number where Calcite keeps 1.5, and the projected sum as
        /// <c>(… + @p0)</c> with <c>"Infinity"</c>, which the service's arithmetic leaves undefined.
        /// </remarks>
        [Theory]
        [InlineData("""SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.n' RETURNING DOUBLE) <> CAST('NaN' AS DOUBLE)""", "NaN")]
        [InlineData("""SELECT c."id", JSON_VALUE(c."DOC", '$.n' RETURNING DOUBLE) + CAST('Infinity' AS DOUBLE) FROM items AS c""", "Infinity")]
        public void AConstantFoldedToANaNOrAnInfinityIsComputedInProcess(string sql, string folded)
        {
            var plan = Explain(sql, declared: false);
            var request = plan.IndexOf("CosmosToClrCursorConverter", StringComparison.Ordinal);

            request.Should().BeGreaterThan(0, "something is computed above the request: " + plan);
            plan[..request].Should().Contain(folded, "Calcite computes the folded constant: " + plan);
            plan[request..].Should().NotContain(folded, "the request holds no " + folded + ": " + plan);
            Sends(sql, declared: false, Schema, null, null).Should().NotContainAny(["NaN", "Infinity"], "nothing sent binds one");
        }

        /// <summary>
        /// A row count beside a request the facts graph decides not to send is read as Calcite's limit reads it, whatever
        /// the rows (<c>Calcite.ARowCountIsTakenToItsCeiling</c>): a negative count or a null fails the statement by name, as
        /// Calcite's own plan fails over the same documents, and a count the limit takes answers no rows; nothing is sent.
        /// </summary>
        /// <remarks>
        /// Read only once the request was sent, the count was never read beside one decided unsent, and the statement
        /// answered no rows where Calcite fails.
        /// </remarks>
        [Fact]
        public void ARowCountBesideARequestDecidedUnsentIsReadAsCalcitesLimitReadsIt()
        {
            const string Sql = """SELECT c."id" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.kind') = 'C' FETCH FIRST ? ROWS ONLY""";
            Explain(Sql, declared: true).Should().Contain("CosmosToClrCursorConverter(sends=[nothing])");

            var sent = new System.Collections.Generic.List<string>();
            using var source = Open(Schema, new RecordingExecutor(sent), null);
            int Rows(object? count)
            {
                using var connection = source.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = Sql.Replace(" items AS ", " \"items\" AS ");
                var parameter = command.CreateParameter();
                parameter.Value = count ?? DBNull.Value;
                command.Parameters.Add(parameter);

                var rows = 0;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows++;

                return rows;
            }

            foreach (var refused in new object?[] { -1L, null })
            {
                var failure = FluentActions.Invoking(() => Rows(refused)).Should().Throw<Exception>().Which;
                Chain(failure).OfType<CosmosExecutionException>().Should().ContainSingle()
                    .Which.Message.Should().StartWith("Parameter ?0 cannot be read as the FETCH count, as Calcite's limit reads it");
            }

            Rows(2L).Should().Be(0);
            sent.Should().BeEmpty("the request is decided unsent, whatever the count");

            static System.Collections.Generic.IEnumerable<Exception> Chain(Exception e)
            {
                for (Exception? x = e; x is not null; x = x.InnerException)
                    yield return x;
            }
        }

        /// <summary>
        /// A null test of a parameter of an interval type answers as Calcite's own plan answers it over the same rows,
        /// whether a null, an integer or a long is bound: nothing sent holds the parameter, and the test is computed in
        /// process, over a container whose every request answers both documents, whatever its <c>WHERE</c>.
        /// </summary>
        /// <remarks>
        /// Sent, the test read the count Calcite holds the interval in, and a null bound failed the statement where
        /// Calcite answers.
        /// </remarks>
        [Theory]
        [InlineData("""CAST(? AS INTERVAL DAY TO SECOND) IS NULL OR {id} = 'a'""")]
        [InlineData("""CAST(? AS INTERVAL DAY) IS NOT NULL""")]
        [InlineData("""CAST(? AS INTERVAL YEAR) IS NULL""")]
        [InlineData("""CAST(? AS INTERVAL YEAR TO MONTH) IS NULL OR {id} = 'a'""")]
        public void AnIntervalParametersNullTestAnswersAsCalcitesPlan(string predicate)
        {
            string[] documents = ["""{"id":"a","category":"x"}""", """{"id":"b","category":"x"}"""];
            using var calcite = Measurements.MaterialisedTable.Open();
            var ids = Measurements.MaterialisedTable.Of(org.apache.calcite.sql.type.SqlTypeName.VARCHAR, ["\"a\"", "\"b\""]).AddTo(calcite);

            foreach (var bound in new object?[] { null, 2, 2L })
            {
                var sent = new System.Collections.Generic.List<CosmosQuery>();
                var expected = Rows(calcite, $"SELECT \"v\" FROM \"{ids}\" WHERE " + predicate.Replace("{id}", "\"v\""), [bound]);
                Answer("SELECT c.\"id\" FROM items AS c WHERE " + predicate.Replace("{id}", "c.\"id\""), documents, [bound], sent)
                    .Should().Equal(expected, $"bound to {bound?.GetType().Name ?? "null"}, the statement answers as Calcite's plan");
                sent.SelectMany(q => q.Parameters).Select(p => p.Value).Should().OnlyContain(v => Equals(v, "a"), "nothing sent holds the parameter, only the literal beside it");
            }
        }

        /// <summary>
        /// A <c>RETURNING UUID</c> or a binary <c>RETURNING</c> compared with a parameter fails as Calcite's own plan fails
        /// over the same documents, which hold a string at the path: nothing sent holds the parameter, and the comparison is
        /// computed in process, over a container whose every request answers both documents, whatever its <c>WHERE</c>.
        /// </summary>
        /// <remarks>
        /// Sent, the parameter was bound as the <c>UuidValue</c> or the <c>ByteString</c> Calcite converts it to, which the
        /// SDK writes as an object no stored value equals, and the statement answered where Calcite fails reading the path.
        /// </remarks>
        [Fact]
        public void AUuidOrBinaryParametersComparisonFailsAsCalcitesPlan()
        {
            string[] documents = ["""{"id":"a","u":"00000000-0000-0000-0000-000000000001","b":"AQID"}""", """{"id":"b","u":"00000000-0000-0000-0000-000000000002","b":"BAUG"}"""];
            using var calcite = Measurements.MaterialisedTable.Open();
            var table = Measurements.MaterialisedTable.Of(documents).AddTo(calcite);

            foreach (var (reading, bound) in new (string, object)[]
            {
                ("""JSON_VALUE({d}, '$.u' RETURNING UUID)""", Guid.Parse("00000000-0000-0000-0000-000000000001")),
                ("""JSON_VALUE({d}, '$.b' RETURNING VARBINARY(4))""", new byte[] { 1, 2, 3 }),
                ("""JSON_VALUE({d}, '$.b' RETURNING BINARY(3))""", new byte[] { 1, 2, 3 }),
            })
            {
                var sent = new System.Collections.Generic.List<CosmosQuery>();
                var expected = Innermost(FluentActions.Invoking(() => Rows(calcite, $"SELECT \"k\" FROM \"{table}\" WHERE " + reading.Replace("{d}", "\"d\"") + " = ?", [bound]))
                    .Should().Throw<Exception>("Calcite's plan fails reading the string at the path as " + reading).Which);
                var failure = Innermost(FluentActions.Invoking(() => Answer("SELECT c.\"id\" FROM items AS c WHERE " + reading.Replace("{d}", "c.\"DOC\"") + " = ?", documents, [bound], sent))
                    .Should().Throw<Exception>("the statement fails as Calcite's plan: " + reading).Which);

                failure.GetType().Should().Be(expected.GetType(), reading);
                failure.Message.Should().Be(expected.Message, reading);
                sent.SelectMany(q => q.Parameters).Should().BeEmpty("nothing sent holds the parameter: " + reading);
            }
        }

        /// <summary>The exception that failed a statement, beneath every one wrapping it.</summary>
        static Exception Innermost(Exception e)
        {
            while (e.InnerException is not null)
                e = e.InnerException;

            return e;
        }

        /// <summary>
        /// A <c>RETURNING DATE</c>, <c>TIMESTAMP</c> or <c>TIME</c> accessor over a path declared to spell one, compared
        /// with a parameter, fails as Calcite's own plan fails over the same documents, which hold the spelling at the
        /// path: nothing sent holds the parameter, and the comparison is computed in process, over a container whose
        /// every request answers both documents, whatever its <c>WHERE</c>.
        /// </summary>
        /// <remarks>
        /// Sent, the parameter was bound as the count of days or milliseconds Calcite holds it in, which no stored string
        /// equals, and the statement answered where Calcite fails reading the string as the accessor's type.
        /// </remarks>
        [Fact]
        public void ADateOrTimeParametersComparisonFailsAsCalcitesPlan()
        {
            string[] dates = ["""{"id":"a","at":"2024-01-15"}""", """{"id":"b","at":"2024-01-16"}"""];
            string[] times = ["""{"id":"a","at":"12:34:56"}""", """{"id":"b","at":"12:35:00"}"""];
            using var calcite = Measurements.MaterialisedTable.Open();

            foreach (var (pattern, documents, reading, bound) in new (string, string[], string, object)[]
            {
                ("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", dates, """JSON_VALUE({d}, '$.at' RETURNING DATE) = ?""", new DateTime(2024, 1, 15)),
                ("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", dates, """JSON_VALUE({d}, '$.at' RETURNING DATE) <> ?""", new DateTime(2024, 1, 15)),
                ("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", dates, """JSON_VALUE({d}, '$.at' RETURNING TIMESTAMP) = ?""", new DateTime(2024, 1, 15)),
                ("^[0-9]{2}:[0-9]{2}:[0-9]{2}$", times, """JSON_VALUE({d}, '$.at' RETURNING TIME) = ?""", new TimeOnly(12, 34, 56)),
            })
            {
                var schema = $$"""{ "type": "object", "required": ["at"], "properties": { "at": { "type": "string", "pattern": "{{pattern}}" } } }""";
                var table = Measurements.MaterialisedTable.Of(documents).AddTo(calcite);
                var sent = new System.Collections.Generic.List<CosmosQuery>();
                var expected = Innermost(FluentActions.Invoking(() => Rows(calcite, $"SELECT \"k\" FROM \"{table}\" WHERE " + reading.Replace("{d}", "\"d\""), [bound]))
                    .Should().Throw<Exception>("Calcite's plan fails reading the string at the path as " + reading).Which);
                var failure = Innermost(FluentActions.Invoking(() => Answer("SELECT c.\"id\" FROM items AS c WHERE " + reading.Replace("{d}", "c.\"DOC\""), documents, [bound], sent, schema))
                    .Should().Throw<Exception>("the statement fails as Calcite's plan: " + reading).Which);

                failure.GetType().Should().Be(expected.GetType(), reading);
                failure.Message.Should().Be(expected.Message, reading);
                sent.SelectMany(q => q.Parameters).Should().BeEmpty("nothing sent holds the parameter: " + reading);
            }
        }

        /// <summary>
        /// A <c>RETURNING</c> collection compared with a parameter answers as Calcite's own plan answers over the same
        /// documents, an <c>int[]</c>, a <c>double[]</c> or a <c>string[]</c> bound: nothing sent holds the parameter, and
        /// the comparison is computed in process, over a container whose every request answers both documents, whatever its
        /// <c>WHERE</c>.
        /// </summary>
        /// <remarks>
        /// Sent, the parameter was bound as the <c>java.util.List</c> of Java boxes Calcite holds the collection in,
        /// which the SDK writes as an array of objects no stored array equals, so <c>&lt;&gt;</c> kept the document
        /// Calcite drops.
        /// </remarks>
        [Fact]
        public void ACollectionParametersComparisonAnswersAsCalcitesPlan()
        {
            string[] documents = ["""{"id":"a","a":[5,7],"d":[1.5,2.5],"v":["x","y"]}""", """{"id":"b","a":[1],"d":[3.5],"v":["z"]}"""];
            using var calcite = Measurements.MaterialisedTable.Open();
            var table = Measurements.MaterialisedTable.Of(documents).AddTo(calcite);

            foreach (var (reading, bound) in new (string, object)[]
            {
                ("""JSON_QUERY({d}, '$.a' RETURNING INTEGER ARRAY) <> ?""", new[] { 5, 7 }),
                ("""JSON_QUERY({d}, '$.a' RETURNING INTEGER ARRAY) <> ?""", new[] { 1 }),
                ("""JSON_QUERY({d}, '$.a' RETURNING INTEGER MULTISET) <> ?""", new[] { 5, 7 }),
                ("""JSON_QUERY({d}, '$.d' RETURNING DOUBLE ARRAY) <> ?""", new[] { 1.5, 2.5 }),
                ("""JSON_QUERY({d}, '$.v' RETURNING VARCHAR ARRAY) <> ?""", new[] { "x", "y" }),
            })
            {
                var sent = new System.Collections.Generic.List<CosmosQuery>();
                var expected = Rows(calcite, $"SELECT JSON_VALUE(\"d\", '$.id') FROM \"{table}\" WHERE " + reading.Replace("{d}", "\"d\""), [bound]);
                Answer("SELECT c.\"id\" FROM items AS c WHERE " + reading.Replace("{d}", "c.\"DOC\""), documents, [bound], sent)
                    .Should().Equal(expected, $"bound to {bound.GetType().Name}, the statement answers as Calcite's plan: " + reading);
                sent.SelectMany(q => q.Parameters).Should().BeEmpty("nothing sent holds the parameter: " + reading);
            }
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
        /// A text equality beside a cast of the same path to <c>INTEGER</c>, nothing declared: no Calcite line holds
        /// <c>IS_NUMBER</c> or <c>IS_DEFINED</c>, and over documents on which nothing raises the plan answers Calcite's rows.
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
        /// Through this connection, whose rules carry a filter past a window's partition keys, into a join's inputs and past
        /// a sample, a <c>WHERE</c> over a lateral whose side holds such a member over a traversal: the traversal's filter on
        /// <c>id</c> stays above the traversal in wr, wj2 and wm6, and is beneath it in wr2, whose window passes only
        /// <c>id</c>, declared and not.
        /// </summary>
        [Theory]
        [InlineData("wr", true)]
        [InlineData("wj2", true)]
        [InlineData("wm6", true)]
        [InlineData("wr2", false)]
        public void AConjunctCarriedPastAWindowAJoinOrASampleIsReadAtTheTraversalBeneath(string name, bool above)
        {
            foreach (var declared in new[] { true, false })
            {
                var plan = Explain(CosmosDeclaredFactPlanningTests.BesideACorrelate(name, "items"), declared, CosmosDeclaredFactPlanningTests.Traversed);
                var lines = plan.Split('\n');
                var unnest = System.Array.FindIndex(lines, l => l.Contains("CosmosUnnest", StringComparison.Ordinal));
                var filter = System.Array.FindIndex(lines, l => l.Contains("CosmosFilter(condition=[=($", StringComparison.Ordinal) && l.Contains("'1')", StringComparison.Ordinal));
                unnest.Should().BeGreaterThanOrEqualTo(0, $"{name} declared {declared}: " + plan);
                filter.Should().BeGreaterThanOrEqualTo(0, $"{name} declared {declared}: " + plan);
                (filter < unnest).Should().Be(above, $"{name} declared {declared}: " + plan);
            }
        }

        /// <summary>
        /// The statements of <see cref="CosmosDeclaredFactPlanningTests.AStackOfWheresOverATraversalIsReadOnceWithinABound"/>
        /// plan through this connection, declared and not, under both targets, with the correlate rule working the statement's
        /// answer out at most once on each planner and running the planner's rules over members no more times, over all its
        /// planners, than the bound given.
        /// </summary>
        [Theory]
        [InlineData("dp", 10, 64)]
        [InlineData("dn", 8, 160)]
        public void AStackOfWheresOverATraversalIsReadOnceWithinABound(string family, int depth, int bound)
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var declared in new[] { true, false })
                {
                    var seen = new Infrastructure.PlannersSeen();
                    Explain(CosmosDeclaredFactPlanningTests.Stacked(family, depth, "items"), declared, CosmosDeclaredFactPlanningTests.Traversed, target, seen).Should().NotBeEmpty();
                    var work = seen.Planners.Select(Adapter.Rel.Convert.CosmosFilterCorrelateRule.Work).ToList();
                    work.Should().OnlyContain(w => w.Times <= 1, $"{family}{depth} {target} declared {declared}");
                    work.Sum(w => w.Carries).Should().BeLessThanOrEqualTo(bound, $"{family}{depth} {target} declared {declared}");
                }
            }
        }

        /// <summary>
        /// The statements <see
        /// cref="CosmosDeclaredFactPlanningTests.AWhereOverATraversalSendsItsConditionsOnTheDocumentWhereNothingItNewlyEvaluatesCanFail"/>
        /// pins through this connection, whose Calcite moves a <c>WHERE</c>'s conjuncts of the document by its own rules before
        /// the planner registers the statement: each sends the request it sent before the correlate rule asked the facts graph
        /// anything, and the rule asks it nothing, declared and not, under both targets.
        /// </summary>
        [Theory]
        [InlineData("fr0", "Traversed",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id, \"$f5\": (IS_ARRAY(c.tags) ? c.tags : null), \"EXPR$0\": t0 } FROM items c JOIN t0 IN c.tags WHERE ((c.kind = @p0) AND (t0 = @p1)) | @p0=A, @p1=1",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id, \"$f5\": (IS_ARRAY(c.tags) ? c.tags : null), \"EXPR$0\": t0 } FROM items c JOIN t0 IN c.tags WHERE ((c.kind = @p0) AND (t0 = @p1)) | @p0=A, @p1=1")]
        [InlineData("frkk", "Traversed",
            "nothing",
            "nothing")]
        [InlineData("fruu", "Traversed",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id } FROM items c WHERE (c.kind = @p0) | @p0=A",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id } FROM items c WHERE (c.kind = @p0) | @p0=A")]
        [InlineData("ws1k", "TwoTraversals",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id } FROM items c WHERE ((c.kind = @p0) AND (c.n > @p1)) | @p0=A, @p1=50",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id } FROM items c WHERE ((c.kind = @p0) AND IS_DEFINED(c.body) AND (NOT IS_NUMBER(c.body) OR (c.body = @p1)) AND (c.n > @p2)) | @p0=A, @p1=7, @p2=50")]
        [InlineData("j3n", "Traversed",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id } FROM items c WHERE (c.kind = @p0) | @p0=A",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id } FROM items c WHERE ((c.kind = @p0) AND IS_DEFINED(c.body) AND (NOT IS_NUMBER(c.body) OR (c.body = @p1))) | @p0=A, @p1=7")]
        [InlineData("w13", "Traversed",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id, \"$f5\": (IS_ARRAY(c.tags) ? c.tags : null), \"EXPR$0\": t0 } FROM items c JOIN t0 IN c.tags WHERE ((c.kind = @p0) AND (IS_DEFINED(t0) AND NOT IS_NULL(t0) AND (t0 != @p1))) | @p0=B, @p1=zz",
            "SELECT VALUE { \"DOC\": c, \"id\": c.id, \"$f5\": (IS_ARRAY(c.tags) ? c.tags : null), \"EXPR$0\": t0 } FROM items c JOIN t0 IN c.tags WHERE ((c.kind = @p0) AND (IS_DEFINED(t0) AND NOT IS_NULL(t0) AND (t0 != @p1))) | @p0=B, @p1=zz")]
        public void AWhereOverATraversalSendsWhatItSentAndTheCorrelateRuleAsksNothing(string name, string schema, string declaredSends, string undeclaredSends)
        {
            var wrong = new System.Collections.Generic.List<string>();
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                foreach (var declared in new[] { true, false })
                {
                    var seen = new Infrastructure.PlannersSeen();
                    var sent = Sends(CosmosDeclaredFactPlanningTests.BesideACorrelate(name, "items"), declared, schema == "TwoTraversals" ? CosmosDeclaredFactPlanningTests.TwoTraversals : CosmosDeclaredFactPlanningTests.Traversed, target, seen);
                    var questions = seen.Planners.Sum(p => Adapter.Rel.Convert.CosmosFilterCorrelateRule.Work(p).Questions);
                    TestContext.Current.TestOutputHelper?.WriteLine($"{name} {target} declared {declared}: {sent}; {questions} questions");
                    if (sent != (declared ? declaredSends : undeclaredSends) || questions != 0)
                        wrong.Add($"{name} {target} declared {declared} sends {sent}, {questions} questions");
                }
            }

            wrong.Should().BeEmpty();
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

        /// <summary>
        /// Required strings: an instant to the second <c>at</c>, a calendar date <c>d</c>, and a time of day to the second
        /// <c>t</c> and to the minute <c>m</c>.
        /// </summary>
        const string Temporal = """{ "type": "object", "required": ["at", "d", "t", "m"], "properties": { "at": { "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$" }, "d": { "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$" }, "t": { "type": "string", "pattern": "^[0-9]{2}:[0-9]{2}:[0-9]{2}$" }, "m": { "type": "string", "pattern": "^[0-9]{2}:[0-9]{2}$" } } }""";

        /// <summary>
        /// A sort keyed by a cast the adapter's own transpose cannot prove orders as its operand stays above the cast in
        /// the host's planner too, declared or not, on both targets: no request is sorted on the stored text.
        /// </summary>
        /// <remarks>
        /// Calcite's own <c>SortProjectTransposeRule</c>, among the host's default rules, maps a key that is a cast to the
        /// cast's operand wherever it calls the cast monotonic, and it calls a cast of text to a time of day monotonic.
        /// The projection is split, the accessor sent and the cast left in process, so the sort beneath it was sent as an
        /// <c>ORDER BY</c> on the stored text, which orders <c>12:30:99</c> before <c>12:31:00</c> where Calcite's cast
        /// reads the first as 12:31:39 (<c>Calcite.TimeCastRollsAClocksFields</c>). The convention takes that rule out of
        /// the planner and leaves its own guarded one, <c>CosmosSortProjectTransposeRule</c>.
        /// </remarks>
        [Theory]
        [InlineData("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.t') AS TIME) AS "v" FROM items AS c ORDER BY 2 FETCH NEXT 10 ROWS ONLY""")]
        [InlineData("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.t') AS TIME(0)) AS "v" FROM items AS c ORDER BY 2 NULLS FIRST FETCH NEXT 10 ROWS ONLY""")]
        [InlineData("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.m') AS TIME) AS "v" FROM items AS c ORDER BY 2 DESC FETCH NEXT 10 ROWS ONLY""")]
        [InlineData("""SELECT c."id" FROM items AS c ORDER BY CAST(JSON_VALUE(c."DOC", '$.t') AS TIME)""")]
        [InlineData("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP(0) FORMAT 'YYYY-MM-DD''T''HH24:MI:SS''Z''') AS "v" FROM items AS c ORDER BY 2 FETCH NEXT 10 ROWS ONLY""")]
        [InlineData("""SELECT c."id" FROM items AS c ORDER BY CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP(0) FORMAT 'YYYY-MM-DD''T''HH24:MI:SS''Z''') DESC""")]
        [InlineData("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.d') AS DATE FORMAT 'YYYY-MM-DD') AS "v" FROM items AS c ORDER BY 2 FETCH NEXT 10 ROWS ONLY""")]
        [InlineData("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.t') AS TIME(0) FORMAT 'HH24:MI:SS') AS "v" FROM items AS c ORDER BY 2 FETCH NEXT 10 ROWS ONLY""")]
        public void ASortThroughACastTheTransposeCannotProveIsNotSentOnTheStoredText(string sql)
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
                foreach (var declared in new[] { true, false })
                    Sends(sql, declared, Temporal, target, new Infrastructure.PlannersSeen())
                        .Should().NotContain("ORDER BY", $"{target}, declared {declared}: the cast's order is not the text's");
        }

        /// <summary>
        /// The control: a sort keyed by a calendar date's plain cast over the declared date is still sent, the adapter's
        /// transpose proving from the declaration that the stored text orders as the dates.
        /// </summary>
        [Fact]
        public void ASortThroughACalendarDatesCastIsStillSentOverTheDeclaredDate()
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
                Sends("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.d') AS DATE) AS "v" FROM items AS c ORDER BY 2 FETCH NEXT 10 ROWS ONLY""", true, Temporal, target, new Infrastructure.PlannersSeen())
                    .Should().Contain("ORDER BY c.d ASC", $"{target}: the declaration proves the date's text orders as the dates");
        }

        /// <summary>
        /// A sort keyed by a cast orders the rows by the value the cast reads: a time of day whose second is past its range
        /// after the time it rolls past, and an unpadded date by its day rather than by its text.
        /// </summary>
        /// <remarks>
        /// The documents are listed in the order the stored text sorts, which is what a request sorted on it would be
        /// answered in; each statement's rows are in the order Calcite's cast gives. The clock is declared, where the
        /// sort was sent on the text; the date is not, where the transposed sort ordered the text in process.
        /// </remarks>
        [Fact]
        public void ASortThroughACastOrdersByTheValueTheCastReads()
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                AnswerInStoredOrder("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.t') AS TIME) AS "v" FROM items AS c ORDER BY 2 FETCH NEXT 10 ROWS ONLY""", Temporal, target,
                    [
                        """{ "id": "a", "category": "x", "at": "2024-01-15T12:30:00Z", "d": "2024-01-15", "t": "12:30:99", "m": "12:30" }""",
                        """{ "id": "b", "category": "x", "at": "2024-01-15T12:30:00Z", "d": "2024-01-15", "t": "12:31:00", "m": "12:30" }""",
                    ]).Should().Equal(["b", "a"], $"{target}: 12:30:99 is 12:31:39 to Calcite, after 12:31:00");

                AnswerInStoredOrder("""SELECT c."id" FROM items AS c ORDER BY CAST(JSON_VALUE(c."DOC", '$.d') AS DATE)""", null, target,
                    [
                        """{ "id": "a", "category": "x", "d": "2024-10-1" }""",
                        """{ "id": "b", "category": "x", "d": "2024-9-1" }""",
                    ]).Should().Equal(["b", "a"], $"{target}: 2024-9-1 is before 2024-10-1, whatever their text");
            }
        }

        /// <summary>
        /// A sort keyed by a cast with a format orders the rows by the instant the parse reads, over the declared instant:
        /// a day past its month's range after the day it rolls past.
        /// </summary>
        /// <remarks>
        /// As above, the documents are listed in the order the stored text sorts. The parse reads <c>2016-11-61</c> as
        /// 31 December (<c>Calcite.ParsesReadTheirFieldsLeniently</c>), so no sort through it is sent.
        /// </remarks>
        [Fact]
        public void ASortThroughAParseOrdersByTheInstantTheParseReads()
        {
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
                AnswerInStoredOrder("""SELECT c."id", CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP(0) FORMAT 'YYYY-MM-DD''T''HH24:MI:SS''Z''') AS "v" FROM items AS c ORDER BY 2 FETCH NEXT 10 ROWS ONLY""", Temporal, target,
                    [
                        """{ "id": "a", "category": "x", "at": "2016-11-61T00:00:00Z", "d": "2016-11-30", "t": "00:00:00", "m": "00:00" }""",
                        """{ "id": "b", "category": "x", "at": "2016-12-01T00:00:00Z", "d": "2016-12-01", "t": "00:00:00", "m": "00:00" }""",
                    ]).Should().Equal(["b", "a"], $"{target}: 2016-11-61 is 31 December to Calcite, after 1 December");
        }

    }

}
