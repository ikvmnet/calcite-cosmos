using System;
using System.Collections.Immutable;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Every reading's rows, reading by reading (<see cref="ReadingRows"/>, G7): S's <c>readerrows</c>, each row joined in by
    /// <see cref="Union"/>, which the pairing rule binds beside the raw union, so Calcite's side of a cell reads its
    /// atom's readings in it, as it reads its keys in the union.
    /// </summary>
    internal sealed class ReadingTable : IEquatable<ReadingTable>
    {

        /// <summary>The readings' rows joined reading by reading.</summary>
        public static readonly ValueLattice<ReadingTable> Union = new("reading-union", (a, b) => a.Join(b));

        /// <summary>No reading's rows: what <c>readerrows</c> starts as.</summary>
        public static readonly ReadingTable Empty = new(ImmutableSortedDictionary.Create<string, ReadingRows>(StringComparer.Ordinal));

        readonly ImmutableSortedDictionary<string, ReadingRows> _rows;

        ReadingTable(ImmutableSortedDictionary<string, ReadingRows> rows) => _rows = rows;

        static ReadingTable Of(string reader, ReadingRows rows) => new(Empty._rows.Add(reader, rows));

        /// <summary>A row of a reading, as the rule joins it in.</summary>
        public static ReadingTable Read(string reader, CosmosValueClass kind, string answer) => Of(reader, ReadingRows.Read(kind, answer));

        /// <summary>
        /// A row of a raw column's reading, as the rule joins it in: the reading through that column's (rawtyped), and the
        /// reading through every column's, which reads what each does.
        /// </summary>
        public static ReadingTable Raw(string reader, string column, CosmosValueClass kind, string answer) =>
            new(Empty._rows.Add(reader, ReadingRows.Read(kind, answer)).Add(PredicateAtom.InColumn(reader, column), ReadingRows.Read(kind, answer)));

        /// <summary>The classes a reading's parse was measured over.</summary>
        public static ReadingTable Measured(string reader, ValueSet<string> bands) => Of(reader, ReadingRows.Measured(bands));

        /// <summary>A reading evaluated as another.</summary>
        public static ReadingTable Evaluated(string reader, string evaluated) => Of(reader, ReadingRows.Evaluated(evaluated));

        /// <summary>A reading's rows; none where no row names it.</summary>
        public ReadingRows this[string reader] => _rows.TryGetValue(reader, out var rows) ? rows : ReadingRows.None;

        /// <summary>How many readings a row names.</summary>
        public int Count => _rows.Count;

        ReadingTable Join(ReadingTable other)
        {
            var (into, from) = _rows.Count >= other._rows.Count ? (this, other) : (other, this);
            var joined = into._rows;
            foreach (var (reader, rows) in from._rows)
                joined = joined.SetItem(reader, joined.TryGetValue(reader, out var held) ? ReadingRows.Join(held, rows) : rows);

            return joined.Count == into._rows.Count && joined.All(r => into._rows[r.Key] == r.Value) ? into : new(joined);
        }

        public bool Equals(ReadingTable? other) => other is not null && (ReferenceEquals(this, other) || _rows.Count == other._rows.Count
            && _rows.All(r => other._rows.TryGetValue(r.Key, out var rows) && rows == r.Value));

        public override bool Equals(object? obj) => Equals(obj as ReadingTable);

        public override int GetHashCode() => _rows.Aggregate(0, (h, r) => h + HashCode.Combine(r.Key, r.Value));

        /// <summary>Each reading and its rows, in reading order, as the theory's digest reads a value.</summary>
        public override string ToString() => string.Join(";", _rows.Select(r => r.Key + "=" + r.Value));

    }

}
