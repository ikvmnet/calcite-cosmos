using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The check run's verdicts by content (<see cref="RenderingMemoKey"/>): each computed once, however many statements and
    /// threads ask, and named by an entry number every verdict reported from it carries.
    /// </summary>
    /// <param name="keyOf">How a key is filed; the plant "a memo keyed without the claim" files each under <c>Equal</c>.</param>
    internal sealed class RenderingMemo(Func<RenderingMemoKey, RenderingMemoKey>? keyOf = null)
    {

        readonly ConcurrentDictionary<RenderingMemoKey, Lazy<Entry>> _entries = new();
        int _next;

        /// <summary>Gets every entry, in the order they were numbered.</summary>
        public IReadOnlyList<Entry> Entries => [.. _entries.Values.Where(e => e.IsValueCreated).Select(e => e.Value).OrderBy(e => e.Id)];

        /// <summary>Gets the entry filed under a key, computing it the first time it is asked.</summary>
        public Entry GetOrAdd(RenderingMemoKey key, Func<int, Entry> compute) =>
            _entries.GetOrAdd(keyOf?.Invoke(key) ?? key, _ => new Lazy<Entry>(() => compute(Interlocked.Increment(ref _next)))).Value;

        /// <summary>
        /// One check: the claim's verdict under the asserted set and under <see cref="RenderingSet.Portable"/>, each with the
        /// run it was read off; each conjunct's verdict at its own position in the first; and the import.
        /// </summary>
        public sealed record Entry(int Id, RenderingMemoKey Key, RenderingVerdict Asserted, RenderingRun AssertedRun, RenderingVerdict Portable,
            RenderingRun PortableRun, IReadOnlyList<RenderingVerdict> Alone, ImportReport Import);

    }

}
