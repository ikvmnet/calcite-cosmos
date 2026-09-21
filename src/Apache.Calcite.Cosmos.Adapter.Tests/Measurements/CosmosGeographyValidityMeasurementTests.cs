using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Newtonsoft.Json.Linq;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the service does with a document whose geography is not one, and what that says about
    /// which orderings this adapter may push.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The question is whether a bad shape fails or vanishes</b>, and the two lead to different
    /// adapters. In process a bad shape <em>raises</em> — <c>GeographyFunctions.FromGeoJson</c> throws
    /// over anything it cannot parse, measured in <c>CalciteGeographyReadingMeasurementTests</c>. If
    /// the service raised too, the two would agree by both failing and there would be nothing to
    /// record. It does not.
    /// </para>
    /// <para>
    /// <b>What follows is the distinction this file exists for: a value null to Calcite and a value
    /// undefined to Cosmos are different sets.</b> A schema can say a property is an object, and an
    /// object that is not a shape still measures undefined — so a declaration about the <em>type</em>
    /// at a path never settles what the service will answer for a spatial function over it. That is
    /// the gap a null-placement proof would have to close and cannot, and writing it down is what
    /// keeps the proof from being attempted: a key provably non-null to Calcite is not a key the
    /// service will never leave undefined.
    /// </para>
    /// <para>
    /// A real account, not the emulator: <c>COSMOS_TEST_ENDPOINT</c> and <c>COSMOS_TEST_KEY</c>, or
    /// the class skips. The emulator has been found to accept statements the service refuses, which
    /// is exactly the kind of answer these ask for.
    /// </para>
    /// </remarks>
    public class CosmosGeographyValidityMeasurementTests : IClassFixture<CosmosGeographyValidityMeasurementTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown, driven through a fixture rather than static hooks.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public async ValueTask DisposeAsync() => await Cleanup();

        }

        const string Point = """{"type":"Point","coordinates":[-109.55,38.57]}""";
        const string DatabaseName = "calcite_geography_validity_tests";

        static readonly string? Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT");
        static readonly string? Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY");

        static CosmosClient? _client;
        static Container? _container;
        static string? _unavailable;

        /// <summary>
        /// One document per thing a path can hold, named for what it is.
        /// </summary>
        /// <remarks>
        /// <c>badcoord</c> is the one worth reading twice: structurally perfect GeoJSON whose
        /// coordinates are out of range. It is what puts a declared schema out of reach, since
        /// expressing it would need numeric bounds on array elements.
        /// </remarks>
        static readonly (string Id, object? Location)[] Documents =
        {
            ("valid", new { type = "Point", coordinates = new[] { -109.6, 38.6 } }),
            ("notgeo", new { kind = "somewhere" }),
            ("array", new[] { 1, 2 }),
            ("scalar", "moab"),
            ("nullval", null),
            ("badcoord", new { type = "Point", coordinates = new[] { 999.0, 999.0 } }),

            // The shapes that are GeoJSON and are still not measurable. Each one is a reason
            // CosmosGeographyForms refuses to prove something from a declaration.
            ("wrongcase", new { type = "point", coordinates = new[] { 1.0, 2.0 } }),
            ("lineone", new { type = "LineString", coordinates = new[] { new[] { 0.0, 0.0 } } }),
            ("multipoint", new { type = "MultiPoint", coordinates = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } } }),
            ("ringopen", new { type = "Polygon", coordinates = new[] { new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 0.0, 1.0 } } } }),
            ("ringcrossed", new { type = "Polygon", coordinates = new[] { new[] { new[] { 0.0, 0.0 }, new[] { 2.0, 2.0 }, new[] { 2.0, 0.0 }, new[] { 0.0, 2.0 }, new[] { 0.0, 0.0 } } } }),

            // And the ones that are, including a polygon wound either way and a point with an
            // elevation, so the refusals above are not read as a general suspicion of shapes.
            ("ringclosed", new { type = "Polygon", coordinates = new[] { new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 0.0, 1.0 }, new[] { 0.0, 0.0 } } } }),
            ("ringclockwise", new { type = "Polygon", coordinates = new[] { new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 1.0 }, new[] { 1.0, 1.0 }, new[] { 1.0, 0.0 }, new[] { 0.0, 0.0 } } } }),
            ("linetwo", new { type = "LineString", coordinates = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } } }),
            ("elevated", new { type = "Point", coordinates = new[] { 1.0, 2.0, 3.0 } }),
        };

        /// <summary>
        /// The documents the service will measure, by id.
        /// </summary>
        static readonly string[] Measurable = { "valid", "ringclosed", "ringclockwise", "linetwo", "elevated" };

        /// <summary>
        /// The documents it will not, by id — six that are not shapes at all, and five that are
        /// GeoJSON and still unmeasurable.
        /// </summary>
        static readonly string[] Unmeasurable =
        {
            "notgeo", "array", "scalar", "nullval", "absent", "badcoord",
            "wrongcase", "lineone", "multipoint", "ringopen", "ringcrossed",
        };

        static async Task Initialize()
        {
            if (string.IsNullOrEmpty(Endpoint) || string.IsNullOrEmpty(Key))
            {
                _unavailable = "COSMOS_TEST_ENDPOINT / COSMOS_TEST_KEY are not set.";
                return;
            }

            try
            {
                _client = new CosmosClient(Endpoint, Key);

                var database = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName)).Database;
                _container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("shapes", "/pk"))).Container;

                foreach (var (id, location) in Documents)
                    await _container.UpsertItemAsync(new { id, pk = "a", location }, new PartitionKey("a"));

                // The one with no property at all, which an anonymous type cannot express.
                await _container.UpsertItemAsync(new { id = "absent", pk = "a" }, new PartitionKey("a"));
            }
            catch (Exception e)
            {
                _unavailable = $"No Cosmos DB account reachable at {Endpoint}: {e.Message}";
            }
        }

        static async Task Cleanup()
        {
            if (_client is null)
                return;

            try
            {
                await _client.GetDatabase(DatabaseName).DeleteAsync();
            }
            catch (CosmosException)
            {
                // Nothing to undo: the database is the only thing created, and a failure to remove it
                // costs storage rather than correctness.
            }

            _client.Dispose();
        }

        static Container Shapes()
        {
            if (_unavailable is not null)
                Assert.Skip(_unavailable);

            return _container!;
        }

        static async Task<IReadOnlyList<JObject>> Query(string sql)
        {
            var rows = new List<JObject>();

            using var iterator = Shapes().GetItemQueryIterator<JObject>(new QueryDefinition(sql));
            while (iterator.HasMoreResults)
                foreach (var row in await iterator.ReadNextAsync())
                    rows.Add(row);

            return rows;
        }

        static async Task<CosmosException> Refused(string sql)
        {
            // Asked before the assertion, which would otherwise catch the skip as the failure it is
            // looking for and report a missing account as a refusal that was never made.
            Shapes();

            var act = async () => await Query(sql);

            return (await act.Should().ThrowAsync<CosmosException>()).Which;
        }

        /// <summary>
        /// A distance over anything that is not a shape is <em>undefined</em>, not an error.
        /// </summary>
        /// <remarks>
        /// The measurement the rest rests on. Six kinds of bad value, one answer: the property is
        /// absent from the row. So a pushed statement returns an answer where the in-process plan
        /// raises, which is a divergence the adapter carries today in its filter and its projection
        /// alike — and a reason never to read "the key cannot be null" as "the service will answer".
        /// </remarks>
        [Fact]
        public async Task ADistanceOverSomethingThatIsNotAShapeIsUndefined()
        {
            var rows = await Query($"SELECT c.id, IS_DEFINED(ST_DISTANCE(c.location, {Point})) AS defined FROM c");

            var defined = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var row in rows)
                defined[(string)row["id"]!] = (bool)row["defined"]!;

            foreach (var id in Measurable)
                defined[id].Should().BeTrue($"'{id}' is a shape the service measures");

            foreach (var id in Unmeasurable)
                defined[id].Should().BeFalse($"'{id}' is not one the service will measure");
        }

        /// <summary>
        /// <c>ST_ISVALID</c> answers for exactly the rows a distance is defined over.
        /// </summary>
        /// <remarks>
        /// Which makes it the condition that characterises them, and the reason a caller who wants a
        /// pushed proximity query over a container of mixed quality can write one.
        /// </remarks>
        [Fact]
        public async Task ValidityAgreesWithDefinedness()
        {
            var rows = await Query($"SELECT c.id, ST_ISVALID(c.location) AS valid, IS_DEFINED(ST_DISTANCE(c.location, {Point})) AS defined FROM c");

            rows.Should().HaveCount(Measurable.Length + Unmeasurable.Length);

            foreach (var row in rows)
                ((bool)row["valid"]!).Should().Be((bool)row["defined"]!, "for " + row["id"]);
        }

        /// <summary>
        /// Undefined sorts first ascending and last descending, which is nulls-low in both directions.
        /// </summary>
        /// <remarks>
        /// <b>The placement the adapter's whole sort gate turns on</b>, measured against an account
        /// rather than the emulator. Calcite's default is the opposite on both counts, which is why a
        /// connection sets <c>defaultNullCollation=LOW</c> and why a sort that does not is declined
        /// rather than returning rows in an order the plan did not ask for. See the README.
        /// </remarks>
        [Fact]
        public async Task UndefinedSortsFirstAscendingAndLastDescending()
        {
            var ascending = await Query($"SELECT c.id FROM c ORDER BY ST_DISTANCE(c.location, {Point}) ASC");
            var descending = await Query($"SELECT c.id FROM c ORDER BY ST_DISTANCE(c.location, {Point}) DESC");

            var lastAscending = new List<string>();
            for (var i = ascending.Count - Measurable.Length; i < ascending.Count; i++)
                lastAscending.Add((string)ascending[i]["id"]!);

            lastAscending.Should().BeEquivalentTo(Measurable, "the defined distances sort last ascending");

            var reversed = new List<string>();
            for (var i = descending.Count - 1; i >= 0; i--)
                reversed.Add((string)descending[i]["id"]!);

            var forward = new List<string>();
            foreach (var row in ascending)
                forward.Add((string)row["id"]!);

            forward.Should().Equal(reversed, "DESC is the exact reverse of ASC, there being one total order");
        }

        /// <summary>
        /// There is no rendering that repairs the placement: the service takes a spatial call in the
        /// clause and nothing built around one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why it matters is what it rules out.</b> Cosmos orders undefined first and offers no
        /// control, so the obvious repair is to order by something that maps the undefined to an end —
        /// <c>IIF</c> over a sentinel, or a definedness key ahead of the distance. Both are refused,
        /// so a plan wanting Calcite's default placement cannot be served by writing a cleverer
        /// clause. Either the placement matches or the sort stays in process.
        /// </para>
        /// <para>
        /// Error 2206 is the same refusal a computed <c>ORDER BY</c> draws generally; the distance is
        /// the exception, not the rule.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task NeitherAConditionalNorASecondKeyIsAcceptedInTheClause()
        {
            var conditional = await Refused($"SELECT c.id FROM c ORDER BY IIF(IS_DEFINED(ST_DISTANCE(c.location, {Point})), ST_DISTANCE(c.location, {Point}), \"~\") ASC");
            conditional.ResponseBody.Should().Contain("2206");

            var second = await Refused($"SELECT c.id FROM c ORDER BY IS_DEFINED(ST_DISTANCE(c.location, {Point})) DESC, ST_DISTANCE(c.location, {Point}) ASC");
            second.ResponseBody.Should().Contain("2206");

            // And the control: the distance alone is accepted, so the refusals are about what was
            // built around it rather than about the clause carrying a spatial call at all.
            (await Query($"SELECT c.id FROM c ORDER BY ST_DISTANCE(c.location, {Point}) ASC"))
                .Should().HaveCount(Measurable.Length + Unmeasurable.Length);
        }

        /// <summary>
        /// A filter on validity leaves exactly the rows a distance orders, which is how a caller
        /// writes a proximity query over a container that holds bad shapes.
        /// </summary>
        /// <remarks>
        /// Recorded because it is the sound alternative to inventing one: the adapter must not add
        /// this condition itself — it would drop rows the query defines, silently — but a caller who
        /// writes it has asked for those rows to be excluded, and then the ordering is over a set with
        /// no undefined keys in it.
        /// </remarks>
        [Fact]
        public async Task AValidityFilterLeavesOnlyWhatOrders()
        {
            var rows = await Query($"SELECT c.id FROM c WHERE ST_ISVALID(c.location) ORDER BY ST_DISTANCE(c.location, {Point}) ASC");

            var kept = new List<string>();
            foreach (var row in rows)
                kept.Add((string)row["id"]!);

            kept.Should().BeEquivalentTo(Measurable);
        }

    }

}
