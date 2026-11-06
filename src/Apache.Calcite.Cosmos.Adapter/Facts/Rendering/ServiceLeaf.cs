using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The service on one path's cells: one test, read off the handed table cell by cell, a cell keyed by its kind or by
    /// the side each comparator places it on. A key the table does not hold answers every outcome, a failure included,
    /// and is named (<c>DESIGN.md</c>, <i>Forms, rows, and the service on a cell</i>).
    /// </summary>
    internal static class ServiceLeaf
    {

        public static OutcomeVector On(ServiceForm leaf, CellPartition cells, ServiceTable table, LeafReads reads)
        {
            if (leaf.Members.Count > 0)
                throw new ArgumentException("A leaf tests one path; a connective is evaluated from its parts.", nameof(leaf));
            if ((leaf.Kind == ServiceFormKind.ComparePaths ? PlaceSpellings.Of(leaf.Path!, leaf.Right!) : leaf.Path) is { } own && own != cells.Path)
                throw new ArgumentException("A leaf is evaluated on its own path's cells, and the partition rule hands no other.", nameof(cells));

            var rows = new HashSet<string>(StringComparer.Ordinal);
            var outcomes = cells.Cells.Select(cell => new CellOutcome(Outcomes(leaf, cell, table, reads, rows), NonBoolean(leaf, cell))).ToList();
            return new OutcomeVector(cells, outcomes, ValueSet.From(rows));
        }

        static ServiceOutcomes Outcomes(ServiceForm leaf, PathCell cell, ServiceTable table, LeafReads reads, ISet<string> rows)
        {
            if (leaf.Kind == ServiceFormKind.ComparePaths)
                return cell is PathCell.Pair pair ? Paired(leaf, pair, table, reads, rows) : ServiceOutcomes.Widest;

            // A kind the theory does not know says nothing of the cell.
            if (CosmosValueClasses.Of(cell) is not CosmosValueClass kind)
                return ServiceOutcomes.Widest;

            switch (leaf.Kind)
            {
                case ServiceFormKind.Test or ServiceFormKind.Bare:
                    return table.Outcomes(MeasuredKey.Of(leaf.Test, CosmosValueClass.None, kind), rows);

                case ServiceFormKind.Constant:
                    return table.Outcomes(MeasuredKey.Of(leaf.Test, CosmosValueClasses.Of(leaf.Literal!), kind), rows);

                // A class S placed is placed by its order's comparators (F4); any other is read by its key.
                case ServiceFormKind.Compare:
                    var operand = CosmosValueClasses.Of(leaf.Literal!);
                    return operand == kind && table.Placed.Contains(operand)
                        ? Within(leaf, cell, operand, table, reads, rows)
                        : table.Outcomes(MeasuredKey.Of(leaf.Test, operand, kind), rows);

                case ServiceFormKind.Affix:
                    return Affix(leaf, cell, kind, table, reads, rows);

                default:
                    return ServiceOutcomes.Widest;
            }
        }

        /// <summary>
        /// An affix or a <c>LIKE</c> (h07): over a value that is no string by its kind
        /// (<c>Service.StringPredicatesOverNonStringsAreUndefined</c>); over a string by whether it matches under the matcher
        /// or matchers the test's own entry names, as an order names a comparator (<c>Service.AffixesMatchByCodePoint</c>,
        /// <c>Service.LikeMatchesByCodePoint</c>). An argument no pattern says, and an entry naming no matcher, answer anything.
        /// </summary>
        static ServiceOutcomes Affix(ServiceForm leaf, PathCell cell, CosmosValueClass kind, ServiceTable table, LeafReads reads, ISet<string> rows)
        {
            if (kind != CosmosValueClass.String)
                return table.Outcomes(MeasuredKey.Of(leaf.Test, CosmosValueClass.None, kind), rows);

            if (leaf.Pattern is not { } pattern)
                return ServiceOutcomes.Widest;

            var key = MeasuredKey.Of(leaf.Test, CosmosValueClass.String, CosmosValueClass.None);
            if (table.TryGet(key, out var matchers) == false)
            {
                rows.Add("missing:" + key);
                return ServiceOutcomes.Widest;
            }

            // The matchers are the entry's, as S settled them from the meanings' roles; a comparator no matcher evaluates beside
            // them leaves the match unsaid.
            rows.UnionWith(matchers.Evidence);
            if (matchers.Matching.IsEmpty || matchers.Unmatched.IsEmpty == false)
                return ServiceOutcomes.Widest;

            var outcomes = matchers.Outcomes;
            foreach (var matcher in matchers.Matching)
                foreach (var side in Matched(cell, pattern, matcher, reads))
                    outcomes |= table.Outcomes(MeasuredKey.Of(leaf.Test, CosmosValueClass.String, side), rows);

            return outcomes;
        }

        /// <summary>
        /// Whether a string cell's values match a pattern under a matcher: a string as the matcher matches it
        /// (<see cref="Matches"/>), and a range as the affix cut it by its own pattern, a lone <c>%</c> matching every string
        /// (<see cref="AffixPattern.Universal"/>). Either where S's rules found the match unread over a band the cell may hold
        /// (<see cref="LeafReads.Unread"/>), a range's affix cut untold over one it may hold (<see cref="LeafReads.Uncut"/>),
        /// or every cell unread (<see cref="LeafReads.Blind"/>): which rows say so, and of which bands, is S's
        /// (<c>truncates</c>, <c>storedas</c>, <c>rewrites</c>), and the leaf tests the bands it is handed on the cell.
        /// </summary>
        static CosmosValueClass[] Matched(PathCell cell, string pattern, CosmosAnswer matcher, LeafReads reads)
        {
            CosmosValueClass[] either = [CosmosValueClass.Matching, CosmosValueClass.Missing];
            CosmosValueClass[] Side(bool match) => match ? [CosmosValueClass.Matching] : [CosmosValueClass.Missing];
            if (reads.Blind.IsEmpty == false)
                return either;

            return cell switch
            {
                PathCell.Point when Bands(cell).Overlaps(reads.Unread) => either,
                PathCell.Point { Value: var v } => Side(Matches(matcher, StringOf(v), pattern)),
                PathCell.Strings s when s.Classes.Overlaps(reads.Unread) || s.Classes.Overlaps(reads.Uncut) => either,
                PathCell.Strings when AffixPattern.Universal(pattern) => [CosmosValueClass.Matching],
                PathCell.Strings s when s.Like?.Contains(pattern) == true => [CosmosValueClass.Matching],
                PathCell.Strings s when s.NotLike?.Contains(pattern) == true => [CosmosValueClass.Missing],
                _ => either,
            };
        }

        /// <summary>
        /// A comparison with a literal of the cell's type: by the side or sides each comparator of the type's order entry
        /// places the cell on, the comparators S settled of its answers (<see cref="MeasuredEntry.Placing"/>).
        /// </summary>
        static ServiceOutcomes Within(ServiceForm leaf, PathCell cell, CosmosValueClass operand, ServiceTable table, LeafReads reads, ISet<string> rows)
        {
            var order = MeasuredKey.Of(CosmosServiceTest.Order, operand, CosmosValueClass.None);
            if (table.TryGet(order, out var comparators) == false)
            {
                rows.Add("missing:" + order);
                return ServiceOutcomes.Widest;
            }

            // An entry that names no comparator orders nothing; what else it answers, a failure say, stands beside.
            rows.UnionWith(comparators.Evidence);
            if (comparators.Placing.IsEmpty)
                return ServiceOutcomes.Widest;

            var outcomes = comparators.Outcomes;
            foreach (var comparator in comparators.Placing)
            {
                if (Place(cell, leaf.Literal!, comparator, reads) is not { } sides)
                    return ServiceOutcomes.Widest;

                foreach (var side in sides)
                    outcomes |= table.Outcomes(MeasuredKey.Of(leaf.Test, operand, side), rows);
            }

            return outcomes;
        }

        /// <summary>
        /// A comparison of two paths on a pair of cells, where the set's rows say two paths compare as the left with the
        /// right's value (<c>Service.TwoPathsCompareAsAPathAndALiteral</c>): within a type by the side or sides the
        /// comparator places the left on (<c>Service.ComparisonWithinAType</c>, <c>Service.StringOrderIsCodePoint</c>),
        /// across types by the classes' row, and where the right is no literal a row keys (null, absent, a structure) by the
        /// row mirrored, as that row measured both ways round; a side absent by the row's own key for one, and two nulls as
        /// against the literal null. Without it, anything.
        /// </summary>
        static ServiceOutcomes Paired(ServiceForm leaf, PathCell.Pair pair, ServiceTable table, LeafReads reads, ISet<string> rows)
        {
            if (CosmosValueClasses.Of(pair.Left) is not CosmosValueClass left || CosmosValueClasses.Of(pair.Right) is not CosmosValueClass right)
                return ServiceOutcomes.Widest;

            var licence = MeasuredKey.Of(CosmosServiceTest.ComparePaths, CosmosValueClass.None, CosmosValueClass.None);
            if (table.TryGet(licence, out var paired) == false || paired.Answers.Contains(CosmosAnswer.True) == false)
            {
                rows.Add("missing:" + licence);
                return ServiceOutcomes.Widest;
            }

            rows.UnionWith(paired.Evidence);

            if (left == CosmosValueClass.Absent || right == CosmosValueClass.Absent)
                return table.Outcomes(MeasuredKey.Of(CosmosServiceTest.ComparePaths, CosmosValueClass.Absent, CosmosValueClass.None), rows);

            // Two values of one class S placed are placed by its order's comparators (F4).
            if (left == right && table.Placed.Contains(left))
            {
                var order = MeasuredKey.Of(CosmosServiceTest.Order, left, CosmosValueClass.None);
                if (table.TryGet(order, out var comparators) == false)
                {
                    rows.Add("missing:" + order);
                    return ServiceOutcomes.Widest;
                }

                rows.UnionWith(comparators.Evidence);
                if (comparators.Placing.IsEmpty)
                    return ServiceOutcomes.Widest;

                var outcomes = comparators.Outcomes;
                foreach (var side in comparators.Placing.AsEnumerable().SelectMany(comparator => Sides(pair, comparator, reads)).Distinct())
                    outcomes |= table.Outcomes(MeasuredKey.Of(leaf.Test, left, side), rows);

                return outcomes;
            }

            var direct = MeasuredKey.Of(leaf.Test, right, left);
            var mirrored = MeasuredKey.Of(Mirror(leaf.Test), left, right);
            return table.TryGet(direct, out _) || table.TryGet(mirrored, out _) == false ? table.Outcomes(direct, rows) : table.Outcomes(mirrored, rows);
        }

        static CosmosServiceTest Mirror(CosmosServiceTest op) => op switch
        {
            CosmosServiceTest.Less => CosmosServiceTest.Greater,
            CosmosServiceTest.LessOrEqual => CosmosServiceTest.GreaterOrEqual,
            CosmosServiceTest.Greater => CosmosServiceTest.Less,
            CosmosServiceTest.GreaterOrEqual => CosmosServiceTest.LessOrEqual,
            _ => op,
        };

        /// <summary>
        /// Where the left of a pair lies against the right under a comparator: two points as they compare; a cell parted
        /// by UTF-16 unit as it is parted, but either way where the sides may hold two bands S's rules found the set's
        /// comparators rank apart from that order (<see cref="LeafReads.Departs"/>), and anywhere where either side may hold
        /// a band they move (<see cref="LeafReads.Moved"/>), two points too; two numbers equal on a place's diagonal, a number
        /// against a point by its value; anywhere otherwise.
        /// </summary>
        static IEnumerable<CosmosValueClass> Sides(PathCell.Pair pair, CosmosAnswer comparator, LeafReads reads)
        {
            CosmosValueClass[] anywhere = [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above];
            var moved = Bands(pair.Left).Overlaps(reads.Moved) || Bands(pair.Right).Overlaps(reads.Moved);
            switch (comparator, pair.Left, pair.Right)
            {
                case (CosmosAnswer.Numeric or CosmosAnswer.NumericPointedAsDouble, _, _) when pair.Order == 0:
                    return [CosmosValueClass.Equal];
                case (CosmosAnswer.Numeric or CosmosAnswer.NumericPointedAsDouble, PathCell.Point or PathCell.Numbers, PathCell.Point { Value: var v }) when TryNumberOf(v, out var c):
                    return Numeric(pair.Left, c) ?? anywhere;
                case (CosmosAnswer.Numeric or CosmosAnswer.NumericPointedAsDouble, _, _):
                    return anywhere;
                case (_, PathCell.Point { Value: var a }, PathCell.Point { Value: var b }) when IsString(a) && IsString(b) && moved == false:
                    return [Side(Compare(comparator, StringOf(a), StringOf(b)))];
                case (_, _, _) when pair.Order is null || moved:
                    return anywhere;
                case (_, _, _) when pair.Order != 0 && reads.Departs.AsEnumerable().Any(d => Bands(pair.Left).Contains(d.Band) && Bands(pair.Right).Contains(d.Other)):
                    return [CosmosValueClass.Below, CosmosValueClass.Above];
                default:
                    return [Side(pair.Order!.Value)];
            }
        }

        /// <summary>The bands a string cell's characters may be in.</summary>
        static ValueSet<string> Bands(PathCell cell) => cell switch
        {
            PathCell.Strings s => s.Classes,
            PathCell.Point { Value: var v } when IsString(v) => CharactersOf(v),
            _ => ValueSet<string>.Empty,
        };

        static CosmosValueClass NonBoolean(ServiceForm leaf, PathCell cell) =>
            leaf.Kind == ServiceFormKind.Bare && CosmosValueClasses.Of(cell) is CosmosValueClass kind and not (CosmosValueClass.True or CosmosValueClass.False) ? kind : CosmosValueClass.None;

        /// <summary>
        /// Where a cell's values lie against a literal under a comparator; <c>null</c> where the comparator does not order
        /// them, or not exactly. A literal S's rules found unplaced lies anywhere (<see cref="LeafReads.Unplaced"/>); a range
        /// is placed by its bounds unless they found the literal parted over it (<see cref="LeafReads.Parted"/>), and is equal
        /// to a literal it holds or one they found loose (<see cref="LeafReads.Loose"/>).
        /// </summary>
        static CosmosValueClass[]? Place(PathCell cell, string literal, CosmosAnswer comparator, LeafReads reads)
        {
            switch (comparator, cell)
            {
                case (_, PathCell.Point or PathCell.Strings) when IsString(literal) && reads.Unplaced.Contains(literal):
                    return [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above];

                case (CosmosAnswer.CodePoint or CosmosAnswer.Utf16 or CosmosAnswer.StoredCodePoint, PathCell.Point { Value: var v }) when IsString(v) && IsString(literal):
                    return [Side(Compare(comparator, StringOf(v), StringOf(literal)))];

                case (CosmosAnswer.CodePoint or CosmosAnswer.Utf16 or CosmosAnswer.StoredCodePoint, PathCell.Strings s) when IsString(literal):
                    var (t, parted) = (StringOf(literal), reads.Parted.Contains(literal));
                    return Sides(cell, literal, parted == false && s.High is string h && string.CompareOrdinal(StringOf(h), t) <= 0,
                        parted == false && s.Low is string l && string.CompareOrdinal(StringOf(l), t) >= 0, reads.Loose.Contains(literal));

                case (CosmosAnswer.Numeric or CosmosAnswer.NumericPointedAsDouble, PathCell.Point { Value: var v }) when IsNumber(v) && TryNumberOf(literal, out var c):
                    return Numeric(cell, c);

                case (CosmosAnswer.Numeric or CosmosAnswer.NumericPointedAsDouble, PathCell.Numbers) when TryNumberOf(literal, out var c):
                    return Numeric(cell, c);

                default:
                    return null;
            }
        }

        /// <summary>One side where every value lies there; otherwise either, and the literal where the cell holds it or may equal it.</summary>
        static CosmosValueClass[] Sides(PathCell cell, string literal, bool below, bool above, bool loose) =>
            below ? [CosmosValueClass.Below] : above ? [CosmosValueClass.Above]
            : cell.Contains(literal) || loose ? [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above] : [CosmosValueClass.Below, CosmosValueClass.Above];

        static CosmosValueClass Side(int order) => order < 0 ? CosmosValueClass.Below : order > 0 ? CosmosValueClass.Above : CosmosValueClass.Equal;

        /// <summary>
        /// Two strings under a comparator of strings, the one the harness holds the service's order to. Under the emulator's,
        /// the stored value against the literal as the statement sends it, each in the stored spelling with both escapes
        /// (<see cref="EmulatorStoredText.Of"/>): what it compares, measured (<c>Service.EmulatorComparesTheSpellingItIsSent</c>).
        /// </summary>
        internal static int Compare(CosmosAnswer comparator, string a, string b) => comparator switch
        {
            CosmosAnswer.Utf16 => string.CompareOrdinal(a, b),
            CosmosAnswer.StoredCodePoint => ByCodePoint(EmulatorStoredText.Of(a), EmulatorStoredText.Of(b)),
            _ => ByCodePoint(a, b),
        };

        /// <summary>
        /// Whether a string matches a pattern of runs under a matcher, one the test's entry names: by code point, as by UTF-16
        /// unit over well-formed text (<see cref="AffixPattern.Matches"/>); under the emulator's, the string's stored spelling
        /// against the pattern's, which is what it matches (<c>Service.EmulatorComparesTheSpellingItIsSent</c>). The matcher's
        /// own evaluation on two concrete strings, as <see cref="Compare"/> is the comparator's.
        /// </summary>
        internal static bool Matches(CosmosAnswer matcher, string text, string pattern) => matcher == CosmosAnswer.StoredCodePoint
            ? AffixPattern.Matches(EmulatorStoredText.Of(text), EmulatorStoredText.Of(pattern))
            : AffixPattern.Matches(text, pattern);

        /// <summary>Strings by code point, a lone surrogate read as U+FFFD, as the service stores one.</summary>
        static int ByCodePoint(string a, string b)
        {
            var (x, y) = (a.EnumerateRunes(), b.EnumerateRunes());
            while (true)
            {
                var (more, others) = (x.MoveNext(), y.MoveNext());
                if (more == false || others == false)
                    return more.CompareTo(others);

                if (x.Current.Value.CompareTo(y.Current.Value) is var order and not 0)
                    return order;
            }
        }

        /// <summary>
        /// Where a number or a cell of numbers lies against a literal by the value the service compares
        /// (<c>Service.NumbersBeyondLongCompareAsDoubles</c>): an int64 as itself and any other number as its double, by
        /// value, [2^63, 2^64) among the others. A whole number in int64 is placed both ways, since the theory spells it
        /// alike written with a point, which the emulator holds as a double and Azure as an int64: the order rows name each
        /// target's comparator apart (<see cref="CosmosAnswer.Numeric"/>, <see cref="CosmosAnswer.NumericPointedAsDouble"/>),
        /// and both are evaluated so until Azure's is given its own reading (G2, <c>TODO.md</c>); a literal sent as a double
        /// is placed against only where that double is it.
        /// </summary>
        static CosmosValueClass[]? Numeric(PathCell cell, decimal c) => cell.SidesOf(c, byValue: true, byDouble: true) is not { } sides ? null
            : sides.Contains(null) ? [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above] : [.. sides.Select(s => Side(s!.Value)).Distinct()];

    }

}
