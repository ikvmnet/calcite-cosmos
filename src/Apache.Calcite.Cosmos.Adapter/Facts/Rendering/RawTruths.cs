using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The raw columns' answers, key by key: what Calcite may answer for a raw reading's test over a value of a kind, the
    /// truths S's rules state whole and those a comparison on the cell decides (<see cref="RawAnswer"/>, h12, G5). S's
    /// <c>rawunion</c> holds it, each column's answer joined in by <see cref="Union"/>, so the union across columns is
    /// Eggnog's join; a primitive reads the cell's key in it.
    /// </summary>
    internal sealed class RawTruths : IEquatable<RawTruths>
    {

        /// <summary>An answer's union, the join each key's answers take.</summary>
        public static readonly ValueLattice<RawAnswer> AnswerUnion = new("raw-answer-union", RawAnswer.Join);

        /// <summary>The keys' answers joined key by key, a key one side lacks taking the other's.</summary>
        public static readonly ValueLattice<RawTruths> Union = new("raw-union", (a, b) => a.Join(b));

        /// <summary>No column's truths: what <c>rawunion</c> starts as.</summary>
        public static readonly RawTruths Empty = new(ImmutableSortedDictionary<RawKey, RawAnswer>.Empty, 0);

        readonly ImmutableSortedDictionary<RawKey, RawAnswer> _truths;
        readonly int _hash;

        // The hash is the entries' hashes summed, so a join updates it by the keys it changes.
        RawTruths(ImmutableSortedDictionary<RawKey, RawAnswer> truths, int hash)
        {
            _truths = truths;
            _hash = hash;
        }

        static int HashOf(RawKey key, RawAnswer answer) => HashCode.Combine(key, answer);

        /// <summary>One column's answer on a key, as the rule joins it in.</summary>
        public static RawTruths Of(RawKey key, RawAnswer answer) => new(ImmutableSortedDictionary<RawKey, RawAnswer>.Empty.Add(key, answer), HashOf(key, answer));

        /// <summary>
        /// One column's answer on a key, as the rule joins it in: under the union's key, beside the other columns', and
        /// under the key of the reading through that column alone (rawtyped).
        /// </summary>
        public static RawTruths Of(RawKey key, string column, RawAnswer answer) =>
            new(ImmutableSortedDictionary<RawKey, RawAnswer>.Empty.Add(key, answer).Add(key.In(column), answer), HashOf(key, answer) + HashOf(key.In(column), answer));

        /// <summary>The answer on a key; the lattice's top, anything and a raise, where no column answers for it.</summary>
        public RawAnswer this[RawKey key] => _truths.TryGetValue(key, out var answer) ? answer : RawAnswer.Unanswered;

        /// <summary>How many keys a column answers for.</summary>
        public int Count => _truths.Count;

        /// <summary>Each key and its answer, in key order.</summary>
        public IEnumerable<KeyValuePair<RawKey, RawAnswer>> Entries => _truths;

        RawTruths Join(RawTruths other)
        {
            var (into, from) = _truths.Count >= other._truths.Count ? (this, other) : (other, this);
            var (joined, hash) = (into._truths, into._hash);
            foreach (var (key, truths) in from._truths)
            {
                var held = joined.TryGetValue(key, out var h) ? h : (RawAnswer?)null;
                var merged = held is { } t ? AnswerUnion.Merge(t, truths) : truths;
                if (merged == held)
                    continue;

                (joined, hash) = (joined.SetItem(key, merged), hash - (held is { } o ? HashOf(key, o) : 0) + HashOf(key, merged));
            }

            return joined == into._truths ? into : new(joined, hash);
        }

        public bool Equals(RawTruths? other) => other is not null && (ReferenceEquals(this, other) || _hash == other._hash && _truths.Count == other._truths.Count
            && _truths.All(t => other._truths.TryGetValue(t.Key, out var truths) && truths == t.Value));

        public override bool Equals(object? obj) => Equals(obj as RawTruths);

        public override int GetHashCode() => _hash;

        /// <summary>Each key and its answer, in key order, as the theory's digest reads a row.</summary>
        public override string ToString() => string.Join(";", _truths.Select(t => t.Key + "=" + t.Value));

    }

}
