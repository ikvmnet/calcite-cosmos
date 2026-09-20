using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using Azure.Identity;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// Measures the trade issue #92 reopens: when a predicate pins a complete <c>id</c> and partition
    /// key but carries a residual conjunct, is the query the extractor falls back to cheaper than a
    /// point read followed by an in-process filter?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CosmosPartitionKeyExtractor.TryExtractPointRead</c> declines whenever <c>CoversExactly</c>
    /// finds a conjunct that is not one of the pinned equalities, so
    /// <c>id = 'X' AND k = 'Y' AND deleteUtcTime IS NULL</c> — every by-id lookup through a
    /// soft-delete view — answers through the query engine for what is at most one document. The
    /// question is whether that default is the expensive one.
    /// </para>
    /// <para>
    /// The single-document legs turn on the per-query floor: a point read is charged as a read of one
    /// document, and a query pays the engine's floor on top of the same read. The set leg is the one
    /// genuinely in doubt — <c>ReadManyItemsAsync</c> is charged as its constituent point reads, so a
    /// large id set pays the read floor N times against a single query's one, and the two must cross
    /// somewhere.
    /// </para>
    /// <para>
    /// These tests need a real account and report inconclusive without one: the emulator reports a
    /// flat request charge and settles nothing. <c>COSMOS_TEST_ENDPOINT</c> names it;
    /// <c>COSMOS_TEST_KEY</c> is optional, and an account with local auth disabled is reached with
    /// <c>DefaultAzureCredential</c>, which needs the Cosmos DB Built-in Data Contributor role on
    /// the signed-in principal. The container they read is provisioned outside the fixture, because
    /// an Entra token cannot create one; they touch nothing else on the account. No throughput is
    /// named, so they run against a serverless account — which also
    /// means a single physical partition, and the cross-partition fan-out leg is left to the
    /// argument that a fan-out cannot cost less than the single-partition query measured here.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosPointReadResidualMeasurementTests
    {

        static readonly string? Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT");
        static readonly string? Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY");

        /// <summary>
        /// Fixed, because the database is provisioned on the control plane rather than by this
        /// fixture: an Entra token cannot create one, so the name has to be one a human can type.
        /// </summary>
        const string DatabaseName = "calcite_cosmos_pointread";

        const string ContainerName = "pointread";

        /// <summary>The partition every set measurement reads from, so a pinned query is single-partition.</summary>
        const string Partition = "k000";

        /// <summary>Documents seeded into <see cref="Partition"/>; every other one is soft-deleted.</summary>
        const int DocumentCount = 128;

        /// <summary>The partition holding the large documents, which price a body against the floor.</summary>
        const string LargePartition = "kbig";

        /// <summary>The partition holding the size ladder.</summary>
        const string SizePartition = "ksize";

        /// <summary>Body sizes in kilobytes, spanning the break-even the two-point fit predicts.</summary>
        static readonly int[] SizeLadder = { 1, 4, 8, 16, 24, 32, 48, 64 };

        static string SizeId(int kilobytes) => "size-" + kilobytes.ToString(CultureInfo.InvariantCulture);

        static CosmosClient? _client;
        static Container? _container;
        static string? _initializationFailure;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
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

                // An account with local auth disabled has no key to give, so a missing one selects
                // Entra rather than failing — the same credential the adapter itself builds.
                var client = string.IsNullOrEmpty(Key)
                    ? new CosmosClient(Endpoint, new DefaultAzureCredential(), options)
                    : new CosmosClient(Endpoint, Key, options);

                // Creating a database or container is a control plane operation, and Cosmos refuses
                // it over an Entra token however the principal is assigned — 403/5300, "cannot be
                // authorized by AAD token in data plane". So the container is provisioned outside
                // this fixture and only read here; seeding items is a data plane operation and is
                // allowed. To create it:
                //
                //   az cosmosdb sql database create --account-name <account> --resource-group <rg> \
                //       --name calcite_cosmos_pointread
                //   az cosmosdb sql container create --account-name <account> --resource-group <rg> \
                //       --database-name calcite_cosmos_pointread --name pointread --partition-key-path "/k"
                var container = client.GetContainer(DatabaseName, ContainerName);
                await container.ReadContainerAsync(cancellationToken: cts.Token);

                // Each task owns its stream: a using in the loop would dispose it while the request
                // is still in flight.
                async Task Seed(string key, string json)
                {
                    using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var response = await container.CreateItemStreamAsync(stream, new PartitionKey(key), cancellationToken: cts.Token);

                    // A conflict is a rerun against a surviving database, and the document is there.
                    if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
                        response.EnsureSuccessStatusCode();
                }

                var seed = new List<Task>();

                for (var i = 0; i < DocumentCount; i++)
                {
                    // Every other document is soft-deleted, so the residual both accepts and rejects
                    // over the same container and a set read spans the two.
                    var deleted = i % 2 == 1 ? "\"2026-01-01T00:00:00Z\"" : "null";

                    seed.Add(Seed(Partition, "{\"id\":\"" + D(i) + "\",\"k\":\"" + Partition + "\",\"v\":" + i + ",\"deleteUtcTime\":" + deleted + "}"));

                    if (seed.Count == 50)
                    {
                        await Task.WhenAll(seed);
                        seed.Clear();
                    }
                }

                await Task.WhenAll(seed);

                // A body far over the 1 KB a point read is charged at, so the read pays for the
                // document where a rejecting query pays the floor and returns nothing.
                var padding = new string('x', 100 * 1024);
                await Seed(LargePartition, "{\"id\":\"big-live\",\"k\":\"" + LargePartition + "\",\"deleteUtcTime\":null,\"pad\":\"" + padding + "\"}");
                await Seed(LargePartition, "{\"id\":\"big-dead\",\"k\":\"" + LargePartition + "\",\"deleteUtcTime\":\"2026-01-01T00:00:00Z\",\"pad\":\"" + padding + "\"}");

                // A ladder of sizes, so the break-even rests on a fitted line rather than on the two
                // points at either end of it.
                foreach (var kilobytes in SizeLadder)
                    await Seed(SizePartition,
                        "{\"id\":\"" + SizeId(kilobytes) + "\",\"k\":\"" + SizePartition + "\",\"deleteUtcTime\":null,\"pad\":\"" +
                        new string('x', kilobytes * 1024) + "\"}");

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

        [ClassCleanup]
        public static void ClassCleanup()
        {
            // The database is not dropped here: deleting one is the same control plane operation an
            // Entra token cannot perform. It is torn down the way it was raised, outside the fixture.
            _client?.Dispose();
            _client = null;
            _container = null;
        }

        static string D(int i) => $"d{i:D3}";

        static Container Container()
        {
            if (_container is null)
                Assert.Inconclusive("This measurement needs a real account. " + (_initializationFailure ?? "The fixture did not run."));

            return _container!;
        }

        /// <summary>
        /// Reports a line of the measurement. The test platform swallows the console output of a
        /// passing test, so <c>COSMOS_MEASUREMENT_LOG</c> names a file to append the tables to —
        /// which is how the numbers in the remarks above were read off.
        /// </summary>
        static void Say(string line)
        {
            Console.WriteLine(line);

            var log = Environment.GetEnvironmentVariable("COSMOS_MEASUREMENT_LOG");
            if (string.IsNullOrEmpty(log) == false)
                System.IO.File.AppendAllText(log, line + Environment.NewLine);
        }

        /// <summary>How many times each form is charged before its mean is taken.</summary>
        const int Runs = 5;

        /// <summary>
        /// Charges an operation once to warm the connection, then takes the mean of <see cref="Runs"/>.
        /// </summary>
        static async Task<double> Mean(Func<Task<double>> operation)
        {
            await operation();

            double total = 0;
            for (var i = 0; i < Runs; i++)
                total += await operation();

            return total / Runs;
        }

        /// <summary>Charges a single point read, which applies no predicate of its own.</summary>
        static async Task<double> PointRead(string id, string key)
        {
            using var response = await Container().ReadItemStreamAsync(id, new PartitionKey(key));
            response.EnsureSuccessStatusCode();
            return response.Headers.RequestCharge;
        }

        /// <summary>Charges a batch read, which the service prices as its constituent point reads.</summary>
        static async Task<double> ReadMany(IReadOnlyList<string> ids, string key)
        {
            var items = new List<(string, PartitionKey)>(ids.Count);
            foreach (var id in ids)
                items.Add((id, new PartitionKey(key)));

            using var response = await Container().ReadManyItemsStreamAsync(items);
            response.EnsureSuccessStatusCode();
            return response.Headers.RequestCharge;
        }

        /// <summary>Runs a statement to exhaustion and totals its charge and returned documents.</summary>
        static async Task<(double Charge, int Documents)> Query(QueryDefinition query, string? partitionKey)
        {
            var options = new QueryRequestOptions();
            if (partitionKey is not null)
                options.PartitionKey = new PartitionKey(partitionKey);

            using var iterator = Container().GetItemQueryStreamIterator(query, requestOptions: options);

            double charge = 0;
            var documents = 0;

            while (iterator.HasMoreResults)
            {
                using var response = await iterator.ReadNextAsync();
                response.EnsureSuccessStatusCode();

                charge += response.Headers.RequestCharge;

                using var document = await System.Text.Json.JsonDocument.ParseAsync(response.Content);
                documents += document.RootElement.GetProperty("Documents").GetArrayLength();
            }

            return (charge, documents);
        }

        /// <summary>The statement the extractor falls back to today: the pinned equalities, plus the residual.</summary>
        static QueryDefinition LookupQuery(string id, string key) =>
            new QueryDefinition("SELECT * FROM c WHERE c.id = @id AND c.k = @k AND IS_NULL(c.deleteUtcTime)")
                .WithParameter("@id", id)
                .WithParameter("@k", key);

        /// <summary>The same statement without the residual — the query a point read may replace today.</summary>
        static QueryDefinition BareQuery(string id, string key) =>
            new QueryDefinition("SELECT * FROM c WHERE c.id = @id AND c.k = @k")
                .WithParameter("@id", id)
                .WithParameter("@k", key);

        /// <summary>The statement a declined <c>TryExtractPointReadSet</c> falls back to.</summary>
        static QueryDefinition SetQuery(IReadOnlyList<string> ids, string key)
        {
            var parameters = new List<string>(ids.Count);
            for (var i = 0; i < ids.Count; i++)
                parameters.Add("@i" + i.ToString(CultureInfo.InvariantCulture));

            var query = new QueryDefinition(
                $"SELECT * FROM c WHERE c.k = @k AND c.id IN ({string.Join(", ", parameters)}) AND IS_NULL(c.deleteUtcTime)")
                .WithParameter("@k", key);

            for (var i = 0; i < ids.Count; i++)
                query = query.WithParameter(parameters[i], ids[i]);

            return query;
        }

        /// <summary>
        /// The single-document leg: a point read plus an in-process residual against the query the
        /// residual currently forces, for a residual that accepts and one that rejects.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured against a serverless account, ~200 byte documents: the point read cost 1.00 RU
        /// whether the residual kept the document or discarded it; the query carrying the residual
        /// cost 3.02 RU when it returned the document and 2.99 RU when it returned nothing; and the
        /// same query stripped of the residual cost 2.92 RU. So the residual itself is worth about
        /// 0.1 RU — effectively free — and all 2 RU of the difference is the engine floor the query
        /// pays and the read does not. Pinning the partition key changed nothing on a
        /// single-partition container (3.02 RU either way), because the router already prunes an
        /// equality over it.
        /// </para>
        /// <para>
        /// What is asserted is the relations rather than the charges, which the service may reprice.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task AReadThenFilterCostsLessThanTheQueryTheResidualForces()
        {
            var live = D(0);
            var dead = D(1);

            var readLive = await Mean(() => PointRead(live, Partition));
            var readDead = await Mean(() => PointRead(dead, Partition));

            var lookupLive = await Mean(async () => (await Query(LookupQuery(live, Partition), Partition)).Charge);
            var lookupDead = await Mean(async () => (await Query(LookupQuery(dead, Partition), Partition)).Charge);

            // Unpinned, to price the case where the planner does not pin the partition key.
            var openLive = await Mean(async () => (await Query(LookupQuery(live, Partition), null)).Charge);

            // Without the residual — the query a point read is already allowed to replace, which
            // separates the engine's floor from anything the residual itself costs.
            var bareLive = await Mean(async () => (await Query(BareQuery(live, Partition), Partition)).Charge);

            // The two paths must agree on the answer, which is the bar the trade is made against.
            (await Query(LookupQuery(live, Partition), Partition)).Documents.Should().Be(1);
            (await Query(LookupQuery(dead, Partition), Partition)).Documents.Should().Be(0);

            Say("## Single document (id + partition key pinned, residual IS_NULL(deleteUtcTime))");
            Say($"point read, residual accepts : {readLive:F2} RU");
            Say($"point read, residual rejects : {readDead:F2} RU  (the document is read, then discarded)");
            Say($"query + residual, pinned     : {lookupLive:F2} RU (accepts) / {lookupDead:F2} RU (rejects)");
            Say($"query + residual, unpinned   : {openLive:F2} RU");
            Say($"query, no residual, pinned   : {bareLive:F2} RU (the engine floor alone)");
            Say($"saving, accepting case       : {lookupLive - readLive:F2} RU ({(lookupLive / readLive):F2}x)");
            Say($"saving, rejecting case       : {lookupDead - readDead:F2} RU ({(lookupDead / readDead):F2}x)");

            readLive.Should().BeLessThan(lookupLive, "a point read pays for the document without the engine floor the query adds");
            readDead.Should().BeLessThan(lookupDead, "even a read whose residual discards the document beats the query it replaces");
            openLive.Should().BeGreaterThanOrEqualTo(lookupLive * 0.9, "an unpinned query cannot be cheaper than the pinned one");
        }

        /// <summary>
        /// The set leg, which is the one in doubt: <c>ReadManyItemsAsync</c> is charged per document
        /// and the single query is not, so the two cross somewhere as the id set grows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured against a serverless account, one partition, ~200 byte documents (ReadMany vs.
        /// query, RU): 1 → 1.00 / 3.02; 2 → 3.14 / 3.12; 4 → 3.59 / 3.35; 8 → 4.48 / 3.81;
        /// 16 → 6.27 / 4.72; 32 → 9.89 / 6.71; 64 → 18.35 / 6.74; 128 → 36.95 / 6.79. The batch read
        /// grows at roughly 0.29 RU per document over a floor it pays as soon as it stops being a
        /// single point read — 1.00 RU at one id, 3.14 at two — while the query flattens near 6.8 RU.
        /// By 128 ids the query is 5.4x cheaper.
        /// </para>
        /// <para>
        /// So <c>N = 1</c> is the only size at which the batch read wins, and #92's argument does not
        /// carry from the single document to the set: the per-document charge it dismisses is the
        /// whole cost at scale. Part of the gap is that read-then-filter must transport every
        /// document the residual will discard — here half of them — where the query never returns
        /// them. Relaxing <c>CoversExactly</c> for <c>TryExtractPointReadSet</c> on the strength of
        /// the single-document result would make the batch path worse at every size but one.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task TheBatchReadAndTheSetQueryCrossOverAsTheIdSetGrows()
        {
            var sizes = new[] { 1, 2, 4, 8, 16, 32, 64, 128 };

            Say("");
            Say("## Id set in one partition (partition key pinned, residual IS_NULL(deleteUtcTime))");
            Say("     N   ReadMany     query   ratio");

            int? crossover = null;
            var readings = new List<(int N, double Many, double Query)>();

            foreach (var n in sizes)
            {
                var ids = new List<string>(n);
                for (var i = 0; i < n; i++)
                    ids.Add(D(i));

                var many = await Mean(() => ReadMany(ids, Partition));
                var query = await Mean(async () => (await Query(SetQuery(ids, Partition), Partition)).Charge);

                readings.Add((n, many, query));
                Say($"{n,6}   {many,8:F2}  {query,8:F2}   {(query / many),5:F2}");

                if (crossover is null && many > query)
                    crossover = n;
            }

            // Both forms have to be answering the same question before their prices mean anything:
            // the batch read returns every id, and the residual then drops the soft-deleted half.
            var all = new List<string>(DocumentCount);
            for (var i = 0; i < DocumentCount; i++)
                all.Add(D(i));

            (await Query(SetQuery(all, Partition), Partition)).Documents.Should().Be(DocumentCount / 2,
                "the set query returns exactly the live half, which is what read-then-filter must reproduce");

            Say(crossover is null
                ? $"No crossover up to N={sizes[sizes.Length - 1]}: the batch read is cheaper at every size measured."
                : $"Crossover at N={crossover}: at and above it the single query is cheaper than the batch read.");

            readings[0].Many.Should().BeLessThan(readings[0].Query, "one id is the single-document case, where the read wins");
        }

        /// <summary>
        /// Sweeps document size, which is what the whole gate turns on, and reads the crossing off a
        /// line rather than off two points.
        /// </summary>
        [TestMethod]
        public async Task TheCrossingIsWhereTheTwoPointFitSaysItIs()
        {
            Say("");
            Say("## Point read against query, by document size");
            Say("   KB   read    query   cheaper");

            double? crossing = null;
            var previous = (Kilobytes: 0, Read: 0d, QueryCharge: 0d);

            foreach (var kilobytes in SizeLadder)
            {
                var id = SizeId(kilobytes);

                var read = await Mean(() => PointRead(id, SizePartition));
                var query = await Mean(async () => (await Query(LookupQuery(id, SizePartition), SizePartition)).Charge);

                Say($"{kilobytes,5}  {read,6:F2}  {query,6:F2}   {(read < query ? "read" : "query")}");

                // Linear interpolation between the last size where the read won and the first where it
                // lost, which is a better reading of the crossing than either endpoint.
                if (crossing is null && read > query && previous.Kilobytes > 0)
                {
                    var before = previous.QueryCharge - previous.Read;
                    var after = query - read;
                    crossing = previous.Kilobytes + (kilobytes - previous.Kilobytes) * (before / (before - after));
                }

                previous = (kilobytes, read, query);
            }

            Say(crossing is double point
                ? $"crossing interpolated at {point:F1} KB; the model says {CosmosRequestUnitModel.BreakEvenDocumentSizeInBytes / 1024d:F1} KB"
                : "no crossing inside the ladder");

            crossing.Should().NotBeNull("the ladder should span the crossing");
            crossing!.Value.Should().BeApproximately(CosmosRequestUnitModel.BreakEvenDocumentSizeInBytes / 1024d, 8d,
                "the model's slope is chosen so its crossing lands on the measured one");
        }

        /// <summary>A statement returning one field rather than the document.</summary>
        static QueryDefinition NarrowQuery(string id, string key) =>
            new QueryDefinition("SELECT c.id FROM c WHERE c.id = @id AND c.k = @k AND IS_NULL(c.deleteUtcTime)")
                .WithParameter("@id", id)
                .WithParameter("@k", key);

        /// <summary>
        /// Prices a projection, which the model does not carry and which moves the break-even if it
        /// matters: a query can return one field where a point read always returns the document whole.
        /// </summary>
        [TestMethod]
        public async Task AProjectionCostsTheQueryLessAndTheReadNothing()
        {
            var smallWide = await Mean(async () => (await Query(LookupQuery(D(0), Partition), Partition)).Charge);
            var smallNarrow = await Mean(async () => (await Query(NarrowQuery(D(0), Partition), Partition)).Charge);

            var largeWide = await Mean(async () => (await Query(LookupQuery("big-live", LargePartition), LargePartition)).Charge);
            var largeNarrow = await Mean(async () => (await Query(NarrowQuery("big-live", LargePartition), LargePartition)).Charge);

            // The read is the control: it applies no projection, so its charge cannot move.
            var read = await Mean(() => PointRead("big-live", LargePartition));

            Say("");
            Say("## Projection width (SELECT * against SELECT c.id)");
            Say($"small document, SELECT *     : {smallWide:F2} RU");
            Say($"small document, SELECT c.id  : {smallNarrow:F2} RU");
            Say($"large document, SELECT *     : {largeWide:F2} RU");
            Say($"large document, SELECT c.id  : {largeNarrow:F2} RU");
            Say($"large document, point read   : {read:F2} RU (no projection to apply)");
            Say($"what the projection saves    : {largeWide - largeNarrow:F2} RU on a large document, " +
                $"{smallWide - smallNarrow:F2} on a small one");

            // Whatever the numbers, a projection cannot make a query more expensive than returning the
            // whole document, and cannot change the read at all.
            smallNarrow.Should().BeLessThanOrEqualTo(smallWide * 1.1);
            largeNarrow.Should().BeLessThanOrEqualTo(largeWide * 1.1);
        }

        /// <summary>
        /// Times the two routes end to end, so the choice rests on something measured rather than on
        /// request units alone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Request units are what Cosmos bills and throttles on, so they are the objective a planner
        /// should minimise. Latency is the other half of "better", and if read-then-filter were cheaper
        /// but slower the trade would be a real one rather than free. It is not: both routes are a single
        /// round trip, and the read is the shorter one.
        /// </para>
        /// <para>
        /// Measured, median of thirty alternated round trips against a serverless account a continent
        /// away, over two runs: point read 67.5 and 67.6 ms, query 68.7 and 69.9 ms — the read ahead by
        /// 1.2 and 2.3 ms. The spread between runs is larger than the effect, so the honest reading is
        /// that the read is not slower rather than that it is faster by any particular amount. Both
        /// figures are dominated by the round trip, which is the useful part: the mechanism difference is
        /// small against the network, and the in-process residual over one row costs nothing measurable.
        /// </para>
        /// <para>
        /// What this settles for the planner is narrow but useful. There is no latency penalty to weigh
        /// against the request units saved, so ordering the routes by request units does not trade one
        /// resource for another — which is what would have made a conversion constant necessary.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task TheCheaperRouteIsAlsoTheFasterOne()
        {
            const int Rounds = 30;

            static double Median(List<double> values)
            {
                values.Sort();
                return values[values.Count / 2];
            }

            // Warm the connection and the routing caches, so the first request's handshake is not
            // attributed to whichever form happens to run first.
            await PointRead(D(0), Partition);
            await Query(LookupQuery(D(0), Partition), Partition);

            var read = new List<double>(Rounds);
            var query = new List<double>(Rounds);

            for (var i = 0; i < Rounds; i++)
            {
                // Alternated rather than run in blocks, so a drift in the service's latency lands on
                // both forms rather than on one.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await PointRead(D(0), Partition);
                read.Add(clock.Elapsed.TotalMilliseconds);

                clock.Restart();
                await Query(LookupQuery(D(0), Partition), Partition);
                query.Add(clock.Elapsed.TotalMilliseconds);
            }

            var readMedian = Median(read);
            var queryMedian = Median(query);

            Say("");
            Say("## Latency, single small document (median of " + Rounds + ")");
            Say($"point read                   : {readMedian:F1} ms");
            Say($"query + residual, pinned     : {queryMedian:F1} ms");
            Say($"difference                   : {queryMedian - readMedian:F1} ms");

            // The relation, not the milliseconds, which depend on the distance to the account: the
            // cheaper route must not be the slower one, because that is the trade that would make
            // ordering by request units a choice between resources rather than a free win.
            readMedian.Should().BeLessThan(queryMedian * 1.25,
                "a point read is one round trip and so is the query; the read must not be materially slower");
        }

        /// <summary>
        /// The regime that reverses the single-document trade: a body large enough that reading it
        /// costs more than the floor the query pays.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured against a serverless account, ~100 KB body: the point read cost 9.95 RU whether
        /// or not the residual kept the document, against 4.51 RU for the query that returns it and
        /// 2.99 RU for the query whose residual rejects it — the read is 3.3x the query in the
        /// rejecting case. A point read is charged for the body it returns and has no floor to save
        /// once the body dominates, so the 2 RU the single-document leg saves is a constant that a
        /// large enough document swamps.
        /// </para>
        /// <para>
        /// This is the answer to the regime question #92 raises: there is one, it is document size,
        /// and it is not marginal. Admitting a residual to the point-read path should therefore turn
        /// on the container's document size, which is what <c>CosmosTableStatistic</c> already
        /// estimates — not on the shape of the predicate alone.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task ALargeBodyIsWhereTheRejectingCaseCouldReverse()
        {
            var readLive = await Mean(() => PointRead("big-live", LargePartition));
            var readDead = await Mean(() => PointRead("big-dead", LargePartition));

            var lookupLive = await Mean(async () => (await Query(LookupQuery("big-live", LargePartition), LargePartition)).Charge);
            var lookupDead = await Mean(async () => (await Query(LookupQuery("big-dead", LargePartition), LargePartition)).Charge);

            Say("");
            Say("## Large document (~100 KB body)");
            Say($"point read, residual accepts : {readLive:F2} RU");
            Say($"point read, residual rejects : {readDead:F2} RU");
            Say($"query + residual, accepts    : {lookupLive:F2} RU");
            Say($"query + residual, rejects    : {lookupDead:F2} RU (returns nothing)");
            Say($"the read costs {(readDead / lookupDead):F2}x the query it would replace, discarding a body it paid for");

            // Measured, and the reverse of what the single-document leg finds: a point read is
            // charged for the body it returns, and past a few KB that outgrows the engine floor the
            // query pays. The read has no floor to save once the document is the dominant cost.
            readLive.Should().BeGreaterThan(lookupLive, "a point read is charged for the whole body, which a large document makes the dominant cost");
            readDead.Should().BeGreaterThan(lookupDead, "the rejecting case is worse still: the read pays for a body the residual discards, where the query returns nothing");
        }

    }

}
