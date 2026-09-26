using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using NSubstitute;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    public partial class CosmosQueryExecutorTests
    {

        /// <summary>
        /// Covers the cursors the executor opens, against an SDK that answers from memory.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The enclosing class runs statements against a service; this one runs none. What it checks is
        /// the cursor's own bookkeeping, which a service cannot show: which page is fetched by which call,
        /// under which token, and what is released when.
        /// </para>
        /// <para>
        /// The container is a substitute and the iterator is written out, because the iterator is where
        /// the behaviour is: every call to <c>ReadNextAsync</c> is recorded with its token.
        /// </para>
        /// </remarks>
        public class Cursors
        {

            /// <summary>
            /// A feed that answers from pages held in memory, and records every fetch.
            /// </summary>
            sealed class PagedIterator : FeedIterator
            {

                readonly Queue<ResponseMessage> _pages;

                public PagedIterator(IEnumerable<ResponseMessage> pages)
                {
                    _pages = new Queue<ResponseMessage>(pages);
                }

                /// <summary>
                /// The token of every fetch, in order.
                /// </summary>
                public List<CancellationToken> Fetches { get; } = new();

                public bool Disposed { get; private set; }

                public override bool HasMoreResults => _pages.Count > 0;

                public override async Task<ResponseMessage> ReadNextAsync(CancellationToken cancellationToken = default)
                {
                    Fetches.Add(cancellationToken);

                    // A real fetch is a round trip, so this one suspends too.
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();

                    return _pages.Dequeue();
                }

                protected override void Dispose(bool disposing)
                {
                    Disposed = true;
                    base.Dispose(disposing);
                }

            }

            static ResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, double charge = 1d)
            {
                var response = new ResponseMessage(status) { Content = new MemoryStream(Encoding.UTF8.GetBytes(body)) };
                response.Headers["x-ms-request-charge"] = charge.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return response;
            }

            static ResponseMessage Page(params int[] values) =>
                Response("{\"Documents\":[" + string.Join(",", values) + "],\"_count\":" + values.Length + "}");

            static (CosmosQueryExecutor Executor, PagedIterator Iterator, Container Container) Given(params ResponseMessage[] pages)
            {
                var iterator = new PagedIterator(pages);

                var container = Substitute.For<Container>();
                container.Id.Returns("products");
                container.GetItemQueryStreamIterator(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>()).Returns(iterator);

                return (new CosmosQueryExecutor(container), iterator, container);
            }

            static CosmosQuery Query() => new("SELECT VALUE c.n FROM c", Array.Empty<Apache.Calcite.Cosmos.Adapter.Sql.CosmosParameter>());

            static List<int> Values(IEnumerable<JsonElement> elements) => elements.Select(e => e.GetInt32()).ToList();

            // ── Pages ─────────────────────────────────────────────────────────────

            /// <remarks>
            /// Opening is acquisition: the statement is sent and its first page awaited by the open,
            /// under the open's token, as a command's <c>ExecuteReader</c> does.
            /// </remarks>
            [Fact]
            public async Task TheOpenFetchesTheFirstPageUnderItsOwnToken()
            {
                var (executor, iterator, _) = Given(Page(1, 2), Page(3));

                using var open = new CancellationTokenSource();
                await using var cursor = await executor.OpenAsync(Query(), cancellationToken: open.Token);

                iterator.Fetches.Should().Equal(open.Token);
            }

            /// <remarks>
            /// <b>The reason the executor opens a cursor.</b> The advance that runs out of a page fetches
            /// the next one under its own token, and an advance that stays within a page fetches nothing.
            /// </remarks>
            [Fact]
            public async Task ALaterPageIsFetchedUnderTheTokenOfTheAdvanceThatNeedsIt()
            {
                var (executor, iterator, _) = Given(Page(1, 2), Page(3));

                using var open = new CancellationTokenSource();
                using var first = new CancellationTokenSource();
                using var second = new CancellationTokenSource();
                using var third = new CancellationTokenSource();

                await using var cursor = await executor.OpenAsync(Query(), cancellationToken: open.Token);

                (await cursor.ReadAsync(first.Token)).Should().BeTrue();
                (await cursor.ReadAsync(second.Token)).Should().BeTrue();
                iterator.Fetches.Should().Equal(new[] { open.Token }, "both values were in the first page");

                (await cursor.ReadAsync(third.Token)).Should().BeTrue();
                iterator.Fetches.Should().Equal(open.Token, third.Token);

                cursor.Current.GetInt32().Should().Be(3);
            }

            [Fact]
            public async Task EveryPageIsReadInOrderAndThenTheCursorEnds()
            {
                var (executor, _, _) = Given(Page(1, 2), Page(), Page(3, 4));

                var values = Values(await ListCursor.CollectAsync(await executor.OpenAsync(Query())));

                values.Should().Equal(1, 2, 3, 4);
            }

            /// <remarks>
            /// A synchronous advance has no token to give and nowhere to suspend, so it waits for a page
            /// where it has to — and reads the same values.
            /// </remarks>
            [Fact]
            public async Task ASynchronousAdvanceFetchesTheSamePages()
            {
                var (executor, iterator, _) = Given(Page(1), Page(2), Page(3));

                using var cursor = await executor.OpenAsync(Query());

                var values = new List<int>();
                while (cursor.Read())
                    values.Add(cursor.Current.GetInt32());

                values.Should().Equal(1, 2, 3);
                iterator.Fetches.Should().HaveCount(3);
                iterator.Fetches.Skip(1).Should().OnlyContain(t => t.CanBeCanceled == false);
            }

            [Fact]
            public async Task TheTwoWaysOfAdvancingStepOnePosition()
            {
                var (executor, _, _) = Given(Page(1), Page(2), Page(3));

                using var cursor = await executor.OpenAsync(Query());

                cursor.Read().Should().BeTrue();
                cursor.Current.GetInt32().Should().Be(1);
                (await cursor.ReadAsync(CancellationToken.None)).Should().BeTrue();
                cursor.Current.GetInt32().Should().Be(2);
                cursor.Read().Should().BeTrue();
                cursor.Current.GetInt32().Should().Be(3);
                (await cursor.ReadAsync(CancellationToken.None)).Should().BeFalse();
            }

            /// <remarks>
            /// Each value is cloned out of its page, whose document goes back to the pool when the next
            /// page arrives. A value the plan is still holding must survive that.
            /// </remarks>
            [Fact]
            public async Task AValueOutlivesThePageItCameFrom()
            {
                var (executor, _, _) = Given(Page(1), Page(2));

                using var cursor = await executor.OpenAsync(Query());

                cursor.Read().Should().BeTrue();
                var held = cursor.Current;

                cursor.Read().Should().BeTrue();

                held.GetInt32().Should().Be(1);
            }

            [Fact]
            public async Task ACancelledAdvanceDoesNotFetch()
            {
                var (executor, iterator, _) = Given(Page(1), Page(2));

                using var cursor = await executor.OpenAsync(Query());
                (await cursor.ReadAsync(CancellationToken.None)).Should().BeTrue();

                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();

                var read = async () => await cursor.ReadAsync(cancelled.Token);

                await read.Should().ThrowAsync<OperationCanceledException>();
                iterator.Fetches.Should().HaveCount(1, "the second page was never asked for");
            }

            // ── Release ───────────────────────────────────────────────────────────

            [Fact]
            public async Task DisposingTheCursorDisposesTheIterator()
            {
                var (executor, iterator, _) = Given(Page(1), Page(2));

                var cursor = await executor.OpenAsync(Query());
                (await cursor.ReadAsync(CancellationToken.None)).Should().BeTrue();

                await cursor.DisposeAsync();

                iterator.Disposed.Should().BeTrue("a reader that stops early releases the statement");
            }

            /// <remarks>
            /// A statement the service refuses fails the open rather than the first advance, and what the
            /// open had acquired is released, since no cursor exists to own it.
            /// </remarks>
            [Fact]
            public async Task ARefusedStatementFailsTheOpenAndReleasesTheIterator()
            {
                var (executor, iterator, _) = Given(Response("{}", HttpStatusCode.BadRequest));

                var open = async () => await executor.OpenAsync(Query());

                await open.Should().ThrowAsync<CosmosException>();
                iterator.Disposed.Should().BeTrue();
            }

            /// <remarks>
            /// The span covers the statement from its first request to the reader letting go of it, and
            /// carries its totals only where the reader got to the end.
            /// </remarks>
            [Fact]
            public async Task TheSpanCarriesTheTotalsOfEveryPageItRead()
            {
                var (executor, _, _) = Given(Response("""{"Documents":[1]}""", charge: 2.5d), Response("""{"Documents":[2]}""", charge: 1.5d));

                Activity? stopped = null;

                using (var listener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == CosmosInstrumentation.Name,
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = activity => stopped = activity,
                })
                {
                    ActivitySource.AddActivityListener(listener);

                    await ListCursor.CollectAsync(await executor.OpenAsync(Query()));
                }

                stopped.Should().NotBeNull();
                stopped!.GetTagItem("cosmos.request_charge").Should().Be(4d);
                stopped.GetTagItem("cosmos.pages").Should().Be(2);
            }

            // ── Point reads ───────────────────────────────────────────────────────

            static CosmosQuery PointRead(string id) =>
                Query() with { PointReadId = id, PartitionKeyValues = new object?[] { "bikes" }, PartitionKeyIsComplete = true };

            [Fact]
            public async Task APointReadOpensTheOneDocument()
            {
                var (executor, _, container) = Given();
                container.ReadItemStreamAsync("a", Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
                    .Returns(Response("""{"id":"a"}"""));

                var rows = await ListCursor.CollectAsync(await executor.OpenAsync(PointRead("a")));

                rows.Should().ContainSingle().Which.GetProperty("id").GetString().Should().Be("a");
                container.DidNotReceiveWithAnyArgs().GetItemQueryStreamIterator(default(QueryDefinition)!, default, default);
            }

            /// <remarks>
            /// A missing document is the empty result the query it stands in for would have returned,
            /// not an error.
            /// </remarks>
            [Fact]
            public async Task APointReadOfAMissingDocumentOpensNothing()
            {
                var (executor, _, container) = Given();
                container.ReadItemStreamAsync("a", Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
                    .Returns(Response("", HttpStatusCode.NotFound));

                var rows = await ListCursor.CollectAsync(await executor.OpenAsync(PointRead("a")));

                rows.Should().BeEmpty();
            }

            [Fact]
            public async Task APointReadRunsUnderTheOpensToken()
            {
                var (executor, _, container) = Given();
                container.ReadItemStreamAsync("a", Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
                    .Returns(Response("""{"id":"a"}"""));

                using var open = new CancellationTokenSource();
                await using var cursor = await executor.OpenAsync(PointRead("a"), cancellationToken: open.Token);

                await container.Received(1).ReadItemStreamAsync("a", Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), open.Token);
            }

            [Fact]
            public async Task ABatchOfPointReadsOpensTheDocumentsThatExist()
            {
                var (executor, _, container) = Given();
                container.ReadManyItemsStreamAsync(Arg.Any<IReadOnlyList<(string, PartitionKey)>>(), Arg.Any<ReadManyRequestOptions>(), Arg.Any<CancellationToken>())
                    .Returns(Response("""{"Documents":[{"id":"a"},{"id":"c"}]}"""));

                var query = Query() with { PointReadIds = new[] { "a", "b", "c" }, PartitionKeyValues = new object?[] { "bikes" }, PartitionKeyIsComplete = true };
                var rows = await ListCursor.CollectAsync(await executor.OpenAsync(query));

                rows.Select(r => r.GetProperty("id").GetString()).Should().Equal("a", "c");
            }

            /// <remarks>
            /// A buffered cursor has nothing left to fetch, but an advance given a cancelled token still
            /// honours it, as every advance of the convention does.
            /// </remarks>
            [Fact]
            public async Task APointReadsCursorHonoursACancelledAdvance()
            {
                var (executor, _, container) = Given();
                container.ReadItemStreamAsync("a", Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
                    .Returns(Response("""{"id":"a"}"""));

                await using var cursor = await executor.OpenAsync(PointRead("a"));

                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();

                var read = async () => await cursor.ReadAsync(cancelled.Token);
                await read.Should().ThrowAsync<OperationCanceledException>();
            }

        }

    }

}
