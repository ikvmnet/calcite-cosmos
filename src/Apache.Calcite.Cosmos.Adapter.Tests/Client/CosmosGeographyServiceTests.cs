using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Newtonsoft.Json.Linq;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// What the service does with the spatial statements this adapter emits, measured rather than read
    /// off the reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other emitted statement form in this adapter was executed against a real account before
    /// being believed, and for a while the geography forms were the exception. These close that.
    /// </para>
    /// <para>
    /// A real account, not the emulator: <c>COSMOS_TEST_ENDPOINT</c> and <c>COSMOS_TEST_KEY</c>, or the
    /// class reports inconclusive. The emulator has been found to accept statements the service refuses
    /// and refuse features it implements, which is exactly the kind of answer these ask for.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosGeographyServiceTests
    {

        const string Here = """{"type":"Point","coordinates":[-122.33,47.61]}""";
        const string Box = """{"type":"Polygon","coordinates":[[[-123.0,47.0],[-121.0,47.0],[-121.0,48.0],[-123.0,48.0],[-123.0,47.0]]]}""";
        const string DatabaseName = "calcite_geography_tests";

        static readonly string? Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT");
        static readonly string? Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY");

        static CosmosClient? _client;
        static Container? _geography;
        static Container? _geometry;
        static string? _unavailable;

        [ClassInitialize]
        public static async Task Initialize(TestContext context)
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

                _geography = await Prepare(database, "geography", GeospatialType.Geography, boundingBox: false);
                _geometry = await Prepare(database, "geometry", GeospatialType.Geometry, boundingBox: true);
            }
            catch (Exception e)
            {
                _unavailable = $"No Cosmos DB account reachable at {Endpoint}: {e.Message}";
            }
        }

        [ClassCleanup]
        public static async Task Cleanup()
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

        /// <summary>
        /// Creates a container reading its coordinates one way or the other, and fills it.
        /// </summary>
        /// <remarks>
        /// A geometry index wants a bounding box and a geography index refuses one, which is the service
        /// saying what this adapter says: the two readings are not one thing with a flag on it.
        /// </remarks>
        static async Task<Container> Prepare(Database database, string name, GeospatialType reading, bool boundingBox)
        {
            var properties = new ContainerProperties(name, "/pk") { GeospatialConfig = new GeospatialConfig(reading) };

            var spatial = new SpatialPath { Path = "/location/*" };
            foreach (var type in new[] { SpatialType.Point, SpatialType.LineString, SpatialType.Polygon, SpatialType.MultiPolygon })
                spatial.SpatialTypes.Add(type);

            if (boundingBox)
                spatial.BoundingBox = new BoundingBoxProperties { Xmin = -180, Ymin = -90, Xmax = 180, Ymax = 90 };

            properties.IndexingPolicy.SpatialIndexes.Add(spatial);

            var container = (await database.CreateContainerIfNotExistsAsync(properties)).Container;

            await container.UpsertItemAsync(new { id = "near", pk = "a", location = new { type = "Point", coordinates = new[] { -122.34, 47.62 } } }, new PartitionKey("a"));
            await container.UpsertItemAsync(new { id = "far", pk = "a", location = new { type = "Point", coordinates = new[] { -118.24, 34.05 } } }, new PartitionKey("a"));

            return container;
        }

        static async Task<IReadOnlyList<JObject>> Query(Container container, string sql)
        {
            var rows = new List<JObject>();

            using var iterator = container.GetItemQueryIterator<JObject>(new QueryDefinition(sql));
            while (iterator.HasMoreResults)
                foreach (var row in await iterator.ReadNextAsync())
                    rows.Add(row);

            return rows;
        }

        static Container Geography()
        {
            if (_unavailable is not null)
                Assert.Inconclusive(_unavailable);

            return _geography!;
        }

        /// <summary>
        /// The service orders by a distance, which is what a nearest-neighbour search needs.
        /// </summary>
        /// <remarks>
        /// It could not be assumed. An <c>ORDER BY</c> over a computed expression is refused —
        /// <c>ORDER BY ToString(c.x)</c> answers 400, error 2206, <i>"ORDER BY item expression could not
        /// be mapped to a document path"</i>, recorded in <c>DESIGN.md</c> — so spatial being admitted
        /// there is a special case rather than the general rule, and this is what says so.
        /// </remarks>
        [TestMethod]
        public async Task ADistanceOrdersAtTheService()
        {
            var ordered = await Query(Geography(), $"SELECT c.id FROM c ORDER BY ST_DISTANCE(c.location, {Here})");

            ordered.Should().HaveCount(2);
            ordered[0].Value<string>("id").Should().Be("near");
            ordered[1].Value<string>("id").Should().Be("far");
        }

        /// <summary>
        /// And under a filter and a page, which is the shape a pushed sort actually takes.
        /// </summary>
        [TestMethod]
        public async Task ADistanceOrdersUnderAFilterAndAPage()
        {
            var page = await Query(Geography(), $"SELECT c.id FROM c WHERE c.pk = 'a' ORDER BY ST_DISTANCE(c.location, {Here}) OFFSET 0 LIMIT 1");

            page.Should().ContainSingle().Which.Value<string>("id").Should().Be("near");
        }

        /// <summary>
        /// The forms the translator emits, executed.
        /// </summary>
        [TestMethod]
        public async Task TheEmittedFormsExecute()
        {
            var container = Geography();

            var distance = await Query(container, $"SELECT c.id, ST_DISTANCE(c.location, {Here}) AS d FROM c WHERE c.id = 'near'");
            distance.Should().ContainSingle().Which.Value<double>("d").Should().BeApproximately(1342, 50);

            // What ST_GEOG_DWITHIN becomes, Cosmos having no ST_DWITHIN.
            (await Query(container, $"SELECT c.id FROM c WHERE ST_DISTANCE(c.location, {Here}) <= 50000")).Should().ContainSingle();

            (await Query(container, $"SELECT c.id FROM c WHERE ST_WITHIN(c.location, {Box})")).Should().ContainSingle();
            (await Query(container, $"SELECT c.id FROM c WHERE ST_INTERSECTS(c.location, {Box})")).Should().ContainSingle();
            (await Query(container, "SELECT c.id FROM c WHERE ST_ISVALID(c.location)")).Should().HaveCount(2);
        }

        /// <summary>
        /// The geometry type is a member of the shape, spelled the way JTS spells it.
        /// </summary>
        /// <remarks>
        /// Which is what lets <c>ST_GEOG_GEOMETRYTYPE</c> push as <c>c.location.type</c> rather than as a
        /// function the service does not have.
        /// </remarks>
        [TestMethod]
        public async Task TheTypeMemberSpellsWhatJtsSpells()
        {
            var types = await Query(Geography(), "SELECT c.id, c.location.type AS t FROM c WHERE c.id = 'near'");

            types.Should().ContainSingle().Which.Value<string>("t").Should().Be("Point");
        }

        /// <summary>
        /// A container reading its coordinates as a plane answers the planar question, and says nothing
        /// about having done so.
        /// </summary>
        /// <remarks>
        /// This is the whole of the argument for refusing to push a geodesic call into such a container.
        /// The same statement over the same shapes answers 1342 metres one way and 0.0141 the other —
        /// the planar hypotenuse in degrees — with nothing in the response to tell them apart.
        /// </remarks>
        [TestMethod]
        public async Task APlanarContainerAnswersInCoordinateUnits()
        {
            if (_unavailable is not null)
                Assert.Inconclusive(_unavailable);

            var planar = await Query(_geometry!, $"SELECT c.id, ST_DISTANCE(c.location, {Here}) AS d FROM c WHERE c.id = 'near'");

            planar.Should().ContainSingle().Which.Value<double>("d").Should().BeApproximately(0.01414, 0.0001);
        }

    }

}
