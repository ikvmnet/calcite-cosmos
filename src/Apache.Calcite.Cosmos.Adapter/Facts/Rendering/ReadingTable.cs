using System;
using System.Collections.Immutable;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Every reading's rows, reading by reading (<see cref="ReadingRows"/>, G7): the readings vocabulary's <c>readerrows</c>
    /// (<see cref="ReadingVocabulary"/>), each row joined in by <see cref="Union"/>, which S's pairing rule binds beside the
    /// raw union and the facts graph's atom rules beside an atom, so a cell and the facts graph read an atom's readings in one
    /// table; what <c>LIKE</c> reads each unit of a pattern as, which every reading's match reads; and the rows each type
    /// test rests on, by its name.
    /// </summary>
    internal sealed class ReadingTable : IEquatable<ReadingTable>
    {

        /// <summary>The readings' rows joined reading by reading.</summary>
        public static readonly ValueLattice<ReadingTable> Union = new("reading-union", (a, b) => a.Join(b));

        /// <summary>No reading's rows: what <c>readerrows</c> starts as.</summary>
        public static readonly ReadingTable Empty = new(ImmutableSortedDictionary.Create<string, ReadingRows>(StringComparer.Ordinal), ValueSet<LikeUnit>.Empty, ValueSet<string>.Empty,
            ImmutableSortedDictionary.Create<string, ValueSet<string>>(StringComparer.Ordinal));

        readonly ImmutableSortedDictionary<string, ReadingRows> _rows;

        readonly ImmutableSortedDictionary<string, ValueSet<string>> _tests;

        ReadingTable(ImmutableSortedDictionary<string, ReadingRows> rows, ValueSet<LikeUnit> units, ValueSet<string> unitRows, ImmutableSortedDictionary<string, ValueSet<string>> tests) =>
            (_rows, Units, UnitRows, _tests) = (rows, units, unitRows, tests);

        ReadingTable(ImmutableSortedDictionary<string, ReadingRows> rows) : this(rows, ValueSet<LikeUnit>.Empty, ValueSet<string>.Empty, Empty._tests) { }

        static ReadingTable Of(string reader, ReadingRows rows) => new(Empty._rows.Add(reader, rows));

        /// <summary>What <c>LIKE</c> reads each unit of a pattern as (<c>likeunits</c>); none where no row says, so no pattern is decided.</summary>
        public ValueSet<LikeUnit> Units { get; }

        /// <summary>The rows <see cref="Units"/> rest on.</summary>
        public ValueSet<string> UnitRows { get; }

        /// <summary>What <c>LIKE</c> reads a unit of its pattern as, as its row says, which the rule joins in.</summary>
        public static ReadingTable Unit(LikeUnit unit, string row) => new(Empty._rows, ValueSet.Of(unit), ValueSet.Of(row), Empty._tests);

        /// <summary>A row a type test rests on, by its name, as the rule joins it in.</summary>
        public static ReadingTable Tested(string test, string row) => new(Empty._rows, ValueSet<LikeUnit>.Empty, ValueSet<string>.Empty, Empty._tests.Add(test, ValueSet.Of(row)));

        /// <summary>The rows a type test rests on, by its name; none where no row says what it is TRUE of.</summary>
        public ValueSet<string> TestRows(string test) => _tests.TryGetValue(test, out var rows) ? rows : ValueSet<string>.Empty;

        /// <summary>A row of a reading, as the rule joins it in.</summary>
        public static ReadingTable Read(string reader, CosmosValueClass kind, string answer) => Of(reader, ReadingRows.Read(kind, answer));

        /// <summary>
        /// A row of a raw column's reading, as the rule joins it in: the reading through that column's (rawtyped), and the
        /// reading through every column's, which reads what each does.
        /// </summary>
        public static ReadingTable Raw(string reader, string column, CosmosValueClass kind, string answer) =>
            new(Empty._rows.Add(reader, ReadingRows.Read(kind, answer)).Add(PredicateAtom.InColumn(reader, column), ReadingRows.Read(kind, answer)));

        /// <summary>The character classes a reading's parse of a class was measured over.</summary>
        public static ReadingTable Measured(string reader, CosmosValueClass kind, ValueSet<string> bands) => Of(reader, ReadingRows.Measured(kind, bands));

        /// <summary>A reading evaluated as another over a class.</summary>
        public static ReadingTable Evaluated(string reader, CosmosValueClass kind, string evaluated) => Of(reader, ReadingRows.Evaluated(kind, evaluated));

        /// <summary>What a test of a reading answers over a class whatever the value.</summary>
        public static ReadingTable Answering(string reader, CosmosValueClass kind, CosmosServiceTest test, Truths truths) => Of(reader, ReadingRows.Answering(kind, test, truths));

        /// <summary>A test of a reading the cell decides over a class read as the answer.</summary>
        public static ReadingTable Comparing(string reader, CosmosValueClass kind, CosmosServiceTest test, string answer) => Of(reader, ReadingRows.Comparing(kind, test, answer));

        /// <summary>A row a reading rests on.</summary>
        public static ReadingTable Cited(string reader, string row) => Of(reader, ReadingRows.Cited(row));

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

            var tests = into._tests;
            foreach (var (test, rows) in from._tests)
                tests = tests.SetItem(test, tests.TryGetValue(test, out var held) ? held.Union(rows) : rows);

            var (units, unitRows) = (into.Units.Union(from.Units), into.UnitRows.Union(from.UnitRows));
            return joined.Count == into._rows.Count && joined.All(r => into._rows[r.Key] == r.Value) && units.Equals(into.Units) && unitRows.Equals(into.UnitRows)
                && tests.Count == into._tests.Count && tests.All(t => into._tests[t.Key].Equals(t.Value))
                ? into : new(joined, units, unitRows, tests);
        }

        public bool Equals(ReadingTable? other) => other is not null && (ReferenceEquals(this, other) || _rows.Count == other._rows.Count
            && _rows.All(r => other._rows.TryGetValue(r.Key, out var rows) && rows == r.Value) && Units.Equals(other.Units) && UnitRows.Equals(other.UnitRows)
            && _tests.Count == other._tests.Count && _tests.All(t => other._tests.TryGetValue(t.Key, out var rows) && rows.Equals(t.Value)));

        public override bool Equals(object? obj) => Equals(obj as ReadingTable);

        public override int GetHashCode() => _tests.Aggregate(_rows.Aggregate(HashCode.Combine(Units, UnitRows), (h, r) => h + HashCode.Combine(r.Key, r.Value)), (h, t) => h + HashCode.Combine(t.Key, t.Value));

        /// <summary>Each reading and its rows, in reading order, <c>LIKE</c>'s units, and each type test's rows, as the theory's digest reads a value.</summary>
        public override string ToString() => string.Join(";", _rows.Select(r => r.Key + "=" + r.Value).Append("like=" + string.Join(",", Units.AsEnumerable()) + "/" + string.Join(",", UnitRows.AsEnumerable()))
            .Concat(_tests.Select(t => "test " + t.Key + "=" + string.Join(",", t.Value.AsEnumerable()))));

    }

}
