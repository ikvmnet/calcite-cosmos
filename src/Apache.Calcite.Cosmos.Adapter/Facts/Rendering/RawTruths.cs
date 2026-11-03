using System;
using System.Collections.Immutable;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The raw columns' truths, key by key: what Calcite may answer for a raw reading's test over a value of a kind, where
    /// a column comparing the value as its type answers TRUE or FALSE (h12). S's <c>rawunion</c> holds it, each column's
    /// row joined in by <see cref="Union"/>, so the union across columns is Eggnog's join; a primitive reads it on a cell.
    /// </summary>
    internal sealed class RawTruths : IEquatable<RawTruths>
    {

        /// <summary>A truth's union, the join each key's truths take.</summary>
        public static readonly ValueLattice<Truths> TruthsUnion = new("truths-union", (a, b) => a | b);

        /// <summary>The keys' truths joined key by key, a key one side lacks taking the other's.</summary>
        public static readonly ValueLattice<RawTruths> Union = new("raw-union", (a, b) => a.Join(b));

        /// <summary>No column's truths: what <c>rawunion</c> starts as.</summary>
        public static readonly RawTruths Empty = new(ImmutableSortedDictionary<RawKey, Truths>.Empty, 0);

        readonly ImmutableSortedDictionary<RawKey, Truths> _truths;
        readonly int _hash;

        // The hash is the entries' hashes summed, so a join updates it by the keys it changes.
        RawTruths(ImmutableSortedDictionary<RawKey, Truths> truths, int hash)
        {
            _truths = truths;
            _hash = hash;
        }

        static int HashOf(RawKey key, Truths truths) => HashCode.Combine(key, truths);

        /// <summary>One column's truths on a key, as the rule joins it in.</summary>
        public static RawTruths Of(RawKey key, Truths truths) => new(ImmutableSortedDictionary<RawKey, Truths>.Empty.Add(key, truths), HashOf(key, truths));

        /// <summary>The truths on a key; anything, a raise among it, where no column answers for it.</summary>
        public Truths this[RawKey key] => _truths.TryGetValue(key, out var truths) ? truths : Truths.Any | Truths.Raises;

        /// <summary>How many keys a column answers for.</summary>
        public int Count => _truths.Count;

        RawTruths Join(RawTruths other)
        {
            var (into, from) = _truths.Count >= other._truths.Count ? (this, other) : (other, this);
            var (joined, hash) = (into._truths, into._hash);
            foreach (var (key, truths) in from._truths)
            {
                var held = joined.TryGetValue(key, out var h) ? h : (Truths?)null;
                var merged = held is { } t ? TruthsUnion.Merge(t, truths) : truths;
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

        /// <summary>Each key and its truths, in key order, as the theory's digest reads a row.</summary>
        public override string ToString() => string.Join(";", _truths.Select(t => t.Key + "=" + t.Value));

    }

}
