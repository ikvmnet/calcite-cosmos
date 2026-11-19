using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// The evaluator the correlate pins answer their requests by (<see cref="DocumentRequests"/>) held to the service: each
    /// request with a <c>WHERE</c> the pins send, on either target, declared or not, sent to the service over the pins'
    /// documents, keeps the documents the evaluator keeps. Service-gated, in a database of its own.
    /// </summary>
    public sealed class CorrelatePinsServiceTests : IClassFixture<CorrelatePinsServiceTests.Fixture>
    {

        /// <summary>The correlate pins that answer over documents, each run with every row it is given.</summary>
        static readonly string[] Pins =
        [
            nameof(CosmosDeclaredFactPlanningTests.AStatementIsTransposedAtEveryCorrelateOrAtNone),
            nameof(CosmosDeclaredFactPlanningTests.AConjunctMovedIntoASideIsMovedOnlyAsTheCorrelateItReachesTakesIt),
            nameof(CosmosDeclaredFactPlanningTests.ATermTheWholeReadsIntoOneSideStaysAboveACorrelate),
            nameof(CosmosDeclaredFactPlanningTests.AConjunctOfBothSidesAboveACorrelateIsPlannedAsOneOfCalcitesPlans),
            nameof(CosmosDeclaredFactPlanningTests.AConjunctKeptAboveACorrelateIsReadBesideTheRest),
            nameof(CosmosDeclaredFactPlanningTests.AConditionReadAsFalseMovesNothingIntoACorrelatesSide),
            nameof(CosmosDeclaredFactPlanningTests.AConjunctThatDecidesTheRestIsNotMovedIntoACorrelatesSide),
            nameof(CosmosDeclaredFactPlanningTests.AConjunctMovedBesideADerivedTermIsNotEvaluatedAlone),
            nameof(CosmosDeclaredFactPlanningTests.APartLeftAboveACorrelateIsReadBesideWhatItMoves),
        ];

        readonly Fixture _service;

        public CorrelatePinsServiceTests(Fixture service) => _service = service;

        /// <summary>
        /// Every request with a <c>WHERE</c> the correlate pins send is answered by the service with the documents the
        /// evaluator keeps, judged on the service's own target.
        /// </summary>
        [Fact]
        public async Task EveryWhereThePinsSendKeepsWhatTheEvaluatorKeeps()
        {
            if (_service.Container is null)
                Assert.Skip("This needs a service. " + _service.Unavailable);

            var sent = new List<(CosmosServiceTarget Target, CosmosQuery Query, string[] Documents)>();
            CosmosDeclaredFactPlanningTests.Sending.Value = sent;
            try
            {
                var pins = new CosmosDeclaredFactPlanningTests();
                foreach (var name in Pins)
                {
                    var method = typeof(CosmosDeclaredFactPlanningTests).GetMethod(name)!;
                    foreach (var row in method.GetCustomAttributes<InlineDataAttribute>())
                    {
                        // A pin's own failure is its own test's to report; here only what it sent is read.
                        try { await (Task)method.Invoke(pins, row.Data)!; }
                        catch (Exception) { }
                    }
                }
            }
            finally
            {
                CosmosDeclaredFactPlanningTests.Sending.Value = null;
            }

            var target = CosmosEvalServiceTests.Target == RenderingTarget.Emulator ? CosmosServiceTarget.Emulator : CosmosServiceTarget.Azure;
            var sets = new Dictionary<string, string>(StringComparer.Ordinal);
            var asked = new HashSet<string>(StringComparer.Ordinal);
            var wrong = new List<string>();
            foreach (var (_, query, documents) in sent.Where(s => s.Query.Sql.Contains(" WHERE ", StringComparison.Ordinal)))
            {
                var key = string.Join("\n", documents);
                if (sets.TryGetValue(key, out var partition) == false)
                {
                    sets[key] = partition = "pins" + sets.Count;
                    await _service.WriteAsIs(partition, documents);
                }

                var parameters = string.Join(", ", query.Parameters.Select(p => $"{p.Name}={JsonSerializer.Serialize(p.Value)}"));
                if (asked.Add(partition + "|" + query.Sql + "|" + parameters) == false)
                    continue;

                var said = DocumentRequests.Answer(query, null, documents, target).Select(r => JsonNode.Parse(r)!["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
                var observed = (await _service.Ids(partition, query)).Order(StringComparer.Ordinal).ToList();
                TestContext.Current.TestOutputHelper?.WriteLine($"{query.Sql} | {parameters} over {partition}: [{string.Join(",", observed)}]");
                if (said.SequenceEqual(observed) == false)
                    wrong.Add($"{query.Sql} | {parameters} over {partition}: the service [{string.Join(",", observed)}], the evaluator [{string.Join(",", said)}]");
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{asked.Count} requests over {sets.Count} sets of documents; {wrong.Count} divergences");
            asked.Should().NotBeEmpty("the pins send a WHERE");
            wrong.Should().BeEmpty();
        }

        /// <summary>A database of its own, holding the pins' documents each set under a partition of its own, with the ids the pins give them.</summary>
        public sealed class Fixture : CosmosEvalServiceTests.Fixture
        {

            public Fixture() : base("pins") { }

            /// <summary>Writes documents under a partition, each with the id it holds.</summary>
            internal async Task WriteAsIs(string partition, IEnumerable<string> documents)
            {
                foreach (var json in documents)
                {
                    var node = JsonNode.Parse(json)!.AsObject();
                    node["pk"] = partition;
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));
                    using var response = (await Container!.UpsertItemStreamAsync(stream, new PartitionKey(partition))).EnsureSuccessStatusCode();
                }
            }

            /// <summary>The ids of the documents a request returns from a partition, sent as written with its parameters.</summary>
            internal async Task<List<string>> Ids(string partition, CosmosQuery query)
            {
                var ids = new List<string>();
                var definition = query.Parameters.Aggregate(new QueryDefinition(query.Sql), (q, p) => q.WithParameter(p.Name, p.Value));
                using var feed = Container!.GetItemQueryStreamIterator(definition, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(partition) });
                while (feed.HasMoreResults)
                {
                    using var response = (await feed.ReadNextAsync()).EnsureSuccessStatusCode();
                    using var json = await JsonDocument.ParseAsync(response.Content);
                    ids.AddRange(json.RootElement.GetProperty("Documents").EnumerateArray().Select(e => e.GetProperty("id").GetString()!));
                }

                return ids;
            }

        }

    }

}
