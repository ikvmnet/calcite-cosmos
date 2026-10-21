using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json.Linq;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// The rendering check's reader held to the service, independently of the reader's author
    /// (<c>Service.NotBindsTightestThenAndThenOr</c>): over every document that sets <c>a</c>, <c>b</c> and <c>c</c> to
    /// nothing, a null or a string, the service's <c>WHERE</c> keeps for each pinned text what it keeps for the pinned tree
    /// written with every grouping explicit, which the other binding's tree does not; and <c>NOT</c> before a comparison
    /// negates the path, which the reader therefore reads as opaque.
    /// </summary>
    /// <remarks>Against whatever account the suite resolves (<see cref="CosmosEmulator"/>).</remarks>
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
        /// Each pinned text the reader reads keeps on the service what its tree written out keeps; where the plant's tree,
        /// <c>NOT</c> binding looser than <c>AND</c>, keeps otherwise, the service keeps what the grammar's does; and
        /// <c>NOT c.a = @p0</c> keeps what <c>(NOT c.a) = @p0</c> keeps, not what <c>NOT (c.a = @p0)</c> does.
        /// </summary>
        [Fact]
        public async Task TheServiceKeepsWhatThePinnedTreePredicts()
        {
            if (_container is null)
                Assert.Skip("This needs a service. " + _unavailable);

            var refuted = 0;
            foreach (var (text, tree) in CosmosSqlFormReaderTests.Pinned.Where(p => RenderingCheckRun.Opaque(p.Tree) == false))
            {
                var (kept, predicted) = (await Kept(text), await Kept(Sql(tree)));
                var planted = await Kept(Sql(CosmosSqlFormReader.Read(text, CosmosSqlFormReaderTests.Parameters, notOverAnd: true).Form));
                kept.Should().Equal(predicted, text);
                refuted += kept.SequenceEqual(planted) ? 0 : 1;
            }

            refuted.Should().BePositive("the documents part the two bindings");
            var (bare, path, comparison) = (await Kept("NOT c.a = @p0"), await Kept("(NOT c.a) = @p0"), await Kept("NOT (c.a = @p0)"));
            bare.Should().Equal(path).And.NotEqual(comparison);
        }

        /// <summary>A pinned tree written with every grouping explicit, which the service reads one way only.</summary>
        static string Sql(ServiceForm form) => form.Kind switch
        {
            ServiceFormKind.Not => $"NOT ({Sql(form.Members[0])})",
            ServiceFormKind.And or ServiceFormKind.Or => "(" + string.Join(form.Kind == ServiceFormKind.And ? " AND " : " OR ", form.Members.Select(Sql)) + ")",
            _ => form.Test switch
            {
                CosmosServiceTest.IsDefined => $"IS_DEFINED(c.{Name(form)})",
                CosmosServiceTest.IsNull => $"IS_NULL(c.{Name(form)})",
                CosmosServiceTest.NotEqual => $"(c.{Name(form)} != @p0)",
                _ => throw new ArgumentException("Only the pinned trees' leaves are written here.", nameof(form)),
            },
        };

        static string Name(ServiceForm form) => Paths.Single(p => SqlValue.Spell(JsonDocumentPath.Root.Property(p)) == form.Path);

        static async Task<string[]> Kept(string where)
        {
            var ids = new List<string>();
            var query = new QueryDefinition("SELECT c.id FROM c WHERE " + where);
            using var iterator = _container!.GetItemQueryIterator<JObject>(where.Contains("@p0") ? query.WithParameter("@p0", "x") : query);
            while (iterator.HasMoreResults)
                foreach (var row in await iterator.ReadNextAsync())
                    ids.Add((string)row["id"]!);

            return [.. ids.Order(StringComparer.Ordinal)];
        }

    }

}
