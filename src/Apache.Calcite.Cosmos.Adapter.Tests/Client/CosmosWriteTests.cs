using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Writes documents to a live Cosmos DB emulator, through the same runtime the plan calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CosmosCursors.WriteAsync"/> is the seam a compiled plan actually enters, so these
    /// drive it rather than the SDK wrapper beneath it: the document a row describes, the partition key
    /// recovered from that document, and the request are one path and are worth testing as one. What
    /// the row means in isolation is covered by <see cref="CosmosDocumentTests"/>, with no service.
    /// </para>
    /// <para>
    /// Its own container, because these add and remove documents and the read fixture asserts a
    /// specific four. Reports inconclusive where no account is reachable, as the read tests do.
    /// </para>
    /// </remarks>
    public class CosmosWriteTests : IClassFixture<CosmosWriteTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown. xUnit drives these through a fixture the
        /// class asks for rather than through static hooks the framework calls by attribute.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await ClassInitialize();

            public ValueTask DisposeAsync() { ClassCleanup(); return default; }

        }


        static string Endpoint => CosmosEmulator.Endpoint!;

        static string Key => CosmosEmulator.Key!;

        static bool IsEmulator => CosmosEmulator.IsEmulator;

        /// <summary>
        /// The database these tests build, named per target framework and distinct from the read tests'.
        /// </summary>
        /// <remarks>
        /// Per framework for the reason the read fixture records — a plain <c>dotnet test</c> runs every
        /// target at once — and distinct from that fixture's so the two cannot drop each other's
        /// containers while both are running.
        /// </remarks>
        static readonly string DatabaseName = "calcite_cosmos_write_tests_" +
            System.Text.RegularExpressions.Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        static readonly string[] Columns = ["DOC", "id", "_ts", "_etag", "$.category"];

        static readonly string[] PartitionKeyPaths = ["/category"];

        static CosmosClient? _client;
        static Container? _container;

        static async Task ClassInitialize()
        {
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(IsEmulator ? 5 : 30),
                MaxRetryAttemptsOnRateLimitedRequests = 0,
            };

            if (IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(IsEmulator ? 10 : 120));
                var client = new CosmosClient(Endpoint, Key, options);

                var database = (await client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;

                try { await database.GetContainer("writes").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }

                _container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("writes", "/category"), cancellationToken: cts.Token)).Container;
                _client = client;
            }
            catch (Exception)
            {
                _client = null;
                _container = null;
            }
        }

        static void ClassCleanup()
        {
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            _client?.Dispose();
            _client = null;
            _container = null;
        }

        static Container Container()
        {
            if (_container is null)
                Assert.Skip("No Cosmos DB account reachable at " + Endpoint);

            return _container!;
        }

        static ValueTask<IClrCursor<object?[]>?> Rows(params object?[][] rows) =>
            new(new ListCursor<object?[]>(rows));

        /// <summary>
        /// Reads the one row a write's cursor holds, which is the count.
        /// </summary>
        static async Task<long> Affected(ValueTask<IClrCursor<long>> open)
        {
            await using var cursor = await open;

            if (await cursor.ReadAsync(CancellationToken.None) == false)
                throw new InvalidOperationException("The write yielded no row count.");

            return cursor.Current;
        }

        /// <summary>
        /// Runs the write the way a compiled plan does.
        /// </summary>
        static Task<long> Write(CosmosWriteOperation operation, params object?[][] rows) =>
            Run(operation, null, rows);

        /// <summary>
        /// Runs an update, whose rows trail one value per <paramref name="updates"/> entry.
        /// </summary>
        /// <remarks>
        /// A separate name rather than an overload: an insert row of strings and nulls converts to
        /// <c>string[]</c>, and an overload taking the update columns first would capture it —
        /// leaving no rows, which is how that was found.
        /// </remarks>
        static Task<long> WriteSets(string[] updates, params object?[][] rows) =>
            Run(CosmosWriteOperation.Update, updates, rows);

        static async Task<long> Run(CosmosWriteOperation operation, string[]? updates, object?[][] rows)
        {
            var writer = new CosmosQueryExecutor(Container());
            var write = new CosmosWrite(operation, Columns, PartitionKeyPaths, updates);

            return await Affected(CosmosCursors.WriteAsync<object?[], long>(Rows(rows), writer, write, r => r!, c => c, null, null, CancellationToken.None));
        }

        /// <summary>
        /// Answers as an account without the whole-partition delete preview does, and records what
        /// it was asked.
        /// </summary>
        sealed class RefusingWriter : ICosmosItemWriter
        {

            readonly ICosmosItemWriter _inner;

            public RefusingWriter(ICosmosItemWriter inner) => _inner = inner;

            public List<PartitionKey> PartitionsDeleted { get; } = new();

            public Task CreateItemAsync(byte[] document, PartitionKey partitionKey, CancellationToken cancellationToken = default) =>
                _inner.CreateItemAsync(document, partitionKey, cancellationToken);

            public Task<bool> DeleteItemAsync(string id, PartitionKey partitionKey, CancellationToken cancellationToken = default) =>
                _inner.DeleteItemAsync(id, partitionKey, cancellationToken);

            public Task<bool> ReplaceItemAsync(byte[] document, string id, PartitionKey partitionKey, CancellationToken cancellationToken = default) =>
                _inner.ReplaceItemAsync(document, id, partitionKey, cancellationToken);

            public Task<bool> DeletePartitionAsync(PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                PartitionsDeleted.Add(partitionKey);
                return Task.FromResult(true);
            }

            public Task<bool> SupportsPartitionDeleteAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(false);

        }

        /// <remarks>
        /// The execution path, with the request itself stubbed — no environment reachable from here
        /// enables the preview, so what can be verified is everything around it: that the count
        /// comes from the partition before it is emptied, that the input rows are never read, and
        /// that the service is asked for exactly the partition the predicate named.
        /// </remarks>
        [Fact]
        public async Task AWholePartitionDeleteCountsFirstAndReadsNoRows()
        {
            await Write(CosmosWriteOperation.Insert, ["""{"id":"wp1","category":"bikes"}""", null, null, null, null]);
            await Write(CosmosWriteOperation.Insert, ["""{"id":"wp2","category":"bikes"}""", null, null, null, null]);

            var writer = new RefusingWriter(new CosmosQueryExecutor(Container()));
            var write = new CosmosWrite(CosmosWriteOperation.DeletePartition, Columns, PartitionKeyPaths, null, new object?[] { "bikes" });

            var executor = new CosmosQueryExecutor(Container());

            async Task<long> Count(PartitionKey key, CancellationToken token)
            {
                var query = new CosmosQuery("SELECT VALUE COUNT(1) FROM c", Array.Empty<Apache.Calcite.Cosmos.Adapter.Sql.CosmosParameter>());

                var rows = await ListCursor.CollectAsync(await executor.OpenAsync(query, key, token), token);
                return rows.Count > 0 ? rows[0].GetInt64() : 0;
            }

            // No input at all, which is what the plan hands a whole-partition delete: it does not open
            // one, because opening is acquisition and would scan a page of the partition being emptied.
            var affected = await Affected(CosmosCursors.WriteAsync<object?[], long>(default, writer, write, r => r!, c => c, null, Count, CancellationToken.None));

            affected.Should().BeGreaterThanOrEqualTo(2, "the count is taken from the partition before it is emptied");
            writer.PartitionsDeleted.Should().ContainSingle().Which.Should().Be(new PartitionKey("bikes"));
        }

        static async Task<JsonElement?> Read(string id, PartitionKey partitionKey)
        {
            using var response = await Container().ReadItemStreamAsync(id, partitionKey);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(response.Content);
            return document.RootElement.Clone();
        }

        [Fact]
        public async Task ADocumentDescribedByTheDocumentColumnIsWritten()
        {
            var count = await Write(CosmosWriteOperation.Insert,
                ["""{"id":"m1","category":"bikes","name":"Trail Blazer","price":120}""", null, null, null, null]);

            count.Should().Be(1);

            var written = await Read("m1", new PartitionKey("bikes"));

            written.Should().NotBeNull();
            written!.Value.GetProperty("name").GetString().Should().Be("Trail Blazer");
            written!.Value.GetProperty("price").GetInt64().Should().Be(120);
        }

        /// <summary>
        /// A row carrying only the projections describes nothing.
        /// </summary>
        /// <remarks>
        /// They are <c>STORED</c>, so a statement cannot name one and the values here are the old
        /// row's, which an update carries alongside the new document. Writing them would describe the
        /// same document twice; the service refuses the empty document that results, which is the
        /// loud failure the row model wants rather than a document assembled out of projections.
        /// </remarks>
        [Fact]
        public async Task ARowCarryingOnlyProjectionsDescribesNothing()
        {
            var act = async () => await Write(CosmosWriteOperation.Insert, [null, "p1", null, null, "shoes"]);

            await act.Should().ThrowAsync<Exception>();

            (await Read("p1", new PartitionKey("shoes"))).Should().BeNull();
        }

        /// <summary>
        /// The replace path end to end: the old row identifies the document, the trailing
        /// <c>SET</c> value is the new document, and the result is that document.
        /// </summary>
        /// <remarks>
        /// The row is the shape the planner produces for <c>UPDATE … SET "DOC" = …</c> — the
        /// table's columns holding what the scan read, then one trailing value per <c>SET</c>
        /// column. A property present in the old document and absent from the new one is gone
        /// afterwards, which is what distinguishes a replace from a merge.
        /// </remarks>
        [Fact]
        public async Task UpdateOfTheDocumentColumnReplacesTheDocument()
        {
            await Write(CosmosWriteOperation.Insert,
                ["""{"id":"u1","category":"bikes","price":10,"old":"yes"}""", null, null, null, null]);

            var count = await WriteSets(["DOC"],
                ["""{"id":"u1","category":"bikes","price":10,"old":"yes"}""", "u1", null, null, "bikes",
                 """{"id":"u1","category":"bikes","price":25,"note":"replaced"}"""]);

            count.Should().Be(1);

            var written = await Read("u1", new PartitionKey("bikes"));

            written.Should().NotBeNull();
            written!.Value.GetProperty("price").GetInt64().Should().Be(25);
            written!.Value.GetProperty("note").GetString().Should().Be("replaced");
            written!.Value.TryGetProperty("old", out _).Should().BeFalse("a replace is not a merge");
        }

        /// <summary>
        /// A document gone by the time the replace arrives was deleted by someone else, and a
        /// smaller count is the honest answer — the same stance the delete takes.
        /// </summary>
        [Fact]
        public async Task UpdateOfAMissingDocumentCountsNothing()
        {
            var count = await WriteSets(["DOC"],
                ["""{"id":"u-missing","category":"bikes"}""", "u-missing", null, null, "bikes",
                 """{"id":"u-missing","category":"bikes","price":1}"""]);

            count.Should().Be(0);
        }

        /// <summary>
        /// A document carrying another's bookkeeping is written, and comes back with its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A document read from a container holds <c>_ts</c>, <c>_etag</c>, <c>_rid</c>, <c>_self</c>
        /// and <c>_attachments</c> alongside the caller's own properties, so this is the shape
        /// <c>INSERT … SELECT "DOC" FROM …</c> produces.
        /// </para>
        /// <para>
        /// <b>It does not test the stripping, and it was written believing it did.</b> Probed by
        /// removing <see cref="CosmosDocument.IsServiceProperty"/>'s answer: this still passed, and only
        /// the unit test failed. The service assigns its own values whatever is supplied, so what this
        /// establishes is that fact — worth keeping, because it is the reason the stripping is a
        /// decision about what the adapter writes rather than something the service requires.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task ServicePropertiesSuppliedByARowAreReplacedByTheService()
        {
            var count = await Write(CosmosWriteOperation.Insert,
                ["""{"id":"c1","category":"bikes","_ts":1,"_etag":"\"nonsense\"","_rid":"bogus","name":"Copy"}""", null, null, null, null]);

            count.Should().Be(1);

            var written = await Read("c1", new PartitionKey("bikes"));

            written.Should().NotBeNull();
            written!.Value.GetProperty("name").GetString().Should().Be("Copy");
            written!.Value.GetProperty("_ts").GetInt64().Should().BeGreaterThan(1);
            written!.Value.GetProperty("_etag").GetString().Should().NotBe("\"nonsense\"");
        }

        /// <summary>
        /// A document with no value at the partition key path lives in the "none" logical partition.
        /// </summary>
        /// <remarks>
        /// A real place, and a different one from where a null key routes to. Getting this wrong writes
        /// the document to the wrong partition rather than failing, so it is checked by reading it back
        /// from the partition it should be in.
        /// </remarks>
        [Fact]
        public async Task ADocumentWithoutAPartitionKeyGoesToTheNonePartition()
        {
            var count = await Write(CosmosWriteOperation.Insert,
                ["""{"id":"n1","name":"Unfiled"}""", null, null, null, null]);

            count.Should().Be(1);

            (await Read("n1", PartitionKey.None)).Should().NotBeNull();
        }

        /// <summary>
        /// <c>INSERT</c> creates, so a repeat is a conflict rather than a replacement.
        /// </summary>
        [Fact]
        public async Task ADuplicateIdIsRefused()
        {
            await Write(CosmosWriteOperation.Insert, ["""{"id":"d1","category":"bikes","name":"First"}""", null, null, null, null]);

            var act = async () => await Write(CosmosWriteOperation.Insert, ["""{"id":"d1","category":"bikes","name":"Second"}""", null, null, null, null]);

            await act.Should().ThrowAsync<CosmosExecutionException>().WithMessage("*409*");

            // And the first is untouched, which is the half of "refused" worth stating.
            var written = await Read("d1", new PartitionKey("bikes"));
            written!.Value.GetProperty("name").GetString().Should().Be("First");
        }

        [Fact]
        public async Task ADeletedDocumentIsGone()
        {
            await Write(CosmosWriteOperation.Insert, ["""{"id":"x1","category":"shoes"}""", null, null, null, null]);

            var count = await Write(CosmosWriteOperation.Delete, ["""{"id":"x1","category":"shoes"}""", null, null, null, null]);

            count.Should().Be(1);
            (await Read("x1", new PartitionKey("shoes"))).Should().BeNull();
        }

        /// <summary>
        /// A row naming a document that is not there affects nothing, and is not an error.
        /// </summary>
        /// <remarks>
        /// The rows were read before they were deleted, so a document gone by the time the delete
        /// arrives was deleted by someone else. Reporting a smaller count is the honest answer.
        /// </remarks>
        [Fact]
        public async Task DeletingWhatIsNotThereAffectsNothing()
        {
            var count = await Write(CosmosWriteOperation.Delete, ["""{"id":"absent","category":"shoes"}""", null, null, null, null]);

            count.Should().Be(0);
        }

        [Fact]
        public async Task EveryRowIsWrittenAndCounted()
        {
            var count = await Write(CosmosWriteOperation.Insert,
                ["""{"id":"b1","category":"bikes"}""", null, null, null, null],
                ["""{"id":"b2","category":"bikes"}""", null, null, null, null],
                ["""{"id":"b3","category":"shoes"}""", null, null, null, null]);

            count.Should().Be(3);

            (await Read("b1", new PartitionKey("bikes"))).Should().NotBeNull();
            (await Read("b2", new PartitionKey("bikes"))).Should().NotBeNull();
            (await Read("b3", new PartitionKey("shoes"))).Should().NotBeNull();
        }

        /// <summary>
        /// A write is charged, and is reported as a write rather than as a query.
        /// </summary>
        [Fact]
        public async Task AWriteIsMeasured()
        {
            var charges = new List<double>();

            using var listener = new System.Diagnostics.Metrics.MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CosmosInstrumentation.Name && instrument.Name == "cosmos.request_charge")
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "cosmos.request_kind" && (string?)tag.Value == CosmosInstrumentation.Kinds.Write)
                        lock (charges)
                            charges.Add(measurement);
            });
            listener.Start();

            await Write(CosmosWriteOperation.Insert, ["""{"id":"r1","category":"bikes"}""", null, null, null, null]);

            listener.RecordObservableInstruments();

            charges.Should().NotBeEmpty();
            charges.Should().OnlyContain(c => c > 0);
        }

    }

}
