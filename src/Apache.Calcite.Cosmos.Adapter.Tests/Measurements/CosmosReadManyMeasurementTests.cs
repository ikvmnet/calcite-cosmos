using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What <c>ReadManyItemsAsync</c> answers: whole documents, one per id it finds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A batch point read is not a query, and nothing of a statement survives into it.</b>
    /// <c>CosmosQueryExecutor</c> answers a statement whose predicate pins a set of ids with it, which
    /// is cheaper than the query for a small set (<c>CosmosPointReadResidualMeasurementTests</c>). But
    /// it applies no projection, no predicate and no <c>DISTINCT</c>: what comes back is the documents,
    /// and whatever the statement said beyond the ids has to be done again in process. A
    /// <c>DISTINCT</c> it drops returns one row per document where the statement asked for one per
    /// value.
    /// </para>
    /// <para>
    /// Against whatever account the suite resolves — see <see cref="CosmosEmulator"/>. Measured on the
    /// emulator.
    /// </para>
    /// </remarks>
    public class CosmosReadManyMeasurementTests : IClassFixture<CosmosReadManyMeasurementTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown, driven through a fixture rather than static hooks.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public ValueTask DisposeAsync() { Cleanup(); return default; }

        }

        static readonly string DatabaseName = "calcite_cosmos_readmany_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>
        /// Two documents that agree on every property but the id, and a third that does not.
        /// </summary>
        static readonly string[] Documents =
        {
            """{"id":"1","pk":"a","category":"bikes"}""",
            """{"id":"2","pk":"a","category":"bikes"}""",
            """{"id":"3","pk":"a","category":"shoes"}""",
        };

        static CosmosClient? _client;
        static Database? _made;
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
                _made = database;

                try { await database.GetContainer("items").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                _container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("items", "/pk"), cancellationToken: cts.Token)).Container;

                foreach (var json in Documents)
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var response = await _container.CreateItemStreamAsync(stream, new PartitionKey("a"), cancellationToken: cts.Token);
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception e)
            {
                _unavailable = e.Message;
                _container = null;
            }
        }

        static void Cleanup()
        {
            // The database the class made, half made among them: none where it reached no service.
            try { _made?.DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            _client?.Dispose();
            _client = null;
        }

        /// <summary>
        /// Reads the ids through <c>ReadManyItemsStreamAsync</c>, the call the executor makes, and
        /// answers each document it returns as its id and the names of its properties.
        /// </summary>
        static async Task<string[]> ReadMany(params string[] ids)
        {
            if (_container is null)
                Assert.Skip("These need a service. " + _unavailable);

            using var response = await _container!.ReadManyItemsStreamAsync(ids.Select(id => (id, new PartitionKey("a"))).ToList());
            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(response.Content);

            return document.RootElement.GetProperty("Documents").EnumerateArray()
                .Select(e => e.GetProperty("id").GetString() + ":" + string.Join(",", e.EnumerateObject().Select(p => p.Name).Where(n => n.StartsWith('_') == false).OrderBy(n => n, StringComparer.Ordinal)))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Two documents equal in everything but their ids come back as two documents, each whole,
        /// the service's own properties included.
        /// </summary>
        [Fact]
        public async Task ReadManyAnswersOneWholeDocumentPerId()
        {
            (await ReadMany("1", "2")).Should().Equal("1:category,id,pk", "2:category,id,pk");
            (await ReadMany("1", "3", "missing")).Should().Equal(new[] { "1:category,id,pk", "3:category,id,pk" }, "an id that matches nothing answers nothing");
        }

        /// <summary>
        /// An id asked twice is answered once. The call deduplicates the documents it is asked for,
        /// which is a set of ids, and nothing about the values in them.
        /// </summary>
        /// <remarks>
        /// So an <c>IN</c> list that names an id twice reads it once, as the query it replaces would —
        /// and that is the only deduplication a batch point read does.
        /// </remarks>
        [Fact]
        public async Task ReadManyAnswersAnIdAskedTwiceOnce()
        {
            (await ReadMany("1", "1")).Should().Equal("1:category,id,pk");
        }

        /// <summary>
        /// On the emulator an id holding U+0000 or U+FDD0 is not read by itself: a point read of one holding U+0000 is a
        /// 500, of one holding U+FDD0 a 404, and of U+FDD0 <c>0</c>, an id of its own, the document whose id is U+0000,
        /// that being how the emulator stores it; <c>ReadMany</c> leaves out every id that holds either beside other
        /// characters. A query of the id sent in its stored spelling finds the document exactly. Azure reads such an id as
        /// written, and there the test skips. Measured on both images on 2026-10-31.
        /// </summary>
        [Fact]
        public async Task AnIdHoldingAnEscapeIsNotReadByItsId()
        {
            if (_container is null)
                Assert.Skip("These need a service. " + _unavailable);

            string[] ids = ["\0", "a\0", "﷐", "a﷐", "﷐0", "﷐1", "plain"];
            var database = _container!.Database;
            try { await database.GetContainer("escaped").DeleteContainerAsync(cancellationToken: TestContext.Current.CancellationToken); } catch (CosmosException) { }
            var escaped = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("escaped", "/pk"), cancellationToken: TestContext.Current.CancellationToken)).Container;
            foreach (var id in ids)
            {
                // Written raw, as the emulator probe wrote it, U+0000 alone escaped as JSON requires.
                var json = "{\"id\":\"" + string.Concat(id.Select(c => c < ' ' ? $"\\u{(int)c:x4}" : c.ToString())) + "\",\"pk\":\"a\"}";
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                using var created = await escaped.CreateItemStreamAsync(stream, new PartitionKey("a"), cancellationToken: TestContext.Current.CancellationToken);
                created.EnsureSuccessStatusCode();
            }

            async Task<(System.Net.HttpStatusCode Status, string? Id)> Read(string id)
            {
                using var read = await escaped.ReadItemStreamAsync(id, new PartitionKey("a"), cancellationToken: TestContext.Current.CancellationToken);
                if (read.IsSuccessStatusCode == false)
                    return (read.StatusCode, null);

                using var document = await JsonDocument.ParseAsync(read.Content, cancellationToken: TestContext.Current.CancellationToken);
                return (read.StatusCode, document.RootElement.GetProperty("id").GetString());
            }

            var nul = await Read("\0");
            if (nul == (System.Net.HttpStatusCode.OK, "\0"))
                Assert.Skip("This service reads an id holding U+0000 by itself, as Azure does; the emulator does not.");

            nul.Status.Should().Be(System.Net.HttpStatusCode.InternalServerError);
            (await Read("a\0")).Status.Should().Be(System.Net.HttpStatusCode.InternalServerError);
            (await Read("﷐")).Should().Be((System.Net.HttpStatusCode.NotFound, (string?)null));
            (await Read("a﷐")).Should().Be((System.Net.HttpStatusCode.NotFound, (string?)null));
            (await Read("﷐0")).Should().Be((System.Net.HttpStatusCode.OK, "\0"), "U+FDD0 0 is how U+0000 is stored, so it reads that document, not its own");
            (await Read("﷐1")).Should().Be((System.Net.HttpStatusCode.OK, "﷐"));
            (await Read("plain")).Should().Be((System.Net.HttpStatusCode.OK, "plain"));

            using (var many = await escaped.ReadManyItemsStreamAsync([.. ids.Select(id => (id, new PartitionKey("a")))], cancellationToken: TestContext.Current.CancellationToken))
            {
                many.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(many.Content, cancellationToken: TestContext.Current.CancellationToken);
                var back = document.RootElement.GetProperty("Documents").EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToList();
                back.Should().NotContain(["a\0", "a﷐"], "ReadMany leaves an id holding an escape beside other characters out");
                back.Should().Contain("plain");
                back.Count.Should().BeLessThan(ids.Length);
            }

            foreach (var id in ids)
            {
                var query = new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.id = @p").WithParameter("@p", EmulatorStoredText.Of(id));
                var found = new List<string>();
                using var iterator = escaped.GetItemQueryIterator<string>(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("a") });
                while (iterator.HasMoreResults)
                    found.AddRange(await iterator.ReadNextAsync(TestContext.Current.CancellationToken));

                found.Should().Equal([id], "the id sent as stored finds its own document");
            }
        }

    }

}
