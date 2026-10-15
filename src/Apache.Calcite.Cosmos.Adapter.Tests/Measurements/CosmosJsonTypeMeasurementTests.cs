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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the service answers about a path, and over it, for each JSON type it can hold and for
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The translator writes the <c>IS_*</c> family as guards, and a guard is only as right as what
    /// it answers over each type.</b> <c>IS_PRIMITIVE</c> is the line <c>JSON_VALUE</c> draws between a
    /// scalar and a structure (<c>CosmosRexTranslator.TryJsonValueProjection</c>); <c>IS_DEFINED</c> and
    /// <c>IS_NULL</c> are SQL's null test over a path; and
    /// <c>CosmosOperators.IsAbsenceObserving</c> claims the whole family answers a real boolean where
    /// a comparison answers undefined. That claim had been measured for <c>IS_DEFINED</c> alone. And
    /// the repository recorded two answers for <c>IS_PRIMITIVE</c> over an absent path — false in the
    /// translator, undefined in <c>DESIGN.md</c> — which is the question that settled it.
    /// </para>
    /// <para>
    /// <b>Read as a projection, so that undefined is visible.</b> A filter cannot tell undefined from
    /// false: neither keeps the document. An object constructor omits a property whose value is
    /// undefined, so <c>SELECT c.id, F(c.v) AS r</c> answers <c>true</c>, <c>false</c> or nothing, and
    /// nothing is undefined.
    /// </para>
    /// <para>
    /// Against whatever account the suite resolves — see <see cref="CosmosEmulator"/>. Measured on the
    /// emulator; Azure is owed, and <c>TODO.md</c> says so.
    /// </para>
    /// </remarks>
    public class CosmosJsonTypeMeasurementTests : IClassFixture<CosmosJsonTypeMeasurementTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown, driven through a fixture rather than static hooks.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public ValueTask DisposeAsync() { Cleanup(); return default; }

        }

        static readonly string DatabaseName = "calcite_cosmos_jsontype_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>
        /// One document per thing the path can hold, named for it.
        /// </summary>
        static readonly string[] Documents =
        {
            """{"id":"string","pk":"a","v":"bikes"}""",
            """{"id":"number","pk":"a","v":30}""",
            """{"id":"boolean","pk":"a","v":true}""",
            """{"id":"null","pk":"a","v":null}""",
            """{"id":"object","pk":"a","v":{"x":1}}""",
            """{"id":"array","pk":"a","v":[1]}""",
            """{"id":"absent","pk":"a"}""",
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

                try { await database.GetContainer("kinds").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                _container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("kinds", "/pk"), cancellationToken: cts.Token)).Container;

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
        /// What an expression over <c>c.v</c> answers for each document, written
        /// <c>id=answer</c> in the order of <see cref="Documents"/>: the value's JSON, or
        /// <c>undefined</c> where the projection omitted it.
        /// </summary>
        static async Task<string> Answers(string expression)
        {
            if (_container is null)
                Assert.Skip("These need a service. " + _unavailable);

            var answers = new Dictionary<string, string>(StringComparer.Ordinal);

            using var iterator = _container!.GetItemQueryIterator<JObject>(new QueryDefinition($"SELECT c.id, {expression} AS r FROM c"));
            while (iterator.HasMoreResults)
                foreach (var row in await iterator.ReadNextAsync())
                    answers[(string)row["id"]!] = row.TryGetValue("r", out var r) ? r.ToString(Formatting.None) : "undefined";

            return string.Join(" ", Documents.Select(json => (string)JObject.Parse(json)["id"]!).Select(id => $"{id}={(answers.TryGetValue(id, out var answer) ? answer : "missing")}"));
        }

        /// <summary>
        /// <c>IS_PRIMITIVE</c> is true of a string, a number, a boolean and a null, and false of an
        /// object, an array and an absent path — false, not undefined.
        /// </summary>
        /// <remarks>
        /// This is the row the translator and <c>DESIGN.md</c> disagreed about. The translator's
        /// reading was right, and the guarded accessor it writes,
        /// <c>(IS_PRIMITIVE(p) ? p : null)</c>, is therefore null over an absent path rather than
        /// undefined — which is what <c>JSON_VALUE</c> answers there, and why
        /// <c>IS_DEFINED</c> over the guard is true even where the path is not.
        /// </remarks>
        [Fact]
        public async Task IsPrimitiveIsFalseOverAnAbsentPath()
        {
            (await Answers("IS_PRIMITIVE(c.v)")).Should().Be("string=true number=true boolean=true null=true object=false array=false absent=false");
            (await Answers("(IS_PRIMITIVE(c.v) ? c.v : null)")).Should().Be("string=\"bikes\" number=30 boolean=true null=null object=null array=null absent=null",
                "so the guarded accessor is null over an absent path, as JSON_VALUE is");
        }

        /// <summary>
        /// <c>IS_DEFINED</c> is false over an absent path and true over everything else, a null
        /// included.
        /// </summary>
        [Fact]
        public async Task IsDefinedIsFalseOnlyOverAnAbsentPath()
        {
            (await Answers("IS_DEFINED(c.v)")).Should().Be("string=true number=true boolean=true null=true object=true array=true absent=false");
        }

        /// <summary>
        /// <c>IS_NULL</c> is true of a null alone, and false over an absent path: absence is not null
        /// to the service, which is why SQL's null test names both.
        /// </summary>
        [Fact]
        public async Task IsNullIsTrueOnlyOfANull()
        {
            (await Answers("IS_NULL(c.v)")).Should().Be("string=false number=false boolean=false null=true object=false array=false absent=false");
        }

        /// <summary>
        /// Each test of a kind is true of that kind and false of everything else, an absent path
        /// included.
        /// </summary>
        [Fact]
        public async Task EachKindTestIsTrueOfItsKindAlone()
        {
            (await Answers("IS_STRING(c.v)")).Should().Be("string=true number=false boolean=false null=false object=false array=false absent=false");
            (await Answers("IS_NUMBER(c.v)")).Should().Be("string=false number=true boolean=false null=false object=false array=false absent=false");
            (await Answers("IS_BOOL(c.v)")).Should().Be("string=false number=false boolean=true null=false object=false array=false absent=false");
            (await Answers("IS_OBJECT(c.v)")).Should().Be("string=false number=false boolean=false null=false object=true array=false absent=false");
            (await Answers("IS_ARRAY(c.v)")).Should().Be("string=false number=false boolean=false null=false object=false array=true absent=false");
        }

        /// <summary>
        /// So <c>NOT</c> over any of them is a real boolean too, over an absent path as over anything
        /// else: true wherever the test is false.
        /// </summary>
        /// <remarks>
        /// That is what <c>CosmosOperators.IsAbsenceObserving</c> claims of the family: an expression
        /// built from one keeps a document without the path, where a comparison over it would not.
        /// </remarks>
        [Fact]
        public async Task NotOfATypeTestIsTrueOverAnAbsentPath()
        {
            foreach (var test in new[] { "IS_PRIMITIVE", "IS_STRING", "IS_NUMBER", "IS_BOOL", "IS_NULL", "IS_OBJECT", "IS_ARRAY" })
                (await Answers($"NOT {test}(c.v)")).Should().EndWith("absent=true", $"NOT {test} over an absent path");

            (await Answers("NOT IS_DEFINED(c.v)")).Should().Be("string=false number=false boolean=false null=false object=false array=false absent=true");
        }

    }

}
