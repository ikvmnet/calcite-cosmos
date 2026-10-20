using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Adapter.Tests.Sql;

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
    /// <b>And the operators the translator writes over a rendering are measured beside it</b>: the
    /// string functions, division, the comparisons across types and <c>NOT</c> over undefined. Each
    /// is a place where the service's answer over a value of the wrong type differs from SQL's over
    /// the value's rendering, and each is a row of <c>CosmosAxioms</c>.
    /// </para>
    /// <para>
    /// <b>A row's outcomes are held by <see cref="CosmosOutcomeHarness"/>,</b> which tells undefined from
    /// false by a <c>WHERE</c> and a <c>WHERE NOT</c>, neither of which keeps it, and by the projection: an
    /// object constructor omits a property whose value is undefined, so <c>SELECT c.id, F(c.v) AS r</c>
    /// answers <c>true</c>, <c>false</c> or nothing. What is no truth is read projected here.
    /// </para>
    /// <para>
    /// Against whatever account the suite resolves — see <see cref="CosmosEmulator"/>. Each outcome
    /// names the targets that ran it.
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

        /// <summary>Holds a row's outcomes against the service, over witnesses of their own.</summary>
        static Task Holds(CosmosAxiom axiom)
        {
            if (_container is null)
                Assert.Skip("These need a service. " + _unavailable);

            return CosmosOutcomeHarness.Holds(_container!, axiom);
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
        [Fact, CosmosPins("Service.IsPrimitive")]
        public async Task IsPrimitiveIsFalseOverAnAbsentPath()
        {
            await Holds(CosmosAxioms.Get("Service.IsPrimitive"));
            (await Answers("(IS_PRIMITIVE(c.v) ? c.v : null)")).Should().Be("string=\"bikes\" number=30 boolean=true null=null object=null array=null absent=null",
                "so the guarded accessor is null over an absent path, as JSON_VALUE is");
        }

        /// <summary>
        /// <c>IS_DEFINED</c> is false over an absent path and true over everything else, a null
        /// included.
        /// </summary>
        [Fact, CosmosPins("Service.IsDefined")]
        public async Task IsDefinedIsFalseOnlyOverAnAbsentPath()
        {
            await Holds(CosmosAxioms.Get("Service.IsDefined"));
        }

        /// <summary>
        /// <c>IS_NULL</c> is true of a null alone, and false over an absent path: absence is not null
        /// to the service, which is why SQL's null test names both.
        /// </summary>
        [Fact, CosmosPins("Service.IsNull")]
        public async Task IsNullIsTrueOnlyOfANull()
        {
            await Holds(CosmosAxioms.Get("Service.IsNull"));
        }

        /// <summary>
        /// Each test of a kind is true of that kind and false of everything else, an absent path
        /// included.
        /// </summary>
        [Fact, CosmosPins("Service.KindTests")]
        public async Task EachKindTestIsTrueOfItsKindAlone()
        {
            await Holds(CosmosAxioms.Get("Service.KindTests"));
        }

        /// <summary>
        /// So <c>NOT</c> over any of them is a real boolean too, over an absent path as over anything
        /// else: true wherever the test is false.
        /// </summary>
        /// <remarks>
        /// That is what <c>CosmosOperators.IsAbsenceObserving</c> claims of the family: an expression
        /// built from one keeps a document without the path, where a comparison over it would not.
        /// </remarks>
        [Fact, CosmosPins("Service.NotOfAKindTest")]
        public async Task NotOfATypeTestIsTrueOverAnAbsentPath()
        {
            await Holds(CosmosAxioms.Get("Service.NotOfAKindTest"));
        }

        /// <summary>
        /// The string functions are undefined over anything that is not a string: a number, a
        /// boolean, a null, a structure and nothing alike.
        /// </summary>
        /// <remarks>
        /// Calcite renders a number or a boolean as text before it folds or joins it, so
        /// <c>UPPER(JSON_VALUE(doc, '$.v'))</c> is <c>'30'</c> over a stored <c>30</c> and
        /// <c>'TRUE'</c> over a stored <c>true</c> (<c>CalciteJsonValueMeasurementTests</c>). The same
        /// function over the guarded accessor is undefined at the service there, so a case fold or a
        /// concatenation over a rendering does not mean at the service what it means in process
        /// unless the path is known to hold a string. <c>WriteLike</c> knows this; the projection
        /// and the comparison sides did not.
        /// </remarks>
        [Fact]
        public async Task TheStringFunctionsAreUndefinedOverAnythingButAString()
        {
            (await Answers("UPPER(c.v)")).Should().Be("string=\"BIKES\" number=undefined boolean=undefined null=undefined object=undefined array=undefined absent=undefined");
            (await Answers("LOWER(c.v)")).Should().Be("string=\"bikes\" number=undefined boolean=undefined null=undefined object=undefined array=undefined absent=undefined");
            (await Answers("CONCAT(c.v, \"!\")")).Should().Be("string=\"bikes!\" number=undefined boolean=undefined null=undefined object=undefined array=undefined absent=undefined");
            (await Answers("UPPER((IS_PRIMITIVE(c.v) ? c.v : null))")).Should().Be("string=\"BIKES\" number=undefined boolean=undefined null=undefined object=undefined array=undefined absent=undefined",
                "and the guard does not change it: it renders nothing");
        }

        /// <summary>
        /// <c>/</c> divides as floating point, integers included, and is undefined over anything that
        /// is not a number.
        /// </summary>
        /// <remarks>
        /// Calcite's <c>/</c> over two integers truncates (<c>CalciteArithmeticMeasurementTests</c>),
        /// so the operator is the same symbol and a different function wherever both operands are
        /// integers.
        /// </remarks>
        [Fact]
        public async Task DivisionIsFloatingPointAndUndefinedOverAnythingButANumber()
        {
            (await Answers("c.v / 4")).Should().Be("string=undefined number=7.5 boolean=undefined null=undefined object=undefined array=undefined absent=undefined");
            (await Answers("7 / 2")).Should().Be("string=3.5 number=3.5 boolean=3.5 null=3.5 object=3.5 array=3.5 absent=3.5");
            (await Answers("-7 / 2")).Should().Be("string=-3.5 number=-3.5 boolean=-3.5 null=-3.5 object=-3.5 array=-3.5 absent=-3.5");
        }

        /// <summary>
        /// An ordering comparison across types is undefined, as is one over an absent path.
        /// </summary>
        [Fact, CosmosPins("Service.OrderingAcrossTypesIsUndefined")]
        public async Task AnOrderingAcrossTypesIsUndefined()
        {
            await Holds(CosmosAxioms.Get("Service.OrderingAcrossTypesIsUndefined"));
        }

        /// <summary>
        /// An equality across types is false, not undefined; only over an absent path is it
        /// undefined. So <c>!=</c> is true over a value of another type and over a null — where SQL's
        /// <c>&lt;&gt;</c> over a null is unknown.
        /// </summary>
        /// <remarks>
        /// <c>CosmosBooleanLogicMeasurementTests.AComparisonIsUndefinedOnlyOverAnAbsentPath</c> says
        /// the same of a boolean in a filter; this says it of every type, read as a value. Which is
        /// why a pushed <c>&lt;&gt;</c> over a nullable path is guarded with <c>IS_DEFINED</c> and
        /// <c>NOT IS_NULL</c>, and why <c>NOT (p = x)</c> keeps a null at the service and not in
        /// process.
        /// </remarks>
        [Fact, CosmosPins("Service.EqualityAcrossTypesIsFalse"), CosmosPins("Service.InequalityOverNullIsTrue")]
        public async Task AnEqualityAcrossTypesIsFalseAndAnInequalityOverANullIsTrue()
        {
            await Holds(CosmosAxioms.Get("Service.EqualityAcrossTypesIsFalse"));
            await Holds(CosmosAxioms.Get("Service.InequalityOverNullIsTrue"));
        }

        /// <summary>
        /// <c>NOT</c> over undefined is undefined: over an absent path, over a comparison across
        /// types, and over a value that is not a boolean.
        /// </summary>
        /// <remarks>
        /// SQL's <c>NOT</c> over unknown is unknown too, so the two agree wherever the service's
        /// undefined stands where SQL's unknown does — and part company wherever the service answers
        /// false where SQL answers unknown, which is the equality across types and over a null above.
        /// </remarks>
        [Fact, CosmosPins("Service.NotOverUndefinedIsUndefined")]
        public async Task NotOverUndefinedIsUndefined()
        {
            await Holds(CosmosAxioms.Get("Service.NotOverUndefinedIsUndefined"));
        }

    }

}
