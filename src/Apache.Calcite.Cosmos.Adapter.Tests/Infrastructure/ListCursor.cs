using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Extensions.Runtime;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// Canned rows as a cursor, recording the token of every advance.
    /// </summary>
    /// <remarks>
    /// What the stub executors answer with, and what a test hands the runtime as an input. An awaited
    /// advance yields before it answers, so the code above is exercised against a genuinely asynchronous
    /// cursor rather than one that happens to complete synchronously; a synchronous advance does not,
    /// having nowhere to suspend.
    /// </remarks>
    sealed class ListCursor<T> : ClrCursor<T>
    {

        readonly IReadOnlyList<T> _items;
        int _index = -1;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ListCursor(IEnumerable<T> items)
        {
            _items = items.ToList();
        }

        /// <summary>
        /// The token each awaited advance was given, in order.
        /// </summary>
        public List<CancellationToken> Tokens { get; } = new();

        /// <summary>
        /// Whether the cursor has been disposed.
        /// </summary>
        public bool Disposed { get; private set; }

        /// <inheritdoc />
        public override T Current => _items[_index];

        /// <inheritdoc />
        public override bool Read()
        {
            if (_index + 1 >= _items.Count)
            {
                _index = _items.Count;
                return false;
            }

            _index++;
            return true;
        }

        /// <inheritdoc />
        public override async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            return Read();
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            Disposed = true;
        }

    }

    /// <summary>
    /// Builds the cursors the stub executors answer with.
    /// </summary>
    static class ListCursor
    {

        /// <summary>
        /// Parses each document into the standalone value an executor yields.
        /// </summary>
        public static ListCursor<JsonElement> Documents(IEnumerable<string> documents)
        {
            return new ListCursor<JsonElement>(documents.Select(d => JsonDocument.Parse(d).RootElement.Clone()));
        }

        /// <summary>
        /// Reads a cursor to its end with awaited advances, and disposes it.
        /// </summary>
        public static async Task<List<T>> CollectAsync<T>(IClrCursor<T> cursor, CancellationToken cancellationToken = default)
        {
            var rows = new List<T>();

            await using (cursor)
                while (await cursor.ReadAsync(cancellationToken))
                    rows.Add(cursor.Current);

            return rows;
        }

    }

}
