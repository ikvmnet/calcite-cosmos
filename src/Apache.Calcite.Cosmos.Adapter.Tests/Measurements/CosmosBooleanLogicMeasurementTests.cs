using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json.Linq;
using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the service's logic does with a path that holds a boolean, a null, something else or
    /// nothing — the behaviour a stored boolean is written over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rendering in <c>CosmosRexTranslator.TryStoredBoolean</c> is a claim about this</b>: that a
    /// path standing for a boolean cast may be written bare, and <c>NOT</c>, <c>AND</c> and
    /// <c>OR</c> over it, because the service treats a value that is not a boolean the way SQL treats
    /// unknown. And <c>WriteTruthTest</c> claims the two places where it does not — a comparison over an
    /// absent path being undefined rather than false — are covered by the <c>IS_DEFINED</c> it writes.
    /// Both are measured here rather than reasoned, one document per thing a path can hold.
    /// </para>
    /// <para>
    /// Against whatever account the suite resolves — see <see cref="CosmosEmulator"/>. The emulator
    /// is where this was first run; a named account is how a caller says to verify against the
    /// service itself.
    /// </para>
    /// </remarks>
    public class CosmosBooleanLogicMeasurementTests : IClassFixture<CosmosBooleanLogicMeasurementTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown, driven through a fixture rather than static hooks.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public ValueTask DisposeAsync() { Cleanup(); return default; }

        }

        static readonly string DatabaseName = "calcite_cosmos_boolean_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>
        /// One document per thing the path can hold, named for it.
        /// </summary>
        static readonly string[] Documents =
        {
            """{"id":"true","pk":"a","flag":true}""",
            """{"id":"false","pk":"a","flag":false}""",
            """{"id":"null","pk":"a","flag":null}""",
            """{"id":"absent","pk":"a"}""",
            """{"id":"string","pk":"a","flag":"true"}""",
            """{"id":"number","pk":"a","flag":1}""",
        };

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

                try { await database.GetContainer("flags").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                _container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("flags", "/pk"), cancellationToken: cts.Token)).Container;

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
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            _client?.Dispose();
            _client = null;
        }

        /// <summary>
        /// The ids of the documents a <c>WHERE</c> keeps.
        /// </summary>
        static async Task<string[]> Kept(string where)
        {
            if (_container is null)
                Assert.Skip("These need a service. " + _unavailable);

            var ids = new List<string>();

            using var iterator = _container!.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT c.id FROM c WHERE " + where).WithParameter("@t", true).WithParameter("@f", false));
            while (iterator.HasMoreResults)
                foreach (var row in await iterator.ReadNextAsync())
                    ids.Add((string)row["id"]!);

            ids.Sort(StringComparer.Ordinal);
            return ids.ToArray();
        }

        /// <summary>
        /// A path stands for itself as a whole condition: the service keeps the document holding
        /// <c>true</c> and nothing else — not a string that spells it, not a number.
        /// </summary>
        [Fact]
        public async Task ABarePathKeepsOnlyTrue()
        {
            (await Kept("c.flag")).Should().Equal("true");
        }

        /// <summary>
        /// <c>NOT</c> over the path keeps only <c>false</c>: over a null, an absent path and a value
        /// that is not a boolean it answers something a <c>WHERE</c> does not keep, which is what SQL's
        /// <c>NOT</c> answers over unknown.
        /// </summary>
        [Fact]
        public async Task NotOverAPathKeepsOnlyFalse()
        {
            (await Kept("NOT c.flag")).Should().Equal("false");
        }

        /// <summary>
        /// The connectives treat what is not a boolean as unknown: <c>OR true</c> keeps it,
        /// <c>AND false</c> under a <c>NOT</c> keeps it, and <c>OR false</c> under a <c>NOT</c> does not.
        /// </summary>
        [Fact]
        public async Task TheConnectivesTreatEverythingButABooleanAsUnknown()
        {
            (await Kept("(c.flag OR true)")).Should().Equal("absent", "false", "null", "number", "string", "true");
            (await Kept("NOT (c.flag AND false)")).Should().Equal("absent", "false", "null", "number", "string", "true");
            (await Kept("NOT (c.flag OR false)")).Should().Equal("false");
        }

        /// <summary>
        /// A comparison with a boolean is false over a null and over another type, and undefined over
        /// an absent path — so <c>NOT</c> above it keeps the first two and not the third.
        /// </summary>
        [Fact]
        public async Task AComparisonIsUndefinedOnlyOverAnAbsentPath()
        {
            (await Kept("(c.flag = @t)")).Should().Equal("true");
            (await Kept("NOT (c.flag = @t)")).Should().Equal("false", "null", "number", "string");
        }

        /// <summary>
        /// Which is why the negative truth tests name the absent path: with it, <c>IS NOT TRUE</c>'s
        /// rendering keeps everything but the stored <c>true</c>.
        /// </summary>
        [Fact]
        public async Task NamingTheAbsentPathKeepsIt()
        {
            (await Kept("(NOT IS_DEFINED(c.flag) OR (NOT (c.flag = @t)))")).Should().Equal("absent", "false", "null", "number", "string");
            (await Kept("NOT (IS_DEFINED(c.flag) AND (c.flag = @t))")).Should().Equal("absent", "false", "null", "number", "string");
        }

    }

}
