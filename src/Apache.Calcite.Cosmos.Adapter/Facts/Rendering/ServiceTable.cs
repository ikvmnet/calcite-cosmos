using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A set's table as the evaluation primitives read it: its entries, indexed by key; the classes S placed by their order's
    /// readers (<see cref="RenderingTheory.Placed"/>); the keys S read a comparison of two paths by, in a table of their
    /// own (G3); and what the set's <c>LIKE</c> reads each unit of a pattern as, by its row (<see cref="RenderingTheory.LikesOf"/>,
    /// G7); equal by content, since Eggnog interns a value by its equality. S views each set's once it is settled
    /// (<see cref="RenderingTheory.View"/>), and the theory holds it.
    /// </summary>
    internal sealed class ServiceTable : IEquatable<ServiceTable>
    {

        readonly Dictionary<MeasuredKey, MeasuredEntry> _index;
        readonly Dictionary<MeasuredKey, MeasuredEntry> _pairs;

        ServiceTable(ValueSet<MeasuredEntry> entries, ValueSet<CosmosValueClass> placed, ValueSet<MeasuredEntry> pairs, ValueSet<(LikeUnit, string)> likes)
        {
            Entries = entries;
            Placed = placed;
            Pairs = pairs;
            Likes = likes;
            LikeUnits = UnitsOf(likes);
            _index = entries.ToDictionary(entry => entry.Key);
            _pairs = pairs.ToDictionary(entry => entry.Key);
        }

        /// <summary>Every entry, one a key.</summary>
        public ValueSet<MeasuredEntry> Entries { get; }

        /// <summary>The classes whose comparisons are placed by their order entry's readers, as S derived them.</summary>
        public ValueSet<CosmosValueClass> Placed { get; }

        /// <summary>Every key of a comparison of two paths S read off the rows, one a key: the pair table (G3).</summary>
        public ValueSet<MeasuredEntry> Pairs { get; }

        /// <summary>What the set's <c>LIKE</c> reads each unit of a pattern as, each beside its row.</summary>
        public ValueSet<(LikeUnit, string)> Likes { get; }

        /// <summary>What the set's <c>LIKE</c> reads each unit of a pattern as (<see cref="LikePattern.Read"/>).</summary>
        public ValueSet<LikeUnit> LikeUnits { get; }

        /// <summary>
        /// The view of a set's entries, the placed classes, the pair table and the units its <c>LIKE</c> reads; two entries
        /// for one key throw.
        /// </summary>
        public static ServiceTable Of(ValueSet<MeasuredEntry> entries, ValueSet<CosmosValueClass> placed, ValueSet<MeasuredEntry> pairs, ValueSet<(LikeUnit, string)> likes) =>
            new(entries, placed, pairs, likes);

        /// <summary>The view of a set's entries, the placed classes and the pair table, its <c>LIKE</c> reading no unit.</summary>
        public static ServiceTable Of(ValueSet<MeasuredEntry> entries, ValueSet<CosmosValueClass> placed, ValueSet<MeasuredEntry> pairs) =>
            Of(entries, placed, pairs, ValueSet<(LikeUnit, string)>.Empty);

        /// <summary>The units of a set's rows of <c>LIKE</c>, apart from their rows.</summary>
        public static ValueSet<LikeUnit> UnitsOf(ValueSet<(LikeUnit, string)> likes) => ValueSet.From(likes.AsEnumerable().Select(l => l.Item1));

        /// <summary>The rows the units a pattern was read by rest on, added to the rows.</summary>
        public void Cite(LikePattern pattern, ISet<string> rows) =>
            rows.UnionWith(Likes.AsEnumerable().Where(l => pattern.Rows.Contains(l.Item1)).Select(l => l.Item2));

        /// <summary>The view of a set's entries and the placed classes, with no pair read.</summary>
        public static ServiceTable Of(ValueSet<MeasuredEntry> entries, ValueSet<CosmosValueClass> placed) => Of(entries, placed, ValueSet<MeasuredEntry>.Empty);

        public bool TryGet(MeasuredKey key, [MaybeNullWhen(false)] out MeasuredEntry entry) => _index.TryGetValue(key, out entry);

        /// <summary>
        /// The outcomes a key may give, its evidence added to the rows; and every outcome, a failure included, where no
        /// entry says one, the key named among the rows as missing.
        /// </summary>
        public ServiceOutcomes Outcomes(MeasuredKey key, ISet<string> rows) => Outcomes(_index, key, rows);

        /// <summary>The outcomes a pair key may give, as <see cref="Outcomes(MeasuredKey, ISet{string})"/> does of a key of the table.</summary>
        public ServiceOutcomes PairOutcomes(MeasuredKey key, ISet<string> rows) => Outcomes(_pairs, key, rows);

        /// <summary>
        /// The entry of a key with the answers S's roles rules found read it (<see cref="MeasuredEntry.Readers"/>), its evidence
        /// added to the rows: what a leaf places or matches a cell by. None where S derived no reader, as for a key no entry
        /// holds, which is named among the rows as missing.
        /// </summary>
        public MeasuredEntry? Read(MeasuredKey key, ISet<string> rows)
        {
            if (TryGet(key, out var entry) == false)
            {
                rows.Add("missing:" + key);
                return null;
            }

            rows.UnionWith(entry.Evidence);
            return entry.Readers.IsEmpty ? null : entry;
        }

        static ServiceOutcomes Outcomes(Dictionary<MeasuredKey, MeasuredEntry> index, MeasuredKey key, ISet<string> rows)
        {
            if (index.TryGetValue(key, out var entry) && entry.Outcomes != ServiceOutcomes.None)
            {
                rows.UnionWith(entry.Evidence);
                return entry.Outcomes;
            }

            rows.Add("missing:" + key);
            return ServiceOutcomes.Widest;
        }

        /// <inheritdoc />
        public bool Equals(ServiceTable? other) => other is not null && Entries.Equals(other.Entries) && Placed.Equals(other.Placed) && Pairs.Equals(other.Pairs) && Likes.Equals(other.Likes);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as ServiceTable);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Entries, Placed, Pairs, Likes);

    }

}
