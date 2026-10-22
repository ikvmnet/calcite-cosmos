using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The check run's verdicts by content (<see cref="RenderingMemoKey"/>): each computed once, however many statements and
    /// threads ask, and named by an entry number every verdict reported from it carries.
    /// </summary>
    /// <param name="keyOf">How a key is filed; the plant "a memo keyed without the claim" files each under <c>Equal</c>.</param>
    internal sealed class RenderingMemo(Func<RenderingMemoKey, RenderingMemoKey>? keyOf = null)
    {

        static readonly ConditionalWeakTable<RenderingTheory, string> Digests = new();

        readonly ConcurrentDictionary<RenderingMemoKey, Lazy<Entry>> _entries = new();
        int _next;

        /// <summary>Gets every entry, in the order they were numbered.</summary>
        public IReadOnlyList<Entry> Entries => [.. _entries.Values.Where(e => e.IsValueCreated).Select(e => e.Value).OrderBy(e => e.Id)];

        /// <summary>Gets the entry filed under a key, computing it the first time it is asked.</summary>
        public Entry GetOrAdd(RenderingMemoKey key, Func<int, Entry> compute) =>
            _entries.GetOrAdd(keyOf?.Invoke(key) ?? key, _ => new Lazy<Entry>(() => compute(Interlocked.Increment(ref _next)))).Value;

        /// <summary>
        /// The frozen theory's rows, hashed (Appendix E): generations defined alike share entries and a planted row shares
        /// none. A primitive is no row, so a plant of one keeps a memo of its own.
        /// </summary>
        public static string DigestOf(RenderingTheory theory) => Digests.GetValue(theory, t => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", t.Graph.Tables.SelectMany(t.Theory.Rows).Select(row => row.ToString()).Order(StringComparer.Ordinal))))));

        /// <summary>
        /// One check: the claim's verdict under the asserted set and under the set beside it, each with the
        /// run it was read off; each checked conjunct's verdict at its own position in the first; and the engine failure
        /// either bundle declined on, which the discipline line refuses; and what the first claimed and imported.
        /// </summary>
        public sealed record Entry(int Id, RenderingMemoKey Key, RenderingVerdict Asserted, RenderingRun AssertedRun, RenderingVerdict Beside,
            RenderingRun BesideRun, IReadOnlyList<RenderingVerdict?> Alone, Exception? Failure, Claimed? What = null);

        /// <summary>
        /// What a check claimed of which conjuncts, over what its bundle imported, each path by its spelling, and the value
        /// each refuting cell puts at its path, <c>null</c> for none: what source A samples, and a witness is made of.
        /// </summary>
        public sealed record Claimed(IReadOnlyList<SqlPredicate> Conjuncts, ServiceForm Form, IReadOnlyList<ServiceForm?> Members,
            IReadOnlyDictionary<string, Imported> Imports, IReadOnlyDictionary<string, string?>? Witness);

        /// <summary>One path's import, and what the stratum it was rendered from says it may hold.</summary>
        public sealed record Imported(PathValues Values, ValueSet<string> Chars, PathValues Assumed);

    }

}
