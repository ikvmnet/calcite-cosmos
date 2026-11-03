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
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

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
        /// Azure as an int64; one in [2^63, 2^64), which both compare as its double; and strings holding U+0000 or U+FDD0,
        /// which the emulator stores as U+FDD0 then 0 or 1 and orders as stored.
        /// </summary>
        static readonly string[] Values = ["null", "true", "false", "30", "30.0", "2.5", "-1", "9007199254740993", "9007199254740993.0", "9223372036854775809", "\"\"", "\"30\"",
            "\"bikes\"", "\"a\\u0000\"", "\"bike\\u0000x\"", "\"\\u0000\"", "\"\\uE000\"", "\"\\uD83D\\uDE00\"", "\"\\uFDD0\"", "\"\\u0000\\uFDD0\"", "{}", "[]"];

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
        /// The forms <see cref="CosmosEval"/> gained with Stage 3, which the reader does not read, held to the service
        /// (line 4b): the ternary (S1), an element of an array (M4) and two paths compared, each way round and on the
        /// diagonal (<c>Service.TwoPathsCompareAsAPathAndALiteral</c>), 9007199254740993.0 against itself among them. Each
        /// is built and printed as S prints it, over documents holding a second path <c>w</c>.
        /// </summary>
        [Fact]
        public async Task EveryStageThreeFormIsKeptAsCosmosEvalSays()
        {
            if (_service.Container is null)
                Assert.Skip("This needs a service. " + _service.Unavailable);

            static string P(string name) => SqlValue.Spell(JsonDocumentPath.Root.Property(name));
            static string E(long i) => SqlValue.Spell(JsonDocumentPath.Root.Property("v").Element(i));
            CosmosServiceTest[] ops = [CosmosServiceTest.Equal, CosmosServiceTest.NotEqual, CosmosServiceTest.Less, CosmosServiceTest.LessOrEqual, CosmosServiceTest.Greater, CosmosServiceTest.GreaterOrEqual];
            var forms = new List<ServiceForm>();
            forms.AddRange(ops.Select(op => ServiceForm.ComparePathsOf(op, P("v"), P("w"))));
            forms.AddRange(ops.Select(op => ServiceForm.NotOf(ServiceForm.ComparePathsOf(op, P("v"), P("w")))));
            forms.AddRange(ops.Select(op => ServiceForm.ComparePathsOf(op, P("v"), P("v"))));
            var (t, f) = (ServiceForm.ConstantOf(true), ServiceForm.ConstantOf(false));
            foreach (var c in new[] { ServiceForm.BareOf(P("v")), ServiceForm.CompareOf(CosmosServiceTest.Equal, P("v"), Of(30m)), ServiceForm.TestOf(CosmosServiceTest.IsString, P("v")),
                ServiceForm.NotOf(ServiceForm.CompareOf(CosmosServiceTest.Less, P("v"), Of("b"))) })
            {
                forms.Add(ServiceForm.ChosenOf(c, t, f));
                forms.Add(ServiceForm.ChosenOf(c, ServiceForm.CompareOf(CosmosServiceTest.Equal, P("w"), Of(30m)), ServiceForm.TestOf(CosmosServiceTest.IsNull, P("w"))));
                forms.Add(ServiceForm.NotOf(ServiceForm.ChosenOf(c, ServiceForm.CompareOf(CosmosServiceTest.Less, P("w"), Of("b")), f)));
            }

            foreach (var i in new long[] { 0, 1 })
            {
                forms.Add(ServiceForm.TestOf(CosmosServiceTest.IsDefined, E(i)));
                forms.Add(ServiceForm.CompareOf(CosmosServiceTest.Equal, E(i), Of(1m)));
                forms.Add(ServiceForm.NotOf(ServiceForm.CompareOf(CosmosServiceTest.Less, E(i), Of(2m))));
            }

            string[] others = ["null", "30", "2.5", "9007199254740993.0", "\"a\"", "\"b\"", "\"\\u0000\"", "\"\\uFFFF\"", "true", "[1]"];
            var documents = await _service.Write("staged", Values.Concat(["[1,2]", "[2]"]).SelectMany(v => others.Select(w => $"{{\"v\":{v},\"w\":{w}}}").Append($"{{\"v\":{v}}}")));
            var (wrong, judged) = (new List<string>(), 0);
            foreach (var form in forms)
            {
                var parameters = new CosmosParameterList();
                var text = RenderingPrinter.Print(form, form.Paths.ToDictionary(p => p, CosmosEval.PathOf), "c", parameters);
                foreach (var (id, observed) in await _service.Observe("staged", text, parameters.Parameters))
                    if (CosmosEval.On(form, documents[id], Target) is { } said && ++judged > 0 && said != observed)
                        wrong.Add($"{text} over {Documents.Json(documents[id])}: the service {observed}, CosmosEval {said}");
            }

            wrong.Should().BeEmpty();
            judged.Should().BeGreaterThan(Target == RenderingTarget.Emulator ? 5_000 : 1_000);
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
            bool _created;

            public Fixture() : this("eval") { }

            protected Fixture(string name) => _database = $"calcite_cosmos_{name}_" + Regex.Replace(RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

            public Container? Container { get; protected set; }

            public string? Unavailable { get; private set; }

            /// <summary>What the service charged for the documents written and the queries asked, in request units: what a run on an account costs.</summary>
            public double Charged { get; private set; }

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
                    var database = (await _client.CreateDatabaseIfNotExistsAsync(_database, cancellationToken: cts.Token)).Database;
                    _created = true;
                    await Seed(database, cts.Token);
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
                // Only a database this fixture made is deleted: with no service answering there is none, and asking
                // would fail the class's cleanup on a leg that names no account.
                if (_created)
                    try { _client!.GetDatabase(_database).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

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
                    using var response = (await Container!.UpsertItemStreamAsync(stream, new PartitionKey(partition))).EnsureSuccessStatusCode();
                    Charged += response.Headers.RequestCharge;
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
                        Charged += response.Headers.RequestCharge;
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
