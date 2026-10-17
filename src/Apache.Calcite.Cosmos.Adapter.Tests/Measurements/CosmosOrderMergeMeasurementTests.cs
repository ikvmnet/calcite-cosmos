using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// Measures the order a cross-partition <c>ORDER BY</c> over strings comes back in, on a container
    /// with two physical partitions, and holds it to <see cref="ServiceOrderModel"/>: each partition by
    /// code point, merged by the SDK by UTF-16 code unit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What every order proof rests on, and what no emulator can show.</b> The emulator holds one
    /// physical partition at any throughput, and there the service's order is the whole answer. On an
    /// account with two, the SDK merges the partitions' streams by <c>ItemComparer</c>, which compares
    /// strings by <c>String.CompareOrdinal</c>, while each partition sorts them by code point — so where a
    /// partition holds characters from both sides of U+D800 the stream is in neither order. Measured on
    /// the probe account, whose streams <c>CosmosServiceOrderModelTests</c> pins the model to; and a
    /// declared-string <c>ORDER BY … DESC FETCH 3</c> there returns the wrong rows on main.
    /// </para>
    /// <para>
    /// <b>The container is the caller's.</b> These tests create none: a container splits into two
    /// physical partitions only past 10,000 RU/s, which is not a test's to provision. They read the
    /// database and container <c>COSMOS_TEST_MULTIPARTITION_DATABASE</c> and
    /// <c>COSMOS_TEST_MULTIPARTITION_CONTAINER</c> name, on the account <c>COSMOS_TEST_ENDPOINT</c> and
    /// <c>COSMOS_TEST_KEY</c> name, and skip without them, or where the container has one feed range. The
    /// partition key must be one top-level path, and the indexing policy must index <c>/s</c>. Every
    /// document a test writes carries a tag of its own run and is deleted before it ends.
    /// </para>
    /// <para>
    /// <b>The placement is found, not assumed.</b> Which partition a key's documents land in is the
    /// hash's business, so placeholders under many keys are written first and each feed range is read
    /// alone to see which keys it holds.
    /// </para>
    /// </remarks>
    public class CosmosOrderMergeMeasurementTests
    {

        static readonly string? Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT");
        static readonly string? Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY");
        static readonly string? DatabaseName = Environment.GetEnvironmentVariable("COSMOS_TEST_MULTIPARTITION_DATABASE");
        static readonly string? ContainerName = Environment.GetEnvironmentVariable("COSMOS_TEST_MULTIPARTITION_CONTAINER");

        /// <summary>The strings both partitions hold: every unit below U+D800, where the two orders are one.</summary>
        static readonly string[] Low = ["", "a", "A", "B", "_", "1", "\u00E9", "e\u0301", "\uD7FF"];

        /// <summary>
        /// What the first partition holds besides: characters from both sides of the line, so that its own
        /// stream is code point order and not UTF-16 order. The probe account's arrangement C.
        /// </summary>
        static readonly string[] First = ["\uFF21", "\uFFFD", "\uFFFF", "a\uE000", "\U00010000", "\U0001F600", "a\U0001F600"];

        /// <summary>What the second partition holds besides.</summary>
        static readonly string[] Second = ["\uE000", "\U0010FFFF", "\U0001F600a"];

        /// <summary>
        /// Each partition alone answers in code point order, and the whole answers in the SDK's merge of
        /// the two, which is neither code point order nor Calcite's.
        /// </summary>
        [Fact]
        public async Task ACrossPartitionOrderIsTheMergeOfCodePointRuns()
        {
            await using var probe = await Probe.Open();

            foreach (var descending in new[] { false, true })
            {
                var direction = descending ? "DESC" : "ASC";
                int Partition(string a, string b) => descending ? -ServiceOrderModel.CompareCodePoints(a, b) : ServiceOrderModel.CompareCodePoints(a, b);
                int Merge(string a, string b) => descending ? -string.CompareOrdinal(a, b) : string.CompareOrdinal(a, b);

                var runs = new List<IReadOnlyList<string>>();
                foreach (var (range, held) in probe.Ranges.Zip(new[] { Low.Concat(First), Low.Concat(Second) }))
                {
                    var run = await probe.Strings($"SELECT VALUE c.s FROM c WHERE c.probe = @probe ORDER BY c.s {direction}", range);
                    run.Should().Equal(held.OrderBy(s => s, Comparer<string>.Create(Partition)), $"a partition alone orders by code point ({direction})");
                    runs.Add(run);
                }

                var whole = await probe.Strings($"SELECT VALUE c.s FROM c WHERE c.probe = @probe ORDER BY c.s {direction}", null);

                whole.Should().Equal(ServiceOrderModel.Merge(runs, Merge), $"the SDK merges the runs by UTF-16 code unit ({direction})");
                whole.Should().NotEqual(whole.OrderBy(s => s, Comparer<string>.Create(Partition)), "the whole is not code point order");
                whole.Should().NotEqual(whole.OrderBy(s => s, Comparer<string>.Create(Merge)), "nor Calcite's");
            }
        }

        /// <summary>
        /// A cross-partition <c>MAX</c> is the greater by UTF-16 of each partition's code point maximum:
        /// right here, by chance of the placement, and wrong where one partition's maximum is not its
        /// UTF-16 one.
        /// </summary>
        [Fact]
        public async Task ACrossPartitionMaxIsTheMergeOfEachPartitionsMax()
        {
            await using var probe = await Probe.Open();

            var maxima = new List<string>();
            foreach (var (range, held) in probe.Ranges.Zip(new[] { Low.Concat(First), Low.Concat(Second) }))
            {
                var max = (await probe.Strings("SELECT VALUE MAX(c.s) FROM c WHERE c.probe = @probe", range)).Single();
                max.Should().Be(held.Max(Comparer<string>.Create(ServiceOrderModel.CompareCodePoints)), "a partition's MAX is by code point");
                maxima.Add(max);
            }

            var whole = (await probe.Strings("SELECT VALUE MAX(c.s) FROM c WHERE c.probe = @probe", null)).Single();
            whole.Should().Be(maxima.Max(StringComparer.Ordinal), "the SDK takes the greater by UTF-16");
        }

        /// <summary>
        /// The adapter's reader completes the merge to Calcite's order: the stream re-sorted between anchors, every
        /// window <c>OFFSET o LIMIT f</c> either certified by its edges or read again unpaged, and the <c>MAX</c> read
        /// again to the first anchor — each statement through the adapter's own executor, one iterator from its start.
        /// </summary>
        /// <remarks>
        /// What the probe account measured of the certificate, run through <c>CosmosOrderedCursor</c> itself: theorems 1
        /// to 3 held on every one-iterator stream and window there, and this holds the code that relies on them to the
        /// same streams.
        /// </remarks>
        [Fact]
        public async Task TheReaderCompletesTheMergeToCalcitesOrder()
        {
            await using var probe = await Probe.Open();

            var executor = new Adapter.Client.CosmosQueryExecutor(probe.Container);
            var all = Low.Concat(Low).Concat(First).Concat(Second).ToList();
            IReadOnlyList<Adapter.Sql.CosmosParameter> parameters = [new("@probe", probe.Tag)];

            async Task<List<string>> Read(string sql, Adapter.CosmosOrderProgram program, int? size = null)
            {
                var cursor = await Adapter.Client.CosmosCursors.OpenAsync(executor, null!, new CosmosQuery(sql, parameters, MaxItemCount: size, Order: program), row => row.GetProperty("s").GetString()!, CancellationToken.None);
                var rows = new List<string>();

                await using (cursor)
                    while (await cursor.ReadAsync(CancellationToken.None))
                        rows.Add(cursor.Current);

                return rows;
            }

            foreach (var descending in new[] { false, true })
            {
                var direction = descending ? "DESC" : "ASC";
                var calcite = all.OrderBy(s => s, Comparer<string>.Create((a, b) => descending ? -string.CompareOrdinal(a, b) : string.CompareOrdinal(a, b))).ToList();
                var unpaged = $"SELECT VALUE {{ \"s\": c.s, \"$k0\": c.s }} FROM c WHERE c.probe = @probe AND IS_STRING(c.s) ORDER BY c.s {direction}";
                var key = new Adapter.CosmosOrderedKey("$k0", Adapter.CosmosOrderedKeyKind.Text, descending, false, Adapter.Metadata.CosmosOrderAnchor.Text);

                (await Read(unpaged, new([key], false, null, null, unpaged, [], []))).Should().Equal(calcite, $"the reader's stream is Calcite's order ({direction})");

                for (var offset = 0; offset < all.Count; offset += 3)
                    foreach (var fetch in new[] { 3, 4 })
                    {
                        var window = await Read($"{unpaged} OFFSET {offset} LIMIT {fetch}", new([key], false, offset, fetch, unpaged, [], []), offset + fetch);
                        window.Should().Equal(calcite.Skip(offset).Take(fetch), $"OFFSET {offset} LIMIT {fetch} {direction}");
                    }
            }

            var max = "SELECT MAX(c.s) AS \"s\" FROM c WHERE c.probe = @probe AND IS_STRING(c.s)";
            var extreme = new Adapter.CosmosOrderExtreme("s", true, Adapter.CosmosOrderedKeyKind.Text, Adapter.Metadata.CosmosOrderAnchor.Text, "SELECT VALUE c.s FROM c WHERE c.probe = @probe AND IS_STRING(c.s) ORDER BY c.s DESC", true);
            (await Read(max, new([], false, null, null, max, [], [extreme]))).Should().Equal([all.Max(StringComparer.Ordinal)!], "Calcite's MAX");
        }

        /// <summary>
        /// The documents of one test, written into the caller's container across its first two feed
        /// ranges, and deleted when the test ends.
        /// </summary>
        sealed class Probe : IAsyncDisposable
        {

            readonly CosmosClient _client;
            readonly Container _container;
            readonly string _key;
            readonly string _tag = "order-merge-" + Guid.NewGuid().ToString("N");
            readonly List<(string Id, string Partition)> _written = [];

            Probe(CosmosClient client, Container container, string key, IReadOnlyList<FeedRange> ranges)
            {
                _client = client;
                _container = container;
                _key = key;
                Ranges = ranges;
            }

            /// <summary>The two feed ranges the documents were placed in, the first first.</summary>
            public IReadOnlyList<FeedRange> Ranges { get; }

            /// <summary>The container the documents were written to.</summary>
            public Container Container => _container;

            /// <summary>The tag every document of this test carries in <c>probe</c>.</summary>
            public string Tag => _tag;

            /// <summary>
            /// Connects, finds two keys in each of the first two feed ranges, and writes the arrangement:
            /// the low strings under a key of each range, the rest under the first or the second.
            /// </summary>
            public static async Task<Probe> Open()
            {
                if (string.IsNullOrEmpty(Endpoint) || string.IsNullOrEmpty(Key) || string.IsNullOrEmpty(DatabaseName) || string.IsNullOrEmpty(ContainerName))
                    Assert.Skip("Needs a container with two physical partitions: COSMOS_TEST_ENDPOINT, COSMOS_TEST_KEY, COSMOS_TEST_MULTIPARTITION_DATABASE and COSMOS_TEST_MULTIPARTITION_CONTAINER.");

                var client = new CosmosClient(Endpoint, Key);
                try
                {
                    var container = client.GetContainer(DatabaseName, ContainerName);
                    var properties = (await container.ReadContainerAsync()).Resource;

                    var path = properties.PartitionKeyPath;
                    if (path is null || path.LastIndexOf('/') != 0)
                        Assert.Skip($"The container's partition key must be one top-level path; it is {path ?? "hierarchical"}.");

                    var ranges = await container.GetFeedRangesAsync();
                    if (ranges.Count < 2)
                        Assert.Skip($"The container has {ranges.Count} feed range; this measures a merge of two.");

                    var probe = new Probe(client, container, path[1..], ranges.Take(2).ToList());
                    await probe.Place();
                    return probe;
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }

            async Task Write(string id, string partition, string group, string? s)
            {
                var document = new Dictionary<string, object?> { ["id"] = id, [_key] = partition, ["probe"] = _tag, ["group"] = group };
                if (s is not null)
                    document["s"] = s;

                await _container.CreateItemAsync(document, new PartitionKey(partition));
                _written.Add((id, partition));
            }

            async Task Place()
            {
                // Placeholders under 64 keys, each feed range read alone for the keys it holds.
                for (var i = 0; i < 64; i++)
                    await Write($"{_tag}-p{i}", $"{_tag}-k{i}", "placeholder", null);

                var keys = new List<List<string>>();
                foreach (var range in Ranges)
                {
                    var held = await Strings($"SELECT VALUE c[\"{_key}\"] FROM c WHERE c.probe = @probe AND c[\"group\"] = \"placeholder\"", range);
                    if (held.Count < 1)
                        Assert.Skip("A feed range held none of 64 placeholder keys.");

                    keys.Add(held);
                }

                foreach (var (id, partition) in _written.ToList())
                    await _container.DeleteItemAsync<object>(id, new PartitionKey(partition));
                _written.Clear();

                var n = 0;
                foreach (var s in Low)
                {
                    await Write($"{_tag}-{n++}", keys[0][0], "strings", s);
                    await Write($"{_tag}-{n++}", keys[1][0], "strings", s);
                }

                foreach (var s in First)
                    await Write($"{_tag}-{n++}", keys[0][n % keys[0].Count], "strings", s);

                foreach (var s in Second)
                    await Write($"{_tag}-{n++}", keys[1][n % keys[1].Count], "strings", s);
            }

            /// <summary>
            /// Reads a statement's strings through one iterator, as the adapter does: over one feed range,
            /// or across the container where none is named.
            /// </summary>
            public async Task<List<string>> Strings(string sql, FeedRange? range)
            {
                var query = new QueryDefinition(sql).WithParameter("@probe", _tag);
                using var iterator = range is null
                    ? _container.GetItemQueryIterator<string>(query)
                    : _container.GetItemQueryIterator<string>(range, query);

                var strings = new List<string>();
                while (iterator.HasMoreResults)
                    strings.AddRange(await iterator.ReadNextAsync());

                return strings;
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    foreach (var (id, partition) in _written)
                        await _container.DeleteItemAsync<object>(id, new PartitionKey(partition));
                }
                finally
                {
                    _client.Dispose();
                }
            }

        }

    }

}
