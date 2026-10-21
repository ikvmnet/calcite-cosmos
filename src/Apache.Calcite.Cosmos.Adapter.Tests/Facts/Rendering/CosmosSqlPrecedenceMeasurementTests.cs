using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json.Linq;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The reader's grammar held to the service, independently of the reader's author: for each pinned text, over every
    /// document that sets <c>a</c>, <c>b</c> and <c>c</c> to nothing, a null or a string, the service's <c>WHERE</c> keeps
    /// exactly what the pinned tree predicts, and so refutes the tree the other binding would give.
    /// </summary>
    /// <remarks>
    /// The documents make every test two-valued: <c>IS_DEFINED</c> and <c>IS_NULL</c> are never undefined, so a tree is
    /// evaluated as a boolean one. Against whatever account the suite resolves (<see cref="CosmosEmulator"/>).
    /// </remarks>
    public sealed class CosmosSqlPrecedenceMeasurementTests : IClassFixture<CosmosSqlPrecedenceMeasurementTests.Fixture>
    {

        /// <summary>The class's one-time setup and teardown.</summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public ValueTask DisposeAsync()
            {
                try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }
                _client?.Dispose();
                return default;
            }

        }

        static readonly string DatabaseName = "calcite_cosmos_precedence_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        static readonly string[] Paths = ["a", "b", "c"];

        /// <summary>Every assignment of nothing, a null or a string to each path: 27 documents, each named by its index.</summary>
        static readonly IReadOnlyList<JsonObject> Documents = [.. Enumerable.Range(0, 27).Select(n =>
        {
            var document = new JsonObject { ["id"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture), ["pk"] = "p" };
            for (var (i, rest) = (0, n); i < Paths.Length; i++, rest /= 3)
                if (rest % 3 > 0)
                    document[Paths[i]] = rest % 3 == 1 ? null : "s";

            return document;
        })];

        static CosmosClient? _client;
        static Container? _container;
        static string? _unavailable;

        static async Task Initialize()
        {
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 5 : 30),
                MaxRetryAttemptsOnRateLimitedRequests = 0,
            };

            if (CosmosEmulator.IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                if (CosmosEmulator.Endpoint is not string endpoint || CosmosEmulator.Key is not string key)
                {
                    _unavailable = "No account is reachable.";
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 10 : 120));
                _client = new CosmosClient(endpoint, key, options);
                var database = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;
                try { await database.GetContainer("precedence").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                _container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("precedence", "/pk"), cancellationToken: cts.Token)).Container;

                foreach (var document in Documents)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
                    using var response = await _container.CreateItemStreamAsync(stream, new PartitionKey("p"), cancellationToken: cts.Token);
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception e)
            {
                _unavailable = e.Message;
                _container = null;
            }
        }

        /// <summary>
        /// Each pinned text without a parameter keeps on the service what its tree predicts; and where the plant's tree,
        /// <c>NOT</c> binding looser than <c>AND</c>, predicts otherwise, the service keeps what the grammar's does.
        /// </summary>
        [Fact]
        public async Task TheServiceKeepsWhatThePinnedTreePredicts()
        {
            if (_container is null)
                Assert.Skip("This needs a service. " + _unavailable);

            var refuted = 0;
            foreach (var (text, tree) in CosmosSqlFormReaderTests.Pinned.Where(p => p.Text.Contains('@') == false))
            {
                var kept = await Kept(text);
                kept.Should().Equal(Predicted(tree), text);

                var planted = CosmosSqlFormReader.Read(text, [], notOverAnd: true).Form;
                refuted += kept.SequenceEqual(Predicted(planted)) ? 0 : 1;
            }

            refuted.Should().BePositive("the documents part the two bindings");
        }

        static string[] Predicted(ServiceForm tree) =>
            [.. Documents.Where(d => Holds(tree, d)).Select(d => (string)d["id"]!).Order(StringComparer.Ordinal)];

        /// <summary>A tree of <c>IS_DEFINED</c> and <c>IS_NULL</c> tests over one document, each two-valued there.</summary>
        static bool Holds(ServiceForm form, JsonObject document) => form.Kind switch
        {
            ServiceFormKind.Test => Paths.Single(p => SqlValue.Spell(JsonDocumentPath.Root.Property(p)) == form.Path) is var path
                && (form.Test == CosmosServiceTest.IsDefined ? document.ContainsKey(path) : document.TryGetPropertyValue(path, out var value) && value is null),
            ServiceFormKind.Not => Holds(form.Members[0], document) == false,
            ServiceFormKind.And => form.Members.All(m => Holds(m, document)),
            ServiceFormKind.Or => form.Members.Any(m => Holds(m, document)),
            _ => throw new ArgumentException("Only the tests and connectives are two-valued here.", nameof(form)),
        };

        static async Task<string[]> Kept(string where)
        {
            var ids = new List<string>();
            using var iterator = _container!.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT c.id FROM c WHERE " + where));
            while (iterator.HasMoreResults)
                foreach (var row in await iterator.ReadNextAsync())
                    ids.Add((string)row["id"]!);

            return [.. ids.Order(StringComparer.Ordinal)];
        }

    }

}
