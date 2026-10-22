using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// <see cref="CosmosEval"/> held to the service (spec §3.16): each form of the family the reader reads, over a path
    /// holding each kind of value, kept by <c>WHERE</c>, by <c>WHERE NOT</c> or by neither as <c>CosmosEval</c> says, and
    /// projected alike. Service-gated, in a database of its own.
    /// </summary>
    public sealed class CosmosEvalServiceTests : IClassFixture<CosmosEvalServiceTests.Fixture>
    {

        /// <summary>
        /// Every kind, and where the targets part: 2^53 + 1 written with a point, which the emulator holds as a double and
        /// Azure as an int64; one in [2^63, 2^64), which both compare as its double; and strings holding U+0000, which the
        /// emulator stores as U+FDD0 then 0 and orders as stored.
        /// </summary>
        static readonly string[] Values = ["null", "true", "false", "30", "30.0", "2.5", "-1", "9007199254740993", "9007199254740993.0", "9223372036854775809", "\"\"", "\"30\"",
            "\"bikes\"", "\"a\\u0000\"", "\"bike\\u0000x\"", "\"\\u0000\"", "\"\\uE000\"", "\"\\uD83D\\uDE00\"", "{}", "[]"];

        static readonly CosmosParameter[] Bound = [new("@s", "bikes"), new("@t", "30"), new("@n", 30L), new("@f", 2.5), new("@b", true), new("@c", "a"),
            new("@m", 9007199254740993L), new("@h", 9223372036854775808d)];

        /// <summary>The forms: every test, each comparison against a literal of each kind, and connectives over them; a bare path and a constant the reader reads as opaque.</summary>
        static readonly string[] Forms =
        [
            .. new[] { "DEFINED", "NULL", "PRIMITIVE", "STRING", "NUMBER", "BOOL", "OBJECT", "ARRAY" }.SelectMany(t => new[] { $"IS_{t}(c.v)", $"NOT IS_{t}(c.v)" }),
            .. new[] { "=", "!=", "<", "<=", ">", ">=" }.SelectMany(op => new[] { "@s", "@t", "@n", "@f", "@m", "@h" }.Select(p => $"(c.v {op} {p})")),
            "(c.v = @b)", "(c.v != @b)",
            "(NOT (c.v = @s))", "((c.v > @c) AND (c.v < @s))", "((c.v = @s) OR IS_NULL(c.v))", "(IS_PRIMITIVE(c.v) AND NOT IS_NULL(c.v) AND (c.v != @s))",
        ];

        readonly Fixture _service;

        public CosmosEvalServiceTests(Fixture service) => _service = service;

        [Fact]
        public async Task EveryFormIsKeptAsCosmosEvalSays()
        {
            if (_service.Container is null)
                Assert.Skip("This needs a service. " + _service.Unavailable);

            var documents = await _service.Write("eval", Values.Select(v => $"{{\"v\":{v}}}"));
            var wrong = new List<string>();
            foreach (var text in Forms)
            {
                var form = CosmosSqlFormReader.Read(text, Bound).Form;
                form.Kind.Should().NotBe(ServiceFormKind.Opaque, text);
                foreach (var (id, observed) in await _service.Observe("eval", text, Bound))
                    if (CosmosEval.On(form, documents[id], Target) is var said && said != observed)
                        wrong.Add($"{text} over {Documents.Json(documents[id])}: the service {observed}, CosmosEval {said?.ToString() ?? "nothing"}");
            }

            wrong.Should().BeEmpty();
        }

        /// <summary>
        /// The two targets' models part on these values, so the service holds one of them: the emulator's where the
        /// endpoint is one, a local one named by <c>COSMOS_TEST_ENDPOINT</c> among them.
        /// </summary>
        internal static RenderingTarget Target => CosmosEmulator.IsEmulator || new Uri(CosmosEmulator.Endpoint!).IsLoopback ? RenderingTarget.Emulator : RenderingTarget.Azure;

        /// <summary>Where the two models part on these values, so that whichever passes is the one the service is.</summary>
        [Fact]
        public void TheTargetsModelsPart()
        {
            var parted = Forms.SelectMany(text => Values.Select(v => (Form: CosmosSqlFormReader.Read(text, Bound).Form, Document: (J.Obj)Fixture.Read(JsonDocument.Parse($"{{\"v\":{v}}}").RootElement)!)))
                .Where(c => CosmosEval.On(c.Form, c.Document, RenderingTarget.Emulator) is { } e && CosmosEval.On(c.Form, c.Document, RenderingTarget.Azure) is { } a && e != a)
                .Select(c => $"{c.Form.Text} over {Documents.Json(c.Document)}").ToList();
            parted.Should().Contain(p => p.Contains("9007199254740993.0")).And.Contain(p => p.Contains("bike"));
        }

        /// <summary>A database of its own, named by the framework, holding one container whose documents each test writes under a partition of its own.</summary>
        public class Fixture : IAsyncLifetime
        {

            readonly string _database;
            CosmosClient? _client;

            public Fixture() : this("eval") { }

            protected Fixture(string name) => _database = $"calcite_cosmos_{name}_" + Regex.Replace(RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

            public Container? Container { get; protected set; }

            public string? Unavailable { get; private set; }

            public async ValueTask InitializeAsync()
            {
                if (CosmosEmulator.Endpoint is not string endpoint || CosmosEmulator.Key is not string key)
                {
                    Unavailable = "No account is reachable.";
                    return;
                }

                var options = new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, MaxRetryAttemptsOnRateLimitedRequests = 0 };
                if (CosmosEmulator.IsEmulator)
                    (options.LimitToEndpoint, options.ServerCertificateCustomValidationCallback) = (true, (_, _, _) => true);

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 30 : 120));
                    _client = new CosmosClient(endpoint, key, options);
                    await Seed((await _client.CreateDatabaseIfNotExistsAsync(_database, cancellationToken: cts.Token)).Database, cts.Token);
                }
                catch (Exception e)
                {
                    Unavailable = e.Message;
                }
            }

            /// <summary>Creates what the tests read: here one container, partitioned on <c>/pk</c>.</summary>
            protected virtual async Task Seed(Database database, CancellationToken token) =>
                Container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("documents", "/pk"), cancellationToken: token)).Container;

            public ValueTask DisposeAsync()
            {
                try { _client?.GetDatabase(_database).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }
                _client?.Dispose();
                return default;
            }

            /// <summary>Writes documents, each JSON text an object, under a partition of their own, and returns them by the id each was given.</summary>
            internal async Task<Dictionary<string, J.Obj>> Write(string partition, IEnumerable<string> documents)
            {
                var written = new Dictionary<string, J.Obj>();
                foreach (var (json, i) in documents.Select((d, i) => (d, i)))
                {
                    var id = $"{partition}-{i}";
                    var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
                    node["id"] = id;
                    node["pk"] = partition;
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));
                    (await Container!.UpsertItemStreamAsync(stream, new PartitionKey(partition))).EnsureSuccessStatusCode();
                    written[id] = (J.Obj)Read(JsonDocument.Parse(json).RootElement)!;
                }

                return written;
            }

            /// <summary>
            /// What the service makes of a condition on each document of a partition: kept by <c>WHERE</c>, by
            /// <c>WHERE NOT</c>, or by neither; a projection that does not say the same is a failure of its own.
            /// </summary>
            internal async Task<Dictionary<string, ServiceOutcomes>> Observe(string partition, string text, IReadOnlyList<CosmosParameter> parameters)
            {
                async Task<List<JsonElement>> Query(string sql)
                {
                    var rows = new List<JsonElement>();
                    var query = parameters.Aggregate(new QueryDefinition(sql), (q, p) => q.WithParameter(p.Name, p.Value));
                    using var feed = Container!.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partition) });
                    while (feed.HasMoreResults)
                    {
                        using var response = (await feed.ReadNextAsync()).EnsureSuccessStatusCode();
                        using var json = await JsonDocument.ParseAsync(response.Content);
                        rows.AddRange(json.RootElement.GetProperty("Documents").EnumerateArray().Select(e => e.Clone()));
                    }

                    return rows;
                }

                var kept = (await Query($"SELECT VALUE c.id FROM c WHERE ({text})")).Select(e => e.GetString()!).ToHashSet();
                var dropped = (await Query($"SELECT VALUE c.id FROM c WHERE NOT ({text})")).Select(e => e.GetString()!).ToHashSet();
                var observed = new Dictionary<string, ServiceOutcomes>();
                foreach (var row in await Query($"SELECT c.id, ({text}) AS r FROM c"))
                {
                    var id = row.GetProperty("id").GetString()!;
                    var outcome = kept.Contains(id) ? ServiceOutcomes.True : dropped.Contains(id) ? ServiceOutcomes.False : ServiceOutcomes.Undefined;
                    var projected = row.TryGetProperty("r", out var r) ? r.ValueKind switch { JsonValueKind.True => ServiceOutcomes.True, JsonValueKind.False => ServiceOutcomes.False, _ => ServiceOutcomes.Undefined } : ServiceOutcomes.Undefined;
                    observed[id] = outcome == projected ? outcome : ServiceOutcomes.Fails;
                }

                return observed;
            }

            /// <summary>A JSON value as the evaluators read one; <c>null</c> for nothing.</summary>
            internal static J? Read(JsonElement value) => value.ValueKind switch
            {
                JsonValueKind.Null => new J.Null(),
                JsonValueKind.True => new J.Bool(true),
                JsonValueKind.False => new J.Bool(false),
                JsonValueKind.Number => decimal.TryParse(value.GetRawText(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? new J.Num(d) : new J.Huge(value.GetDouble()),
                JsonValueKind.String => new J.Str(value.GetString()!),
                JsonValueKind.Object => new J.Obj(new(value.EnumerateObject().ToDictionary(p => p.Name, p => Read(p.Value)!), StringComparer.Ordinal)),
                JsonValueKind.Array => new J.Arr([.. value.EnumerateArray().Select(e => Read(e)!)]),
                _ => null,
            };

        }

    }

}
