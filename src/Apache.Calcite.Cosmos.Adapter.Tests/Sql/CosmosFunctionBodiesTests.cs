using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;

using Azure.Identity;

using FluentAssertions;

using Microsoft.Azure.Cosmos;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// Holds <see cref="CosmosFunctionBodies"/> to the service, by asking both the same question about
    /// the same document and comparing the answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bodies exist so a type test can be evaluated outside the Cosmos convention. That is only
    /// sound if it answers what the service would have answered, and the two distinctions it turns on —
    /// absent against null, scalar against structure — are exactly the ones a reading of two
    /// documentation sets is least likely to settle. So this asks Cosmos.
    /// </para>
    /// <para>
    /// Every kind of JSON value gets a document, and the absent case gets one with the property missing
    /// rather than null. The service answers through a query; the bodies answer over the document text
    /// the same query returns, so neither side is given a tidied-up input.
    /// </para>
    /// <para>
    /// Needs a real account — <c>COSMOS_TEST_ENDPOINT</c>, reached with <c>DefaultAzureCredential</c> —
    /// and reports inconclusive without one. The container is provisioned outside the fixture, an Entra
    /// token being unable to create one:
    /// </para>
    /// <code>
    /// az cosmosdb sql container create --account-name &lt;account&gt; --resource-group &lt;rg&gt; \
    ///     --database-name calcite_cosmos_pointread --name jsonkinds --partition-key-path "/k"
    /// </code>
    /// </remarks>
    public class CosmosFunctionBodiesTests : IClassFixture<CosmosFunctionBodiesTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown. xUnit drives these through a fixture the
        /// class asks for rather than through static hooks the framework calls by attribute.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await ClassInitialize();

            public ValueTask DisposeAsync() { ClassCleanup(); return default; }

        }

        static readonly string? Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT");
        static readonly string? Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY");

        const string DatabaseName = "calcite_cosmos_pointread";
        const string ContainerName = "jsonkinds";
        const string Partition = "kinds";

        static CosmosClient? _client;
        static Container? _container;
        static string? _initializationFailure;

        /// <summary>One document per JSON kind, by the id that names the kind.</summary>
        static readonly (string Id, string Json)[] Kinds =
        {
            ("string", """{"id":"string","k":"kinds","v":"x"}"""),
            ("integer", """{"id":"integer","k":"kinds","v":1}"""),
            ("double", """{"id":"double","k":"kinds","v":1.5}"""),
            ("true", """{"id":"true","k":"kinds","v":true}"""),
            ("false", """{"id":"false","k":"kinds","v":false}"""),
            ("null", """{"id":"null","k":"kinds","v":null}"""),
            ("array", """{"id":"array","k":"kinds","v":[1,2]}"""),
            ("object", """{"id":"object","k":"kinds","v":{"a":1}}"""),
            ("absent", """{"id":"absent","k":"kinds","w":1}"""),
            ("empty-string", """{"id":"empty-string","k":"kinds","v":""}"""),
            ("zero", """{"id":"zero","k":"kinds","v":0}"""),
            ("empty-array", """{"id":"empty-array","k":"kinds","v":[]}"""),
            ("empty-object", """{"id":"empty-object","k":"kinds","v":{}}"""),
        };

        static async Task ClassInitialize()
        {
            if (string.IsNullOrEmpty(Endpoint))
            {
                _initializationFailure = "COSMOS_TEST_ENDPOINT is not set.";
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var options = new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway };

                var client = string.IsNullOrEmpty(Key)
                    ? new CosmosClient(Endpoint, new DefaultAzureCredential(), options)
                    : new CosmosClient(Endpoint, Key, options);

                var container = client.GetContainer(DatabaseName, ContainerName);
                await container.ReadContainerAsync(cancellationToken: cts.Token);

                foreach (var (_, json) in Kinds)
                {
                    using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var response = await container.CreateItemStreamAsync(stream, new PartitionKey(Partition), cancellationToken: cts.Token);

                    if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
                        response.EnsureSuccessStatusCode();
                }

                _client = client;
                _container = container;
            }
            catch (Exception e)
            {
                _initializationFailure = e.ToString();
                _client?.Dispose();
                _client = null;
                _container = null;
            }
        }

        static void ClassCleanup()
        {
            _client?.Dispose();
            _client = null;
            _container = null;
        }

        static Container Container()
        {
            if (_container is null)
                Assert.Skip("This differential needs a real account. " + (_initializationFailure ?? "The fixture did not run."));

            return _container!;
        }

        /// <summary>The tests, named as the service names them, against the body that answers each.</summary>
        static readonly (string Name, Func<string, string, bool> Body)[] Tests =
        {
            ("IS_DEFINED", CosmosFunctionBodies.IsDefined),
            ("IS_NULL", CosmosFunctionBodies.IsNull),
            ("IS_STRING", CosmosFunctionBodies.IsString),
            ("IS_NUMBER", CosmosFunctionBodies.IsNumber),
            ("IS_BOOL", CosmosFunctionBodies.IsBool),
            ("IS_ARRAY", CosmosFunctionBodies.IsArray),
            ("IS_OBJECT", CosmosFunctionBodies.IsObject),
            ("IS_PRIMITIVE", CosmosFunctionBodies.IsPrimitive),
        };

        [Fact]
        public async Task TheBodiesAnswerWhatTheServiceAnswers()
        {
            var projections = new List<string>();
            foreach (var (name, _) in Tests)
                projections.Add($"{name}(c.v) AS \"{name}\"");

            var sql = "SELECT c.id AS \"id\", c AS \"doc\", " + string.Join(", ", projections) + " FROM c";

            using var iterator = Container().GetItemQueryStreamIterator(
                new QueryDefinition(sql),
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(Partition) });

            var disagreements = new List<string>();
            var report = new StringBuilder();
            report.AppendLine("kind            " + string.Join(" ", Array.ConvertAll(Tests, t => t.Name.Replace("IS_", "").PadLeft(10))));

            var seen = 0;

            while (iterator.HasMoreResults)
            {
                using var response = await iterator.ReadNextAsync();
                response.EnsureSuccessStatusCode();

                using var page = await JsonDocument.ParseAsync(response.Content);

                foreach (var row in page.RootElement.GetProperty("Documents").EnumerateArray())
                {
                    seen++;

                    var id = row.GetProperty("id").GetString()!;
                    var document = row.GetProperty("doc").GetRawText();

                    var cells = new List<string>();

                    foreach (var (name, body) in Tests)
                    {
                        // A test the service answers as undefined is absent from the row rather than
                        // false, which is itself one of the semantics being checked.
                        var service = row.TryGetProperty(name, out var answer) && answer.ValueKind == JsonValueKind.True;
                        var ours = body(document, "$.v");

                        cells.Add((service == ours ? "" : "!") + (service ? "T" : "F") + "/" + (ours ? "T" : "F"));

                        if (service != ours)
                            disagreements.Add($"{id}.{name}: service={service} body={ours}");
                    }

                    report.AppendLine(id.PadRight(15) + string.Join(" ", cells.ConvertAll(c => c.PadLeft(10))));
                }
            }

            Console.WriteLine(report.ToString());

            var log = Environment.GetEnvironmentVariable("COSMOS_MEASUREMENT_LOG");
            if (string.IsNullOrEmpty(log) == false)
                System.IO.File.AppendAllText(log, report.ToString());

            seen.Should().Be(Kinds.Length, "every seeded document should come back");
            disagreements.Should().BeEmpty("the bodies exist to answer what the service answers");
        }

    }

}
