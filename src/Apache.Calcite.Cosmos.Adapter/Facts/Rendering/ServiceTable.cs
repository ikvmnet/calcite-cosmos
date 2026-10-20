using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A set's table as the evaluation primitives read it: its entries, indexed by key.
    /// </summary>
    /// <remarks>
    /// <b>Equal by content.</b> Eggnog interns a value by its equality, so two tables equal by anything less would be one
    /// value, and a bundle could read another set's rows.
    /// </remarks>
    internal sealed class ServiceTable : IEquatable<ServiceTable>
    {

        /// <summary>The view of each set of entries, built once.</summary>
        static readonly ConditionalWeakTable<ValueSet<MeasuredEntry>, ServiceTable> Views = new();

        readonly Dictionary<MeasuredKey, MeasuredEntry> _index;

        ServiceTable(ValueSet<MeasuredEntry> entries)
        {
            Entries = entries;
            _index = entries.ToDictionary(entry => entry.Key);
        }

        /// <summary>Every entry, one a key.</summary>
        public ValueSet<MeasuredEntry> Entries { get; }

        /// <summary>The view of a set's entries; two entries for one key throw.</summary>
        public static ServiceTable Of(ValueSet<MeasuredEntry> entries) => Views.GetValue(entries, e => new ServiceTable(e));

        /// <summary>Looks a key's entry up.</summary>
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
        public bool Equals(ServiceTable? other) => other is not null && Entries.Equals(other.Entries);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as ServiceTable);

        /// <inheritdoc />
        public override int GetHashCode() => Entries.GetHashCode();

    }

}
