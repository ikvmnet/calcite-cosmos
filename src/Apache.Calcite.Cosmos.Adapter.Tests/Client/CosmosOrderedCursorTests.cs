using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;

using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// The reader that completes an order the plan proved only around anchors, over canned streams in the service's
    /// orders: what it emits, which statements it sends, and what it reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every stream here is one a partition or the SDK's merge can return: strings in code point order within a
    /// partition, and partitions merged by UTF-16 code unit — the arrangement the probe account measured, which is in
    /// neither order. What comes out is held to Calcite's order, by <see cref="string.CompareOrdinal(string, string)"/>.
    /// </para>
    /// <para>
    /// No planner and no service: a statement and its program are written here as the implementor writes them, and
    /// opened through <see cref="CosmosCursors.OpenAsync{TRow}"/>, which binds the parameters and hands the statement to
    /// the reader.
    /// </para>
    /// </remarks>
    public class CosmosOrderedCursorTests
    {

        /// <summary>
        /// Answers each statement it is sent from canned rows, by its text, and records what it was sent.
        /// </summary>
        sealed class StubExecutor(Func<CosmosQuery, IEnumerable<string>> answer) : ICosmosQueryExecutor
        {

            public List<CosmosQuery> Executed { get; } = new();

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                lock (Executed)
                    Executed.Add(query);

                await Task.Yield();
                return ListCursor.Documents(answer(query));
            }

        }

        /// <summary>
        /// A data context that answers the values it was given.
        /// </summary>
        sealed class Context(params (string Name, object? Value)[] values) : org.apache.calcite.DataContext
        {
            public org.apache.calcite.schema.SchemaPlus getRootSchema() => null!;

            public org.apache.calcite.adapter.java.JavaTypeFactory getTypeFactory() => null!;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => values.FirstOrDefault(v => v.Name == name).Value!;
        }

        const string Statement = "SELECT VALUE { \"id\": c.id, \"$k0\": c.id } FROM c ORDER BY c.id ASC";

        static readonly CosmosOrderedKey ById = new("$k0", CosmosOrderedKeyKind.Text, false, false, CosmosOrderAnchor.Text);

        static string Row(string id) => $"{{\"id\":{JsonSerializer.Serialize(id)},\"$k0\":{JsonSerializer.Serialize(id)}}}";

        static CosmosOrderProgram Sort(CosmosRowLimit? offset = null, CosmosRowLimit? fetch = null, bool distinct = false, IReadOnlyList<CosmosOrderComparison>? comparisons = null, string unpaged = Statement) =>
            new([ById], distinct, offset, fetch, unpaged, comparisons ?? [], (comparisons ?? []).Select((_, i) => CosmosRecheck.Compared(i)).ToList(), []);

        static async Task<List<string>> Read(StubExecutor executor, CosmosQuery query, org.apache.calcite.DataContext? root = null)
        {
            var cursor = await CosmosCursors.OpenAsync(executor, root!, query, row => row.GetProperty("id").GetString()!, CancellationToken.None);
            return await ListCursor.CollectAsync(cursor);
        }

        /// <summary>
        /// Listens to the adapter's activities for the length of a test, keeping the reader's that belong to it.
        /// </summary>
        sealed class Recording : IDisposable
        {

            readonly ActivityListener _listener;
            readonly Activity _test;

            public Recording()
            {
                _test = new Activity("test").Start();
                _listener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == CosmosInstrumentation.Name,
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = activity =>
                    {
                        if (activity.OperationName == "cosmos.order" && activity.TraceId == _test.TraceId)
                            lock (Orders)
                                Orders.Add(activity);
                    },
                };

                ActivitySource.AddActivityListener(_listener);
            }

            public List<Activity> Orders { get; } = new();

            public object? Tag(string name) => Orders.Single().GetTagItem(name);

            public void Dispose()
            {
                _listener.Dispose();
                _test.Stop();
            }

        }

        // ── A stream ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Theorem 1: the anchors stay where the service put them and the runs between them are re-sorted. One partition
        /// orders U+E000 before U+1F600 by code point; Calcite the other way.
        /// </summary>
        [Fact]
        public async Task ARunBetweenAnchorsIsSortedAndTheAnchorsStay()
        {
            using var recording = new Recording();
            var executor = new StubExecutor(_ => new[] { "a", "b", "", "\U0001F600", "\U0001F600a" }.Select(Row));

            var rows = await Read(executor, new CosmosQuery(Statement, [], Order: Sort()));

            rows.Should().Equal("a", "b", "\U0001F600", "\U0001F600a", "");
            executor.Executed.Should().ContainSingle().Which.Sql.Should().Be(Statement);
            recording.Tag("cosmos.order.program").Should().Be("sort");
            recording.Tag("cosmos.order.reissued").Should().Be(false);
            recording.Tag("cosmos.order.buffered").Should().Be(3);
        }

        /// <summary>
        /// The stream the probe account returned over two physical partitions, one holding characters from both sides
        /// of U+D800: each partition by code point, merged by the SDK by UTF-16 code unit, range 0 first on a tie — in
        /// neither order, and in Calcite's once read.
        /// </summary>
        [Fact]
        public async Task TheMergeOfTwoPartitionsComesOutInCalcitesOrder()
        {
            string[] low = ["", "a", "A", "B", "_", "1", "é", "é", "퟿"];
            var range0 = low.Concat(["Ａ", "�", "￿", "a", "\U00010000", "\U0001F600", "a\U0001F600"]).ToList();
            var range1 = low.Concat(["", "\U0010FFFF", "\U0001F600a"]).ToList();

            foreach (var descending in new[] { false, true })
            {
                int Partition(string x, string y) => descending ? -ServiceOrderModel.CompareCodePoints(x, y) : ServiceOrderModel.CompareCodePoints(x, y);
                int Merge(string x, string y) => descending ? -string.CompareOrdinal(x, y) : string.CompareOrdinal(x, y);

                var stream = ServiceOrderModel.Merge(
                    [range0.OrderBy(s => s, Comparer<string>.Create(Partition)).ToList(), range1.OrderBy(s => s, Comparer<string>.Create(Partition)).ToList()],
                    Merge);

                stream.Should().NotEqual(stream.OrderBy(s => s, Comparer<string>.Create(Merge)), "the merge is not Calcite's order as it comes");

                var executor = new StubExecutor(_ => stream.Select(Row));
                var program = new CosmosOrderProgram([ById with { Descending = descending }], false, null, null, Statement, [], [], []);
                var rows = await Read(executor, new CosmosQuery(Statement, [], Order: program));

                rows.Should().Equal(range0.Concat(range1).OrderBy(s => s, Comparer<string>.Create(Merge)), descending ? "descending" : "ascending");
            }
        }

        /// <summary>
        /// A <c>DISTINCT</c>'s value the SDK's merge left apart — one partition holds b and U+1F600, the other a, U+E000
        /// and U+1F600, and the merge emits a, b, U+1F600, U+E000, U+1F600 — comes out once.
        /// </summary>
        [Fact]
        public async Task ADistinctsRepeatAcrossRunsComesOutOnce()
        {
            var executor = new StubExecutor(_ => new[] { "a", "b", "\U0001F600", "", "\U0001F600" }.Select(Row));

            var rows = await Read(executor, new CosmosQuery(Statement, [], Order: Sort(distinct: true)));

            rows.Should().Equal("a", "b", "\U0001F600", "");
        }

        /// <summary>
        /// A <c>DISTINCT</c>'s repeat is told apart by the bytes the service sent, so a row holding text a client wrote
        /// that is not UTF-8 reaches the row's reader, which refuses it by name — where telling it apart by its text failed
        /// first, with the decoder's error, which named nothing. The two rows tie on a numeric key, which is what makes
        /// the reader tell them apart at all.
        /// </summary>
        [Fact]
        public async Task ADistinctsRowHoldingTextThatIsNotUtf8IsRefusedByItsReader()
        {
            static JsonElement Bytes(string before, byte invalid, string after) =>
                JsonDocument.Parse((byte[])[.. System.Text.Encoding.UTF8.GetBytes(before), invalid, .. System.Text.Encoding.UTF8.GetBytes(after)]).RootElement.Clone();

            var rows = new[] { JsonDocument.Parse("""{"id":"a","s":"ok","$k0":1}""").RootElement.Clone(), Bytes("""{"id":"b","s":"x""", 0xFF, """y","$k0":1}""") };
            var key = new CosmosOrderedKey("$k0", CosmosOrderedKeyKind.Exact, false, false, CosmosOrderAnchor.None);
            var program = new CosmosOrderProgram([key], true, null, null, Statement, [], [], []);

            var cursor = await CosmosCursors.OpenAsync(new RowsExecutor(rows), null!, new CosmosQuery(Statement, [], Order: program),
                row => (string)CosmosJson.GetProperty(row, "s", org.apache.calcite.sql.type.SqlTypeName.VARCHAR, null, ["id"])!, CancellationToken.None);

            var act = () => ListCursor.CollectAsync(cursor);

            await act.Should().ThrowAsync<CosmosMaterializationException>().WithMessage("""Column 's' of document "b" holds text that is not UTF-8 at $: the bytes FF in "x\xFFy".*""");
        }

        /// <summary>
        /// Answers every statement with the same rows, parsed from bytes a string cannot carry.
        /// </summary>
        sealed class RowsExecutor(IReadOnlyList<JsonElement> rows) : ICosmosQueryExecutor
        {

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                return new ListCursor<JsonElement>(rows);
            }

        }

        // ── A window ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Theorem 2: a window whose first row and last row are anchors holds Calcite's rows, and is the one statement
        /// sent.
        /// </summary>
        [Fact]
        public async Task AWindowWhoseEdgesAreAnchorsIsTheServicesPage()
        {
            using var recording = new Recording();
            var paged = Statement + " OFFSET 1 LIMIT 4";
            var executor = new StubExecutor(_ => new[] { "b", "b", "b\U0001F600", "c" }.Select(Row));

            var rows = await Read(executor, new CosmosQuery(paged, [], MaxItemCount: 5, Order: Sort(offset: 1, fetch: 4)));

            rows.Should().Equal("b", "b\U0001F600", "b", "c");
            executor.Executed.Should().ContainSingle().Which.Sql.Should().Be(paged);
            recording.Tag("cosmos.order.program").Should().Be("page");
            recording.Tag("cosmos.order.reissued").Should().Be(false);
            recording.Tag("cosmos.order.buffered").Should().Be(2);
        }

        /// <summary>
        /// A window whose last row is no anchor may hold the wrong rows — the service's first two of a, U+E000 and
        /// U+1F600 are a and U+E000, Calcite's a and U+1F600 — and is read again unpaged, the page taken from the
        /// sorted stream.
        /// </summary>
        [Fact]
        public async Task AWindowWhoseEdgeIsNoAnchorIsReadAgain()
        {
            using var recording = new Recording();
            var paged = Statement + " OFFSET 0 LIMIT 2";
            string[] stream = ["a", "", "\U0001F600"];
            var executor = new StubExecutor(q => (q.Sql == paged ? stream.Take(2) : stream).Select(Row));

            var rows = await Read(executor, new CosmosQuery(paged, [], MaxItemCount: 2, Order: Sort(offset: 0, fetch: 2)));

            rows.Should().Equal("a", "\U0001F600");
            executor.Executed.Select(q => q.Sql).Should().Equal(paged, Statement);
            executor.Executed[1].MaxItemCount.Should().Be(2, "the page is still the most the fallback has to give back");
            recording.Tag("cosmos.order.reissued").Should().Be(true);
        }

        /// <summary>
        /// A window past an offset needs its first row an anchor too, or the rows skipped may not be Calcite's first.
        /// </summary>
        [Fact]
        public async Task AWindowPastAnOffsetNeedsItsFirstRowAnAnchor()
        {
            var paged = Statement + " OFFSET 2 LIMIT 2";
            string[] stream = ["a", "a", "a\U0001F600", "b"];
            var executor = new StubExecutor(q => (q.Sql == paged ? stream.Skip(2).Take(2) : stream).Select(Row));

            var rows = await Read(executor, new CosmosQuery(paged, [], MaxItemCount: 4, Order: Sort(offset: 2, fetch: 2)));

            rows.Should().Equal("a", "b");
            executor.Executed.Should().HaveCount(2);
        }

        /// <summary>
        /// A <c>DISTINCT</c>'s offset counts what the service returned, which may hold a value twice before the window
        /// where nobody can see it; so its window is taken here from the start.
        /// </summary>
        [Fact]
        public async Task ADistinctsWindowPastAnOffsetIsTakenHere()
        {
            var paged = Statement + " OFFSET 1 LIMIT 2";
            var executor = new StubExecutor(_ => new[] { "a", "b", "\U0001F600", "", "\U0001F600" }.Select(Row));

            var rows = await Read(executor, new CosmosQuery(paged, [], MaxItemCount: 3, Order: Sort(offset: 1, fetch: 2, distinct: true)));

            rows.Should().Equal("b", "\U0001F600");
            executor.Executed.Should().ContainSingle().Which.Sql.Should().Be(Statement);
        }

        // ── A comparison with a parameter ─────────────────────────────────────────────────────

        static CosmosOrderComparison Comparison(string op, string bound) => new(
            "@p0",
            "@p1",
            op,
            CosmosOrderAnchor.Text,
            "c.id",
            $"(c.id {op} @p0)",
            op is ">" or ">=" ? "(c.id >= @p1)" : "(c.id < @p1)",
            "IS_STRING(c.id)",
            "$k0");

        static IReadOnlyList<CosmosParameter> Slots(string op) =>
        [
            new("@p0", new CosmosDynamicValue(0)),
            new("@p1", new CosmosDynamicValue(0) { Bound = op is ">" or ">=" ? CosmosAnchorBound.Lower : CosmosAnchorBound.Upper }),
        ];

        /// <summary>
        /// A keyset seek past a value that is no anchor: the service's <c>c.id &gt; @p</c> keeps the strings above it by
        /// code point, which loses U+E000 after <c>a</c> + U+1F600. So the statement is read with the bound every string
        /// Calcite keeps satisfies — the value up to U+D800 — and the comparison is rechecked here.
        /// </summary>
        [Fact]
        public async Task AKeysetPastAValueThatIsNoAnchorIsWeakenedAndRechecked()
        {
            using var recording = new Recording();
            var where = "SELECT VALUE { \"id\": c.id, \"$k0\": c.id } FROM c WHERE (c.id > @p0) ORDER BY c.id ASC";
            string[] stream = ["a", "a", "a\U0001F600", "a\U0001F600x", "b", "c"];
            var executor = new StubExecutor(q => stream.Where(id => ServiceOrderModel.CompareCodePoints(id, (string)q.Parameters.Single().Value!) >= 0).Select(Row));

            var rows = await Read(executor, new CosmosQuery(where + " OFFSET 0 LIMIT 3", Slots(">"), MaxItemCount: 3, Order: Sort(offset: 0, fetch: 3, comparisons: [Comparison(">", "@p1")], unpaged: where)), new Context(("?0", "a\U0001F600")));

            rows.Should().Equal("a\U0001F600x", "a", "b");
            var sent = executor.Executed.Should().ContainSingle().Subject;
            sent.Sql.Should().Be(where.Replace("(c.id > @p0)", "(c.id >= @p1)"));
            sent.Parameters.Should().ContainSingle().Which.Should().Be(new CosmosParameter("@p1", "a"), "only what the statement names, the bound written from the value");
            recording.Tag("cosmos.order.reissued").Should().Be(true);
        }

        /// <summary>
        /// The other direction: below the value up to U+D800 with its last character raised, which bounds every string
        /// Calcite finds below it; and where the value begins at U+D800 nothing bounds them but the type test.
        /// </summary>
        [Fact]
        public async Task AKeysetBelowAValueThatIsNoAnchorIsWeakenedAndRechecked()
        {
            var where = "SELECT VALUE { \"id\": c.id, \"$k0\": c.id } FROM c WHERE (c.id < @p0) ORDER BY c.id DESC";
            string[] stream = ["c", "b\U0001F600", "b￿", "b", "b", "a"];
            var executor = new StubExecutor(_ => stream.Select(Row));
            var program = new CosmosOrderProgram([ById with { Descending = true }], false, null, null, where, [Comparison("<", "@p1")], [CosmosRecheck.Compared(0)], []);

            var rows = await Read(executor, new CosmosQuery(where, Slots("<"), Order: program), new Context(("?0", "b")));
            rows.Should().Equal("b\U0001F600", "b", "a");
            executor.Executed.Single().Sql.Should().Be(where.Replace("(c.id < @p0)", "(c.id < @p1)"));
            executor.Executed.Single().Parameters.Single().Value.Should().Be("c");

            executor.Executed.Clear();
            rows = await Read(executor, new CosmosQuery(where, Slots("<"), Order: program), new Context(("?0", "")));
            rows.Should().Equal("c", "b￿", "b", "b\U0001F600", "b", "a");
            executor.Executed.Single().Sql.Should().Be(where.Replace("(c.id < @p0)", "IS_STRING(c.id)"));
            executor.Executed.Single().Parameters.Should().BeEmpty("the type test names no parameter");
        }

        /// <summary>
        /// A value that is an anchor makes the comparison exact: the statement is sent as it is, page and all.
        /// </summary>
        [Fact]
        public async Task AKeysetPastAnAnchorIsTheStatementAsWritten()
        {
            var where = "SELECT VALUE { \"id\": c.id, \"$k0\": c.id } FROM c WHERE (c.id > @p0) ORDER BY c.id ASC";
            var executor = new StubExecutor(_ => new[] { "b", "c" }.Select(Row));

            var rows = await Read(executor, new CosmosQuery(where + " OFFSET 0 LIMIT 2", Slots(">"), MaxItemCount: 2, Order: Sort(offset: 0, fetch: 2, comparisons: [Comparison(">", "@p1")], unpaged: where)), new Context(("?0", "a")));

            rows.Should().Equal("b", "c");
            executor.Executed.Single().Sql.Should().Be(where + " OFFSET 0 LIMIT 2");
            executor.Executed.Single().Parameters.Should().ContainSingle().Which.Name.Should().Be("@p0");
        }

        /// <summary>
        /// The seek past two keys, <c>s &gt; ? OR (s = ? AND id &gt; ?)</c>, whose comparisons sit under an <c>OR</c>: the
        /// one whose value is no anchor is weakened in the fallback, which makes the conjunct weaker, and the reader
        /// rechecks the conjunct whole — the comparisons as Calcite compares text, the equality as the service answered
        /// it, read back from its member.
        /// </summary>
        [Fact]
        public async Task ASeekPastTwoKeysIsRecheckedWhole()
        {
            const string Seek = "SELECT VALUE { \"id\": c.id, \"$c0\": c.s, \"$c1\": c.id, \"$b0\": (c.s = @p1) } FROM c WHERE ((c.s > @p0) OR ((c.s = @p1) AND (c.id > @p2)))";

            CosmosOrderComparison Past(string parameter, string bound, string path, string member) =>
                new(parameter, bound, ">", CosmosOrderAnchor.Text, path, $"({path} > {parameter})", $"({path} >= {bound})", $"IS_STRING({path})", member);

            var program = new CosmosOrderProgram([], false, null, null, Seek,
                [Past("@p0", "@p3", "c.s", "$c0"), Past("@p2", "@p4", "c.id", "$c1")],
                [CosmosRecheck.Of(false, [CosmosRecheck.Compared(0), CosmosRecheck.Of(true, [CosmosRecheck.Answered("(c.s = @p1)") with { Member = "$b0" }, CosmosRecheck.Compared(1)])])],
                []);

            IReadOnlyList<CosmosParameter> slots =
            [
                new("@p0", new CosmosDynamicValue(0)),
                new("@p1", new CosmosDynamicValue(1)),
                new("@p2", new CosmosDynamicValue(2)),
                new("@p3", new CosmosDynamicValue(0) { Bound = CosmosAnchorBound.Lower }),
                new("@p4", new CosmosDynamicValue(2) { Bound = CosmosAnchorBound.Lower }),
            ];

            const string Last = "a\U0001F600";

            string Seeked(string id, string? s) => s is null
                ? $"{{\"id\":{JsonSerializer.Serialize(id)},\"$c1\":{JsonSerializer.Serialize(id)}}}"
                : $"{{\"id\":{JsonSerializer.Serialize(id)},\"$c0\":{JsonSerializer.Serialize(s)},\"$c1\":{JsonSerializer.Serialize(id)},\"$b0\":{(s == Last ? "true" : "false")}}}";

            var executor = new StubExecutor(_ =>
            [
                Seeked("a", "b"),
                Seeked("z", "a\uE000"),
                Seeked("n", Last),
                Seeked("b", Last),
                Seeked("y", "a"),
                Seeked("x", null),
            ]);

            var rows = await Read(executor, new CosmosQuery(Seek, slots, Order: program), new Context(("?0", Last), ("?1", Last), ("?2", "m")));

            rows.Should().Equal("a", "z", "n");
            executor.Executed.Single().Sql.Should().Be(Seek.Replace("(c.s > @p0)", "(c.s >= @p3)"), "the comparison past an anchor stays as it is");
        }

        // ── An extreme ────────────────────────────────────────────────────────────────────────

        const string Aggregate = "SELECT MAX(c.id) AS \"m\" FROM c";

        const string Reread = "SELECT VALUE c.id FROM c ORDER BY c.id DESC";

        static CosmosOrderProgram Extreme(bool ordered = true) =>
            new([], false, null, null, Aggregate, [], [], [new CosmosOrderExtreme("m", true, CosmosOrderedKeyKind.Text, CosmosOrderAnchor.Text, Reread, ordered)]);

        static async Task<List<string?>> ReadExtreme(StubExecutor executor, CosmosOrderProgram program)
        {
            var cursor = await CosmosCursors.OpenAsync(executor, null!, new CosmosQuery(Aggregate, [], Order: program), row => row.TryGetProperty("m", out var m) ? m.GetString() : null, CancellationToken.None);
            return await ListCursor.CollectAsync(cursor);
        }

        /// <summary>
        /// Theorem 3: an extreme that is an anchor is Calcite's, and nothing more is read.
        /// </summary>
        [Fact]
        public async Task AnExtremeThatIsAnAnchorIsCalcites()
        {
            var executor = new StubExecutor(_ => ["{\"m\":\"z\"}"]);

            (await ReadExtreme(executor, Extreme())).Should().Equal("z");
            executor.Executed.Should().ContainSingle();
        }

        /// <summary>
        /// The service's <c>MAX</c> of U+1F600 and U+FFFF is U+1F600 by code point and Calcite's U+FFFF: read again, in
        /// order, up to the first anchor — past which nothing lies above it in either order.
        /// </summary>
        [Fact]
        public async Task AnExtremeThatIsNoAnchorIsReadAgainToTheFirstAnchor()
        {
            using var recording = new Recording();
            var executor = new StubExecutor(q => q.Sql == Aggregate
                ? ["{\"m\":\"\\uD83D\\uDE00\"}"]
                : new[] { "\U0001F600", "￿", "￾", "z", "a" }.Select(s => JsonSerializer.Serialize(s)));

            (await ReadExtreme(executor, Extreme())).Should().Equal("￿");
            executor.Executed.Select(q => q.Sql).Should().Equal(Aggregate, Reread);
            recording.Tag("cosmos.order.program").Should().Be("extreme");
            recording.Tag("cosmos.order.reissued").Should().Be(true);
        }

        /// <summary>
        /// Where the service does not order the argument, it is read whole.
        /// </summary>
        [Fact]
        public async Task AnExtremeTheServiceCannotOrderIsReadWhole()
        {
            var executor = new StubExecutor(q => q.Sql == Aggregate
                ? ["{\"m\":\"\\uD83D\\uDE00\"}"]
                : new[] { "z", "\U0001F600", "a", "￾" }.Select(s => JsonSerializer.Serialize(s)));

            (await ReadExtreme(executor, Extreme(ordered: false))).Should().Equal("￾");
        }

        /// <summary>
        /// Read synchronously, as the synchronous body does, the same rows come back.
        /// </summary>
        [Fact]
        public void ASynchronousReadIsTheSameRows()
        {
            var executor = new StubExecutor(_ => new[] { "a", "b", "", "\U0001F600" }.Select(Row));
            using var cursor = CosmosCursors.Open(executor, null!, new CosmosQuery(Statement, [], Order: Sort()), row => row.GetProperty("id").GetString()!);

            var rows = new List<string>();
            while (cursor.Read())
                rows.Add(cursor.Current);

            rows.Should().Equal("a", "b", "\U0001F600", "");
        }

    }

}
