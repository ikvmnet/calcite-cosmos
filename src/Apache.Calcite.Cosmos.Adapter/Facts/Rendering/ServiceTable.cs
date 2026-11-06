using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A set's table as the evaluation primitives read it: its entries, indexed by key, and the classes S placed by their
    /// order's comparators (<see cref="RenderingTheory.Placed"/>); equal by content, since Eggnog interns a value by its
    /// equality.
    /// </summary>
    internal sealed class ServiceTable : IEquatable<ServiceTable>
    {

        /// <summary>The view of each set of entries beside each set of placed classes, built once.</summary>
        static readonly ConditionalWeakTable<ValueSet<MeasuredEntry>, ConditionalWeakTable<ValueSet<CosmosValueClass>, ServiceTable>> Views = new();

        readonly Dictionary<MeasuredKey, MeasuredEntry> _index;

        ServiceTable(ValueSet<MeasuredEntry> entries, ValueSet<CosmosValueClass> placed)
        {
            Entries = entries;
            Placed = placed;
            _index = entries.ToDictionary(entry => entry.Key);
        }

        /// <summary>Every entry, one a key.</summary>
        public ValueSet<MeasuredEntry> Entries { get; }

        /// <summary>The classes whose comparisons are placed by their order entry's comparators, as S derived them.</summary>
        public ValueSet<CosmosValueClass> Placed { get; }

        /// <summary>The view of a set's entries and the placed classes; two entries for one key throw.</summary>
        public static ServiceTable Of(ValueSet<MeasuredEntry> entries, ValueSet<CosmosValueClass> placed) =>
            Views.GetValue(entries, _ => new()).GetValue(placed, p => new ServiceTable(entries, p));

        public bool TryGet(MeasuredKey key, [MaybeNullWhen(false)] out MeasuredEntry entry) => _index.TryGetValue(key, out entry);

        /// <summary>
        /// The outcomes a key may give, its evidence added to the rows; and every outcome, a failure included, where no
        /// entry says one, the key named among the rows as missing.
        /// </summary>
        public ServiceOutcomes Outcomes(MeasuredKey key, ISet<string> rows)
        {
            if (TryGet(key, out var entry) && entry.Outcomes != ServiceOutcomes.None)
            {
                rows.UnionWith(entry.Evidence);
                return entry.Outcomes;
            }

            rows.Add("missing:" + key);
            return ServiceOutcomes.Widest;
        }

        /// <inheritdoc />
        public bool Equals(ServiceTable? other) => other is not null && Entries.Equals(other.Entries) && Placed.Equals(other.Placed);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as ServiceTable);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Entries, Placed);

    }

}
