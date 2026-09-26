using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Covers the cursors a compiled plan reads from and writes through, with no planner and no service.
    /// </summary>
    /// <remarks>
    /// The converter and the table modify put these on the per-row path, and the plan tests reach them
    /// only by way of a whole statement. What is checked here is each open's own contract: what it
    /// acquires, under which token, what the cursor it hands back owns, and where a synchronous caller
    /// waits.
    /// </remarks>
    public class CosmosCursorsTests
    {

        /// <summary>
        /// Opens canned values and records the token of the open and the cursor it handed back.
        /// </summary>
        sealed class StubExecutor : ICosmosQueryExecutor
        {

            readonly string[] _documents;

            public StubExecutor(params string[] documents)
            {
                _documents = documents;
            }

            public List<CosmosQuery> Executed { get; } = new();

            public CancellationToken Token { get; private set; }

            public ListCursor<JsonElement>? Cursor { get; private set; }

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                Executed.Add(query);
                Token = cancellationToken;

                // Suspends, so that a caller waiting synchronously has a continuation to be careful with.
                await Task.Yield();

                return Cursor = ListCursor.Documents(_documents);
            }

        }

        /// <summary>
        /// Records every write and the token it ran under, and can be told to refuse one.
        /// </summary>
        sealed class RecordingWriter : ICosmosItemWriter
        {

            public List<string> Calls { get; } = new();

            public List<CancellationToken> Tokens { get; } = new();

            public List<string> Documents { get; } = new();

            /// <summary>
            /// The call that throws, counted from one, or zero for none.
            /// </summary>
            public int FailOn { get; set; }

            /// <summary>
            /// What a delete or a replace answers: whether there was a document to affect.
            /// </summary>
            public bool Found { get; set; } = true;

            void Record(string call, CancellationToken token, byte[]? document = null)
            {
                Calls.Add(call);
                Tokens.Add(token);

                if (document is not null)
                    Documents.Add(Encoding.UTF8.GetString(document));

                if (FailOn == Calls.Count)
                    throw new CosmosExecutionException("refused");
            }

            public Task CreateItemAsync(byte[] document, PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                Record("create", cancellationToken, document);
                return Task.CompletedTask;
            }

            public Task<bool> DeleteItemAsync(string id, PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                Record("delete " + id, cancellationToken);
                return Task.FromResult(Found);
            }

            public Task<bool> DeletePartitionAsync(PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                Record("delete partition " + partitionKey, cancellationToken);
                return Task.FromResult(true);
            }

            public Task<bool> SupportsPartitionDeleteAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(true);

            public Task<bool> ReplaceItemAsync(byte[] document, string id, PartitionKey partitionKey, CancellationToken cancellationToken = default)
            {
                Record("replace " + id, cancellationToken, document);
                return Task.FromResult(Found);
            }

        }

        /// <summary>
        /// A synchronization context that records every continuation posted to it and runs none, which
        /// is what a UI thread blocked on a wait amounts to.
        /// </summary>
        sealed class RecordingContext : SynchronizationContext
        {

            public int Posts { get; private set; }

            public override void Post(SendOrPostCallback d, object? state)
            {
                Posts++;

                // Run it elsewhere, so that a test that fails does so by assertion rather than by hanging.
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }

        }

        static CosmosQuery Query() => new("SELECT VALUE c.n FROM c", Array.Empty<CosmosParameter>());

        static readonly Func<JsonElement, int> Number = element => element.GetInt32();

        // ── Reading ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task TheAwaitingOpenBuildsEachRowFromItsValue()
        {
            var executor = new StubExecutor("1", "2", "3");

            var rows = await ListCursor.CollectAsync(await CosmosCursors.OpenAsync(executor, null!, Query(), Number, CancellationToken.None));

            rows.Should().Equal(1, 2, 3);
        }

        [Fact]
        public void TheSynchronousOpenReadsTheSameRows()
        {
            var executor = new StubExecutor("1", "2", "3");

            using var cursor = CosmosCursors.Open(executor, null!, Query(), Number);

            var rows = new List<int>();
            while (cursor.Read())
                rows.Add(cursor.Current);

            rows.Should().Equal(1, 2, 3);
        }

        [Fact]
        public async Task TheOpensTokenReachesTheExecutor()
        {
            var executor = new StubExecutor("1");

            using var open = new CancellationTokenSource();
            await using var cursor = await CosmosCursors.OpenAsync(executor, null!, Query(), Number, open.Token);

            executor.Token.Should().Be(open.Token);
        }

        /// <remarks>
        /// The row cursor adds nothing between an advance and the executor's cursor, so the token each
        /// <c>ReadAsync</c> is given is the token the executor's advance — and so its page fetch — runs
        /// under.
        /// </remarks>
        [Fact]
        public async Task EachAdvancesTokenReachesTheExecutorsCursor()
        {
            var executor = new StubExecutor("1", "2");

            using var first = new CancellationTokenSource();
            using var second = new CancellationTokenSource();

            await using var cursor = await CosmosCursors.OpenAsync(executor, null!, Query(), Number, CancellationToken.None);

            await cursor.ReadAsync(first.Token);
            await cursor.ReadAsync(second.Token);

            executor.Cursor!.Tokens.Should().Equal(first.Token, second.Token);
        }

        [Fact]
        public async Task DisposingTheRowCursorDisposesTheExecutorsWithAwait()
        {
            var executor = new StubExecutor("1");

            var cursor = await CosmosCursors.OpenAsync(executor, null!, Query(), Number, CancellationToken.None);
            await cursor.DisposeAsync();

            executor.Cursor!.Disposed.Should().BeTrue();
        }

        [Fact]
        public void DisposingTheRowCursorDisposesTheExecutorsSynchronously()
        {
            var executor = new StubExecutor("1");

            var cursor = CosmosCursors.Open(executor, null!, Query(), Number);
            cursor.Dispose();

            executor.Cursor!.Disposed.Should().BeTrue();
        }

        [Fact]
        public async Task TheOpenRefusesAMissingExecutorOrRowBuilder()
        {
            var noExecutor = async () => await CosmosCursors.OpenAsync<int>(null!, null!, Query(), Number, CancellationToken.None);
            var noBuilder = async () => await CosmosCursors.OpenAsync<int>(new StubExecutor(), null!, Query(), null!, CancellationToken.None);

            await noExecutor.Should().ThrowAsync<ArgumentNullException>();
            await noBuilder.Should().ThrowAsync<ArgumentNullException>();
        }

        /// <remarks>
        /// <b>The one place the adapter blocks, and the reason it is written the way it is.</b> A
        /// continuation is captured at the moment of suspension, which is inside the open's synchronous
        /// phase — so the context has to be gone before the open is called, not merely around the wait.
        /// On a single-threaded context a continuation posted there would never run while the thread is
        /// blocked on it. Nothing is posted, and the caller's context is put back.
        /// </remarks>
        [Fact]
        public void ASynchronousOpenPostsNothingToTheCallersContext()
        {
            var executor = new StubExecutor("1");
            var context = new RecordingContext();
            var previous = SynchronizationContext.Current;

            SynchronizationContext.SetSynchronizationContext(context);

            try
            {
                using var cursor = CosmosCursors.Open(executor, null!, Query(), Number);

                SynchronizationContext.Current.Should().BeSameAs(context, "the caller's context is restored after the wait");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            context.Posts.Should().Be(0);
        }

        // ── Writing ───────────────────────────────────────────────────────────────

        static readonly string[] Columns = ["DOC", "id", "_ts", "_etag", "$.category"];

        static readonly string[] PartitionKeyPaths = ["/category"];

        static CosmosWrite Write(CosmosWriteOperation operation, string[]? updates = null, object?[]? partition = null) =>
            new(operation, Columns, PartitionKeyPaths, updates, partition);

        static object?[] Row(string id) => ["{\"id\":\"" + id + "\",\"category\":\"bikes\"}", id, null, null, "bikes"];

        static ValueTask<IClrCursor<object?[]>?> Open(ListCursor<object?[]>? rows) => new(rows);

        static async Task<List<long>> Counts(ValueTask<IClrCursor<long>> open) =>
            await ListCursor.CollectAsync(await open);

        [Fact]
        public async Task AnInsertWritesEveryRowAndAnswersWithOneCount()
        {
            var writer = new RecordingWriter();
            var rows = new ListCursor<object?[]>([Row("a"), Row("b")]);

            var counts = await Counts(CosmosCursors.WriteAsync(Open(rows), writer, Write(CosmosWriteOperation.Insert), r => r!, c => c, null, null, CancellationToken.None));

            counts.Should().Equal(2L);
            writer.Calls.Should().Equal("create", "create");
        }

        [Fact]
        public void TheSynchronousWriteMakesTheSameWrites()
        {
            var writer = new RecordingWriter();
            var rows = new ListCursor<object?[]>([Row("a"), Row("b")]);

            using var cursor = CosmosCursors.Write(rows, writer, Write(CosmosWriteOperation.Insert), r => r!, c => c);

            cursor.Read().Should().BeTrue();
            cursor.Current.Should().Be(2L);
            cursor.Read().Should().BeFalse();
            writer.Calls.Should().Equal("create", "create");
        }

        /// <remarks>
        /// The writes are the acquisition, as a sort's drain is: every one is made by the open, under the
        /// open's token, so the cursor handed back has nothing left to do but hold the count.
        /// </remarks>
        [Fact]
        public async Task EveryWriteIsMadeByTheOpenUnderTheOpensToken()
        {
            var writer = new RecordingWriter();
            var rows = new ListCursor<object?[]>([Row("a"), Row("b")]);

            using var open = new CancellationTokenSource();
            await using var cursor = await CosmosCursors.WriteAsync(Open(rows), writer, Write(CosmosWriteOperation.Insert), r => r!, c => c, null, null, open.Token);

            writer.Calls.Should().HaveCount(2, "nothing was read from the cursor yet");
            writer.Tokens.Should().OnlyContain(t => t == open.Token);
            rows.Tokens.Should().OnlyContain(t => t == open.Token, "the input is drained under the same token");
        }

        [Fact]
        public async Task TheInputIsDisposedOnceItHasBeenDrained()
        {
            var rows = new ListCursor<object?[]>([Row("a")]);

            await using var cursor = await CosmosCursors.WriteAsync(Open(rows), new RecordingWriter(), Write(CosmosWriteOperation.Insert), r => r!, c => c, null, null, CancellationToken.None);

            rows.Disposed.Should().BeTrue();
        }

        /// <remarks>
        /// Writes already sent are not undone by the failure of a later one, so the container may have
        /// changed however far the writes got — and what the lookup cache remembers about it may be
        /// wrong either way. The input is released too.
        /// </remarks>
        [Fact]
        public async Task AFailedWriteStillClearsTheCacheAndReleasesTheInput()
        {
            var cache = new CosmosLookupCache(10, TimeSpan.FromMinutes(5));
            cache.Set("s", "bikes", [JsonDocument.Parse("""{"v":1}""").RootElement.Clone()]);

            var writer = new RecordingWriter { FailOn = 2 };
            var rows = new ListCursor<object?[]>([Row("a"), Row("b"), Row("c")]);

            var write = async () => await CosmosCursors.WriteAsync(Open(rows), writer, Write(CosmosWriteOperation.Insert), r => r!, c => c, cache, null, CancellationToken.None);

            await write.Should().ThrowAsync<CosmosExecutionException>();
            cache.Rows.Should().Be(0);
            rows.Disposed.Should().BeTrue();
            writer.Calls.Should().HaveCount(2, "the third row was never written");
        }

        /// <remarks>
        /// A delete counts what it removed rather than what it was asked to remove: a document already
        /// gone is not an error, and not one more row affected.
        /// </remarks>
        [Fact]
        public async Task ADeleteCountsOnlyTheDocumentsThatWereThere()
        {
            var writer = new RecordingWriter { Found = false };
            var rows = new ListCursor<object?[]>([Row("a"), Row("b")]);

            var counts = await Counts(CosmosCursors.WriteAsync(Open(rows), writer, Write(CosmosWriteOperation.Delete), r => r!, c => c, null, null, CancellationToken.None));

            counts.Should().Equal(0L);
            writer.Calls.Should().Equal("delete a", "delete b");
        }

        [Fact]
        public async Task AnUpdateReplacesTheDocumentItsRowNamesWithTheOneItsSetDescribes()
        {
            var writer = new RecordingWriter();
            var rows = new ListCursor<object?[]>([[.. Row("a"), """{"id":"a","category":"bikes","price":25}"""]]);

            var counts = await Counts(CosmosCursors.WriteAsync(Open(rows), writer, Write(CosmosWriteOperation.Update, ["DOC"]), r => r!, c => c, null, null, CancellationToken.None));

            counts.Should().Equal(1L);
            writer.Calls.Should().Equal("replace a");
            JsonDocument.Parse(writer.Documents.Single()).RootElement.GetProperty("price").GetInt32().Should().Be(25);
        }

        [Fact]
        public async Task ARowWithNoIdCannotBeDeleted()
        {
            var rows = new ListCursor<object?[]>([["{\"category\":\"bikes\"}", null, null, null, "bikes"]]);

            var write = async () => await CosmosCursors.WriteAsync(Open(rows), new RecordingWriter(), Write(CosmosWriteOperation.Delete), r => r!, c => c, null, null, CancellationToken.None);

            await write.Should().ThrowAsync<CosmosExecutionException>().WithMessage("*no 'id'*");
        }

        /// <remarks>
        /// Only a whole-partition delete may arrive with no input, because only it reads none. Anything
        /// else with nothing to read is a plan built wrong, and it says so rather than writing nothing.
        /// </remarks>
        [Fact]
        public async Task AWriteThatReadsRowsRefusesToHaveNone()
        {
            var write = async () => await CosmosCursors.WriteAsync(Open(null), new RecordingWriter(), Write(CosmosWriteOperation.Insert), r => r!, c => c, null, null, CancellationToken.None);

            await write.Should().ThrowAsync<CosmosExecutionException>().WithMessage("*no rows*");
        }

        [Fact]
        public async Task AWholePartitionDeleteCountsThenRemovesThePartition()
        {
            var writer = new RecordingWriter();
            var counted = new List<PartitionKey>();

            Task<long> Counter(PartitionKey key, CancellationToken _)
            {
                counted.Add(key);
                return Task.FromResult(7L);
            }

            var counts = await Counts(CosmosCursors.WriteAsync(Open(null), writer, Write(CosmosWriteOperation.DeletePartition, partition: ["bikes"]), r => r!, c => c, null, Counter, CancellationToken.None));

            counts.Should().Equal(7L);
            counted.Should().Equal(new PartitionKey("bikes"));
            writer.Calls.Should().ContainSingle().Which.Should().StartWith("delete partition");
        }

        /// <remarks>
        /// The plan hands it none, but given one it still reads nothing — and releases what it was
        /// given, since the write owns its input either way.
        /// </remarks>
        [Fact]
        public async Task AWholePartitionDeleteReadsNoInputItIsGivenButReleasesIt()
        {
            var rows = new ListCursor<object?[]>([Row("a")]);

            await using var cursor = await CosmosCursors.WriteAsync(Open(rows), new RecordingWriter(), Write(CosmosWriteOperation.DeletePartition, partition: ["bikes"]), r => r!, c => c, null, (_, _) => Task.FromResult(1L), CancellationToken.None);

            rows.Tokens.Should().BeEmpty();
            rows.Disposed.Should().BeTrue();
        }

        [Fact]
        public async Task AWholePartitionDeleteNeedsItsKeyAndACounter()
        {
            var noKey = async () => await CosmosCursors.WriteAsync(Open(null), new RecordingWriter(), Write(CosmosWriteOperation.DeletePartition), r => r!, c => c, null, (_, _) => Task.FromResult(1L), CancellationToken.None);
            var noCounter = async () => await CosmosCursors.WriteAsync(Open(null), new RecordingWriter(), Write(CosmosWriteOperation.DeletePartition, partition: ["bikes"]), r => r!, c => c, null, null, CancellationToken.None);

            await noKey.Should().ThrowAsync<CosmosExecutionException>().WithMessage("*no partition key*");
            await noCounter.Should().ThrowAsync<CosmosExecutionException>().WithMessage("*count the partition*");
        }

        [Fact]
        public async Task TheCountsCursorHonoursACancelledAdvance()
        {
            await using var cursor = await CosmosCursors.WriteAsync(Open(new ListCursor<object?[]>([])), new RecordingWriter(), Write(CosmosWriteOperation.Insert), r => r!, c => c, null, null, CancellationToken.None);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var read = async () => await cursor.ReadAsync(cancelled.Token);
            await read.Should().ThrowAsync<OperationCanceledException>();
        }

    }

}
