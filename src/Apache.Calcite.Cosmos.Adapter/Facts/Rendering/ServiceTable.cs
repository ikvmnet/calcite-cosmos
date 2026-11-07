using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A set's table as the evaluation primitives read it: its entries, indexed by key; the classes S placed by their order's
    /// readers (<see cref="RenderingTheory.Placed"/>); and the keys S read a comparison of two paths by, in a table of their
    /// own (G3); equal by content, since Eggnog interns a value by its equality.
    /// </summary>
    internal sealed class ServiceTable : IEquatable<ServiceTable>
    {

        /// <summary>The view of each set of entries beside each set of placed classes and each pair table, built once.</summary>
        static readonly ConditionalWeakTable<ValueSet<MeasuredEntry>, ConditionalWeakTable<ValueSet<CosmosValueClass>, ConditionalWeakTable<ValueSet<MeasuredEntry>, ServiceTable>>> Views = new();

        readonly Dictionary<MeasuredKey, MeasuredEntry> _index;
        readonly Dictionary<MeasuredKey, MeasuredEntry> _pairs;

        ServiceTable(ValueSet<MeasuredEntry> entries, ValueSet<CosmosValueClass> placed, ValueSet<MeasuredEntry> pairs)
        {
            Entries = entries;
            Placed = placed;
            Pairs = pairs;
            _index = entries.ToDictionary(entry => entry.Key);
            _pairs = pairs.ToDictionary(entry => entry.Key);
        }

        /// <summary>Every entry, one a key.</summary>
        public ValueSet<MeasuredEntry> Entries { get; }

        /// <summary>The classes whose comparisons are placed by their order entry's readers, as S derived them.</summary>
        public ValueSet<CosmosValueClass> Placed { get; }

        /// <summary>Every key of a comparison of two paths S read off the rows, one a key: the pair table (G3).</summary>
        public ValueSet<MeasuredEntry> Pairs { get; }

        /// <summary>The view of a set's entries, the placed classes and the pair table; two entries for one key throw.</summary>
        public static ServiceTable Of(ValueSet<MeasuredEntry> entries, ValueSet<CosmosValueClass> placed, ValueSet<MeasuredEntry> pairs) =>
            Views.GetValue(entries, _ => new()).GetValue(placed, _ => new()).GetValue(pairs, q => new ServiceTable(entries, placed, q));

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
        public bool Equals(ServiceTable? other) => other is not null && Entries.Equals(other.Entries) && Placed.Equals(other.Placed) && Pairs.Equals(other.Pairs);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as ServiceTable);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Entries, Placed, Pairs);

    }

}
