using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Extensions.Runtime;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Reads a statement whose order, or whose rows, the plan proved only around values the rows will hold: checks them
    /// as they arrive, keeps the service's answer where they certify it, and reads the fallback statement where they do
    /// not — the run-time half of an anchored verdict (<see cref="CosmosOrderProgram"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The certificate.</b> An anchor (<see cref="CosmosServiceOrder.IsAnchor"/>) has its place in the stream the
    /// service returns: every row before it is at most it in Calcite's order, and every row after at least, through one
    /// partition's order and through the SDK's merge of several alike. So:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>A stream</b> is in Calcite's order once every run of rows between two anchors is re-sorted:
    /// each anchor is emitted as it arrives, after the run before it, sorted. A <c>DISTINCT</c>'s repeats, which the
    /// SDK's merge can leave apart across partitions where the orders part, fall in one tie class of the sorted stream,
    /// and are dropped there.</description></item>
    /// <item><description><b>A window</b> <c>OFFSET o LIMIT f</c> holds the rows Calcite's does where its last row is an
    /// anchor, or the stream ended inside it, and its first is one where <c>o &gt; 0</c>: every row skipped is at most
    /// the first and every row past it at least the last. Re-sorted, it is allowable. Otherwise the statement is read
    /// again unpaged and the page taken here, from the sorted stream, stopping once the page is out and the run it ended
    /// in is closed. A <c>DISTINCT</c>'s window with an offset is never certified, a repeat before it counting twice
    /// where it cannot be seen, and is read unpaged from the start.</description></item>
    /// <item><description><b>An extreme</b> that is an anchor is Calcite's; any other is replaced by the extreme of its
    /// argument read again, in its order up to the first anchor where the service orders it, and whole
    /// otherwise.</description></item>
    /// <item><description><b>A comparison with a parameter</b> is exact where the value is an anchor. Otherwise the
    /// statement is read unpaged with the comparison weakened to a bound every string Calcite keeps satisfies, and the
    /// rows the conjunct it sits in drops are dropped here (<see cref="CosmosRecheck"/>), before any order is checked —
    /// what is left of a stream is a stream the certificate holds of.</description></item>
    /// </list>
    /// <para>
    /// <b>One iterator per statement, read from its start.</b> Each statement is opened through the executor once and read
    /// to where the program stops; the fallback is a statement of its own. Resuming a cross-partition order from a
    /// continuation token in a new iterator lost and repeated rows on the probe account, which no check of the rows that
    /// arrived can see, so nothing here resumes anything.
    /// </para>
    /// <para>
    /// <b>What it reports.</b> An activity, <c>cosmos.order</c>, around the statements it sends, tagged with the program
    /// (<c>cosmos.order.program</c>: <c>sort</c>, <c>page</c>, <c>extreme</c> or <c>comparison</c>), whether the fallback
    /// was read (<c>cosmos.order.reissued</c>), and how many rows were held back because they were no anchors and
    /// re-sorted (<c>cosmos.order.buffered</c>).
    /// </para>
    /// </remarks>
    /// <typeparam name="TRow">The plan's row type.</typeparam>
    sealed class CosmosOrderedCursor<TRow> : ClrCursor<TRow>
    {

        /// <summary>
        /// Opens a statement under its program: binds which comparisons the parameters decide, sends the statement the
        /// program reads first, and waits for its first page.
        /// </summary>
        /// <param name="executor">Executes each statement the program sends.</param>
        /// <param name="query">The statement, its parameters bound.</param>
        /// <param name="program">What completes it.</param>
        /// <param name="rowBuilder">Builds one row from the JSON value Cosmos returned for it.</param>
        /// <param name="cancellationToken">The token the open runs under.</param>
        /// <returns>The rows.</returns>
        public static async ValueTask<IClrCursor<TRow>> OpenAsync(ICosmosQueryExecutor executor, CosmosQuery query, CosmosOrderProgram program, Func<JsonElement, TRow> rowBuilder, CancellationToken cancellationToken)
        {
            var cursor = new CosmosOrderedCursor<TRow>(executor, query, program, rowBuilder);

            try
            {
                await cursor.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                cursor.Dispose();
                throw;
            }

            return cursor;
        }

        /// <summary>
        /// How the rows of the statement being read become the cursor's.
        /// </summary>
        enum Mode
        {
            /// <summary>As they come, but for a recheck and a page taken here.</summary>
            Pass,

            /// <summary>Anchors at once, the runs between them sorted.</summary>
            Stream,

            /// <summary>The page the service took, certified by its edges or read again.</summary>
            Window,

            /// <summary>The aggregate's row, each extreme certified or read again.</summary>
            Extreme,
        }

        readonly ICosmosQueryExecutor _executor;
        readonly CosmosQuery _query;
        readonly CosmosOrderProgram _program;
        readonly Func<JsonElement, TRow> _rowBuilder;
        readonly CosmosKeyComparer _comparer;
        readonly Activity? _activity;
        readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
        readonly List<(CosmosOrderComparison Comparison, JsonElement Value)> _rechecks = new();
        readonly Dictionary<int, JsonElement> _compared = new();
        readonly List<JsonElement> _run = new();
        readonly Queue<JsonElement> _ready = new();
        readonly HashSet<string> _tied = new(StringComparer.Ordinal);

        readonly long _offset;
        readonly long? _fetch;

        IClrCursor<JsonElement>? _source;
        Mode _mode;
        long _skip;
        long? _take;
        long _skipped;
        long _taken;
        bool _exhausted;
        bool _finished;
        JsonElement? _tie;
        int _buffered;
        TRow _current = default!;

        CosmosOrderedCursor(ICosmosQueryExecutor executor, CosmosQuery query, CosmosOrderProgram program, Func<JsonElement, TRow> rowBuilder)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _query = query;
            _program = program ?? throw new ArgumentNullException(nameof(program));
            _rowBuilder = rowBuilder ?? throw new ArgumentNullException(nameof(rowBuilder));
            _comparer = new CosmosKeyComparer(program.Keys);

            foreach (var parameter in query.Parameters)
                _values[parameter.Name] = parameter.Value;

            _offset = Limit(program.Offset) ?? 0;
            _fetch = Limit(program.Fetch);

            _activity = CosmosInstrumentation.ActivitySource.StartActivity("cosmos.order");
            _activity?.SetTag("db.query.text", query.Sql);
            _activity?.SetTag("cosmos.order.program", program.Name);
            _activity?.SetTag("cosmos.order.reissued", false);
            _activity?.SetTag("cosmos.order.buffered", 0);
        }

        /// <inheritdoc />
        public override TRow Current => _current;

        /// <summary>
        /// Reads a row limit's count, written or bound.
        /// </summary>
        long? Limit(CosmosRowLimit? limit)
        {
            if (limit is not CosmosRowLimit l)
                return null;

            if (l.Count is int count)
                return count;

            return l.Parameter is string name && _values.TryGetValue(name, out var value) && value is not null
                ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>
        /// Decides which statement is read first, and opens it.
        /// </summary>
        async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            // A comparison is exact where its parameter is an anchor; any other is weakened in the fallback, and every
            // conjunct holding one is rechecked here, against the values.
            for (var i = 0; i < _program.Comparisons.Count; i++)
            {
                var comparison = _program.Comparisons[i];
                var value = _values.TryGetValue(comparison.Parameter, out var bound) ? JsonSerializer.SerializeToElement(bound) : default;
                _compared[i] = value;

                if (value.ValueKind == JsonValueKind.String && CosmosServiceOrder.IsAnchor(comparison.Anchor, value) == false)
                    _rechecks.Add((comparison, value));
            }

            if (_program.Extremes.Count > 0)
            {
                _mode = Mode.Extreme;
                _source = await OpenAsync(_query.Sql, _query.MaxItemCount, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_rechecks.Count > 0)
            {
                await ReissueAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_program.Keys.Count == 0)
            {
                _mode = Mode.Pass;
                _source = await OpenAsync(_query.Sql, _query.MaxItemCount, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_offset == 0 && _fetch is null)
            {
                _mode = Mode.Stream;
                _source = await OpenAsync(_query.Sql, _query.MaxItemCount, cancellationToken).ConfigureAwait(false);
                return;
            }

            // A DISTINCT's offset counts what the service returned, which across partitions may hold a value twice
            // where the orders part, and a repeat before the window cannot be seen: no window of one is certified.
            if (_program.Distinct && _offset > 0)
            {
                await ReissueAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _mode = Mode.Window;
            _source = await OpenAsync(_query.Sql, _query.MaxItemCount, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the fallback in place of what was read: the statement unpaged, each comparison whose parameter is no anchor
        /// weakened, the page taken here.
        /// </summary>
        /// <remarks>
        /// The weakened form takes the place of the text the translator wrote for the exact one, which occurs once: it
        /// names the parameter slot <see cref="CosmosOrderComparison.Parameter"/>, which the parameter list hands out to
        /// that comparison alone. Which comparisons are weakened is the value's to decide, so the fallback is finished
        /// here rather than when the statement is built, and its two forms per comparison would otherwise be as many
        /// statements as there are combinations of them.
        /// </remarks>
        async ValueTask ReissueAsync(CancellationToken cancellationToken)
        {
            _source?.Dispose();
            _source = null;

            var sql = _program.Unpaged;
            foreach (var (comparison, _) in _rechecks)
                sql = sql.Replace(comparison.Exact, _values.TryGetValue(comparison.Bound, out var bound) && bound is not null ? comparison.Weakened : comparison.Guarded, StringComparison.Ordinal);

            _mode = _program.Keys.Count > 0 ? Mode.Stream : Mode.Pass;
            _skip = _offset;
            _take = _fetch;
            _activity?.SetTag("cosmos.order.reissued", true);

            var size = _fetch is long fetch && _offset + fetch is > 0 and <= int.MaxValue ? (int?)(_offset + fetch) : null;
            _source = await OpenAsync(sql, size, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends one statement, with only the parameters it names, and opens it: one iterator, read from its start.
        /// </summary>
        async ValueTask<IClrCursor<JsonElement>> OpenAsync(string sql, int? maxItemCount, CancellationToken cancellationToken)
        {
            var parameters = new List<CosmosParameter>(_query.Parameters.Count);
            foreach (var parameter in _query.Parameters)
                if (Names(sql, parameter.Name))
                    parameters.Add(parameter);

            var statement = _query with { Sql = sql, Parameters = parameters, MaxItemCount = maxItemCount, PointReadId = null, PointReadIds = null, Order = null };

            // Each statement's own span beneath this one, whichever advance sends it.
            var previous = Activity.Current;
            if (_activity is not null)
                Activity.Current = _activity;

            try
            {
                return await _executor.OpenAsync(statement, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (_activity is not null)
                    Activity.Current = previous;
            }
        }

        /// <summary>
        /// Determines whether a statement names a parameter: its name, not followed by another character of a name.
        /// </summary>
        static bool Names(string sql, string name)
        {
            for (var at = sql.IndexOf(name, StringComparison.Ordinal); at >= 0; at = sql.IndexOf(name, at + 1, StringComparison.Ordinal))
            {
                var end = at + name.Length;
                if (end >= sql.Length || (char.IsLetterOrDigit(sql[end]) || sql[end] == '_') == false)
                    return true;
            }

            return false;
        }

        /// <inheritdoc />
        public override bool Read() => CosmosCursors.Wait(ReadAsync);

        /// <inheritdoc />
        public override async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                while (_finished == false && _ready.Count > 0)
                    if (Admit(_ready.Dequeue()) is JsonElement row)
                    {
                        _current = _rowBuilder(row);
                        return true;
                    }

                if (_finished || _exhausted)
                    return Finish();

                await FillAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Passes a row in Calcite's order on, where it is not a repeat of a <c>DISTINCT</c>'s, nor before the page or past
        /// it.
        /// </summary>
        JsonElement? Admit(JsonElement row)
        {
            if (_take is long take && _taken >= take)
            {
                _finished = true;
                return null;
            }

            // A repeat ties the row it repeats, and in a sorted stream a tie class is contiguous.
            if (_program.Distinct)
            {
                if (_tie is JsonElement tie && _comparer.Compare(tie, row) == 0)
                {
                    if (_tied.Add(Identity(row)) == false)
                        return null;
                }
                else
                {
                    _tie = row;
                    _tied.Clear();
                    _tied.Add(Identity(row));
                }
            }

            if (_skipped < _skip)
            {
                _skipped++;
                return null;
            }

            _taken++;
            return row;
        }

        /// <summary>
        /// Reads from the statement until there is a row ready, or it ends.
        /// </summary>
        async ValueTask FillAsync(CancellationToken cancellationToken)
        {
            switch (_mode)
            {
                case Mode.Window:
                    await WindowAsync(cancellationToken).ConfigureAwait(false);
                    return;

                case Mode.Extreme:
                    await ExtremesAsync(cancellationToken).ConfigureAwait(false);
                    return;
            }

            while (_ready.Count == 0)
            {
                if (await _source!.ReadAsync(cancellationToken).ConfigureAwait(false) == false)
                {
                    Flush();
                    _exhausted = true;
                    return;
                }

                var row = _source.Current;
                if (Keeps(row) == false)
                    continue;

                if (_mode == Mode.Pass || _comparer.IsAnchor(row))
                {
                    Flush();
                    _ready.Enqueue(row);
                }
                else
                {
                    _run.Add(row);
                    _buffered++;
                }
            }
        }

        /// <summary>
        /// Sorts the run of rows since the last anchor and passes it on, in Calcite's order.
        /// </summary>
        void Flush()
        {
            if (_run.Count == 0)
                return;

            _run.Sort(_comparer);
            foreach (var row in _run)
                _ready.Enqueue(row);

            _run.Clear();
        }

        /// <summary>
        /// Determines whether a row of the fallback satisfies every conjunct holding a comparison weakened in it, as
        /// Calcite answers each: see <see cref="True"/>. The statement as written needs no recheck.
        /// </summary>
        bool Keeps(JsonElement row)
        {
            if (_rechecks.Count == 0)
                return true;

            foreach (var conjunct in _program.Rechecks)
                if (True(conjunct, row) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Determines whether a conjunct is true of a row: an <c>AND</c> or an <c>OR</c> of its operands; a comparison as
        /// Calcite compares text, which reads nothing but a string as a value and compares by UTF-16 code unit
        /// (<see cref="CosmosKeyComparer"/>), a null or a value of another type being unknown; and a predicate as the
        /// service answered it, only <c>true</c> counting. Truth is all a <c>WHERE</c> keeps a row by, so unknown and
        /// false count alike.
        /// </summary>
        bool True(CosmosRecheck conjunct, JsonElement row)
        {
            switch (conjunct.Kind)
            {
                case CosmosRecheckKind.All:
                    foreach (var operand in conjunct.Operands)
                        if (True(operand, row) == false)
                            return false;

                    return true;

                case CosmosRecheckKind.Any:
                    foreach (var operand in conjunct.Operands)
                        if (True(operand, row))
                            return true;

                    return false;

                case CosmosRecheckKind.Predicate:
                    return CosmosKeyComparer.Member(row, conjunct.Member!).ValueKind == JsonValueKind.True;

                default:
                {
                    var comparison = _program.Comparisons[conjunct.Comparison];
                    var value = _compared[conjunct.Comparison];
                    var held = CosmosKeyComparer.Member(row, comparison.Member!);

                    if (CosmosKeyComparer.IsValue(held, CosmosOrderedKeyKind.Text) == false || CosmosKeyComparer.IsValue(value, CosmosOrderedKeyKind.Text) == false)
                        return false;

                    var c = CosmosKeyComparer.CompareValues(held, value, CosmosOrderedKeyKind.Text);
                    return comparison.Operator switch
                    {
                        ">" => c > 0,
                        ">=" => c >= 0,
                        "<" => c < 0,
                        _ => c <= 0,
                    };
                }
            }
        }

        /// <summary>
        /// Reads the page the service took, and passes it on sorted where its edges certify it; otherwise reads the
        /// fallback.
        /// </summary>
        async ValueTask WindowAsync(CancellationToken cancellationToken)
        {
            var rows = new List<JsonElement>();
            while (await _source!.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(_source.Current);

            var certified = rows.Count == 0
                || (_offset == 0 || _comparer.IsAnchor(rows[0]))
                && (_fetch is not long fetch || rows.Count < fetch || _comparer.IsAnchor(rows[^1]))
                && (_program.Distinct == false || Distinct(rows));

            if (certified == false)
            {
                await ReissueAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var row in rows)
                if (_comparer.IsAnchor(row) == false)
                    _buffered++;

            rows.Sort(_comparer);
            foreach (var row in rows)
                _ready.Enqueue(row);

            _exhausted = true;

            static bool Distinct(List<JsonElement> rows)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                    if (seen.Add(Identity(row)) == false)
                        return false;

                return true;
            }
        }

        /// <summary>
        /// Says what a row is, for telling a repeat of a <c>DISTINCT</c>'s from another row: the bytes the service sent,
        /// one character each.
        /// </summary>
        /// <remarks>
        /// The same equality as the row's text, the bytes being UTF-8 wherever there is text, and the one that holds
        /// where they are not: decoding a row to tell it apart failed, unnamed, over text a client wrote that is not
        /// UTF-8 — before the row's reader could refuse it by name (<see cref="CosmosJson.Undecodable"/>).
        /// </remarks>
        static string Identity(JsonElement row) =>
            System.Text.Encoding.Latin1.GetString(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(row));

        /// <summary>
        /// Reads the aggregate's row, and replaces each extreme that is no anchor by its argument's extreme read again.
        /// </summary>
        async ValueTask ExtremesAsync(CancellationToken cancellationToken)
        {
            var rows = new List<JsonElement>();
            while (await _source!.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(_source.Current);

            for (var i = 0; i < rows.Count; i++)
                foreach (var extreme in _program.Extremes)
                {
                    var answered = CosmosKeyComparer.Member(rows[i], extreme.Member);
                    if (CosmosKeyComparer.IsValue(answered, extreme.Kind) == false || CosmosServiceOrder.IsAnchor(extreme.Anchor, answered))
                        continue;

                    _activity?.SetTag("cosmos.order.reissued", true);
                    rows[i] = Replace(rows[i], extreme.Member, await RereadAsync(extreme, cancellationToken).ConfigureAwait(false));
                }

            foreach (var row in rows)
                _ready.Enqueue(row);

            _exhausted = true;
        }

        /// <summary>
        /// Reads an extreme's argument again and returns Calcite's extreme of it: of every value, or, where the values come
        /// in their order toward the extreme, of those up to the first anchor, past which none lies beyond it.
        /// </summary>
        async ValueTask<JsonElement> RereadAsync(CosmosOrderExtreme extreme, CancellationToken cancellationToken)
        {
            using var values = await OpenAsync(extreme.Reread!, null, cancellationToken).ConfigureAwait(false);

            var best = default(JsonElement);
            var any = false;

            while (await values.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = values.Current;
                if (CosmosKeyComparer.IsValue(value, extreme.Kind) == false)
                    continue;

                var c = any ? CosmosKeyComparer.CompareValues(value, best, extreme.Kind) : 0;
                if (any == false || (extreme.Max ? c > 0 : c < 0))
                {
                    best = value;
                    any = true;
                }

                if (extreme.Ordered && CosmosServiceOrder.IsAnchor(extreme.Anchor, value))
                    break;

                if (CosmosServiceOrder.IsAnchor(extreme.Anchor, value) == false)
                    _buffered++;
            }

            return best;
        }

        /// <summary>
        /// Returns a row with one member's value replaced, or left out where the value is undefined.
        /// </summary>
        /// <remarks>
        /// The values are copied as the service sent them, byte for byte. Written out instead, a value holding text a
        /// client wrote that is not UTF-8 came back with each such byte an escaped U+FFFD, and the row's reader read the
        /// replacement without a word, where every other reading refuses the bytes by name
        /// (<see cref="CosmosJson.Undecodable"/>). The names are the statement's own.
        /// </remarks>
        static JsonElement Replace(JsonElement row, string member, JsonElement value)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                foreach (var property in row.EnumerateObject())
                    if (property.NameEquals(member) == false)
                    {
                        writer.WritePropertyName(property.Name);
                        writer.WriteRawValue(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(property.Value), skipInputValidation: true);
                    }

                if (value.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName(member);
                    writer.WriteRawValue(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value), skipInputValidation: true);
                }

                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }

        /// <summary>
        /// Records what the reader did, once the rows are out.
        /// </summary>
        bool Finish()
        {
            _finished = true;
            _activity?.SetTag("cosmos.order.buffered", _buffered);
            return false;
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            _source?.Dispose();
            _source = null;

            _activity?.SetTag("cosmos.order.buffered", _buffered);
            _activity?.Dispose();
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (_source is not null)
                await _source.DisposeAsync().ConfigureAwait(false);

            _source = null;

            _activity?.SetTag("cosmos.order.buffered", _buffered);
            _activity?.Dispose();
        }

    }

}
