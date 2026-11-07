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
    /// the side each of the answers S found read the key places it on. A key the table does not hold answers every outcome,
    /// a failure included, and is named (<c>DESIGN.md</c>, <i>Forms, rows, and the service on a cell</i>).
    /// </summary>
    internal static class ServiceLeaf
    {

        public static OutcomeVector On(ServiceForm leaf, CellPartition cells, ServiceTable table, LeafReads reads)
        {
            if (leaf.Members.Count > 0)
                throw new ArgumentException("A leaf tests one path; a connective is evaluated from its parts.", nameof(leaf));
            if ((leaf.Kind == ServiceFormKind.ComparePaths ? PlaceSpellings.Of(leaf.Path!, leaf.Right!) : leaf.Path) is { } own && own != cells.Path)
                throw new ArgumentException("A leaf is evaluated on its own path's cells, and the partition rule hands no other.", nameof(cells));

            // What licenses the leaf stands beside its table: S's rules found which rows (LeafReads.Licences, G3).
            var rows = new HashSet<string>(reads.Licences, StringComparer.Ordinal);
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

                // A class S placed is placed by its order's readers (F4, G2); any other is read by its key.
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
        /// (<c>Service.StringPredicatesOverNonStringsAreUndefined</c>); over a string by whether it matches under each matcher
        /// S found reads the test's own entry (<see cref="MeasuredEntry.Readers"/>, <c>Service.AffixesMatchByCodePoint</c>,
        /// <c>Service.LikeMatchesByCodePoint</c>). An argument no pattern says, and an entry S found nothing reads, answer
        /// anything.
        /// </summary>
        static ServiceOutcomes Affix(ServiceForm leaf, PathCell cell, CosmosValueClass kind, ServiceTable table, LeafReads reads, ISet<string> rows)
        {
            if (kind != CosmosValueClass.String)
                return table.Outcomes(MeasuredKey.Of(leaf.Test, CosmosValueClass.None, kind), rows);

            if (leaf.Pattern is not { } pattern)
                return ServiceOutcomes.Widest;

            if (table.Read(MeasuredKey.Of(leaf.Test, CosmosValueClass.String, CosmosValueClass.None), rows) is not { } entry)
                return ServiceOutcomes.Widest;

            var outcomes = entry.Outcomes;
            foreach (var matcher in entry.Readers)
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
        /// A comparison with a literal of the cell's type: by the side or sides each comparator S found reads the type's order
        /// entry places the cell on (<see cref="MeasuredEntry.Readers"/>), each by its own readings of a whole number. S hands
        /// a type's entry only the comparators of that type's order (<see cref="AnswerMeanings.ReadBy"/>), so a string is
        /// placed by a comparator of strings and a number by one of numbers, and an entry naming another kind's has none.
        /// </summary>
        static ServiceOutcomes Within(ServiceForm leaf, PathCell cell, CosmosValueClass operand, ServiceTable table, LeafReads reads, ISet<string> rows)
        {
            if (table.Read(MeasuredKey.Of(CosmosServiceTest.Order, operand, CosmosValueClass.None), rows) is not { } order)
                return ServiceOutcomes.Widest;

            // What else the entry answers, a failure say, stands beside.
            var outcomes = order.Outcomes;
            foreach (var comparator in order.Readers)
            {
                if (Place(cell, leaf.Literal!, comparator, order.Readings, reads) is not { } sides)
                    return ServiceOutcomes.Widest;

                foreach (var side in sides)
                    outcomes |= table.Outcomes(MeasuredKey.Of(leaf.Test, operand, side), rows);
            }

            return outcomes;
        }

        /// <summary>
        /// A comparison of two paths on a pair of cells, which S evaluates only under a set whose rows license it
        /// (<c>Service.TwoPathsCompareAsAPathAndALiteral</c>, the licence's rows handed beside the table): two values of one
        /// class S placed by the side or sides each comparator reading its order places the left on
        /// (<c>Service.ComparisonWithinAType</c>, <c>Service.StringOrderIsCodePoint</c>); any other pair by its key in the pair
        /// table, which S's pairs rules read off the rows as the left against a literal of the right's value, as the right
        /// against the left's with the comparator mirrored, and as a side absent (G3).
        /// </summary>
        static ServiceOutcomes Paired(ServiceForm leaf, PathCell.Pair pair, ServiceTable table, LeafReads reads, ISet<string> rows)
        {
            if (CosmosValueClasses.Of(pair.Left) is not CosmosValueClass left || CosmosValueClasses.Of(pair.Right) is not CosmosValueClass right)
                return ServiceOutcomes.Widest;

            // Two values of one class S placed are placed by its order's readers (F4, G2).
            if (left == right && table.Placed.Contains(left))
            {
                if (table.Read(MeasuredKey.Of(CosmosServiceTest.Order, left, CosmosValueClass.None), rows) is not { } order)
                    return ServiceOutcomes.Widest;

                var outcomes = order.Outcomes;
                foreach (var side in order.Readers.AsEnumerable().SelectMany(comparator => Sides(pair, comparator, order.Readings, reads)).Distinct())
                    outcomes |= table.Outcomes(MeasuredKey.Of(leaf.Test, left, side), rows);

                return outcomes;
            }

            return table.PairOutcomes(MeasuredKey.Of(leaf.Test, right, left), rows);
        }

        /// <summary>
        /// Where the left of a pair lies against the right under a comparator: two numbers equal on a place's diagonal, and a
        /// number against a point by its value as the comparator reads one; two strings as they compare at two points; a cell
        /// parted by UTF-16 unit as it is parted, but either way where the sides may hold two bands S's rules found the set's
        /// comparators rank apart from that order (<see cref="LeafReads.Departs"/>), and anywhere where either side may hold a
        /// band they move (<see cref="LeafReads.Moved"/>), two points too, or is a point holding a class they found unkept
        /// (<see cref="LeafReads.Unkept"/>); anywhere otherwise.
        /// </summary>
        static IEnumerable<CosmosValueClass> Sides(PathCell.Pair pair, CosmosAnswer comparator, ValueSet<(CosmosAnswer, string)> readings, LeafReads reads)
        {
            CosmosValueClass[] anywhere = [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above];
            if (IsNumberCell(pair.Left))
                return pair.Order == 0 ? [CosmosValueClass.Equal]
                    : pair.Right is PathCell.Point { Value: var v } && TryNumberOf(v, out var c) ? Numeric(pair.Left, c, comparator, readings) ?? anywhere
                    : anywhere;

            var unsaid = Bands(pair.Left).Overlaps(reads.Moved) || Bands(pair.Right).Overlaps(reads.Moved) || Unkept(pair.Left, reads) || Unkept(pair.Right, reads);
            switch (pair.Left, pair.Right)
            {
                case (PathCell.Point { Value: var a }, PathCell.Point { Value: var b }) when IsString(a) && IsString(b) && unsaid == false:
                    return [Side(Compare(comparator, StringOf(a), StringOf(b)))];
                case (_, _) when pair.Order is null || unsaid:
                    return anywhere;
                case (_, _) when pair.Order != 0 && reads.Departs.AsEnumerable().Any(d => Bands(pair.Left).Contains(d.Band) && Bands(pair.Right).Contains(d.Other)):
                    return [CosmosValueClass.Below, CosmosValueClass.Above];
                default:
                    return [Side(pair.Order!.Value)];
            }
        }

        /// <summary>A cell of numbers, or a point holding one.</summary>
        static bool IsNumberCell(PathCell cell) => cell is PathCell.Numbers || cell is PathCell.Point { Value: var v } && IsNumber(v);

        /// <summary>The bands a string cell's characters may be in.</summary>
        static ValueSet<string> Bands(PathCell cell) => cell switch
        {
            PathCell.Strings s => s.Classes,
            PathCell.Point { Value: var v } when IsString(v) => CharactersOf(v),
            _ => ValueSet<string>.Empty,
        };

        /// <summary>A point, a value as written, holding a class S's rules found it says nothing of the stored string by.</summary>
        static bool Unkept(PathCell cell, LeafReads reads) => cell is PathCell.Point && Bands(cell).Overlaps(reads.Unkept);

        static CosmosValueClass NonBoolean(ServiceForm leaf, PathCell cell) =>
            leaf.Kind == ServiceFormKind.Bare && CosmosValueClasses.Of(cell) is CosmosValueClass kind and not (CosmosValueClass.True or CosmosValueClass.False) ? kind : CosmosValueClass.None;

        /// <summary>
        /// Where a cell's values lie against a literal under a comparator; <c>null</c> where it does not place them, or not
        /// exactly. A string as the comparator orders two, a number by its value as the comparator reads one. A literal S's
        /// rules found unplaced lies anywhere (<see cref="LeafReads.Unplaced"/>), and so does a point holding a class they
        /// found unkept (<see cref="LeafReads.Unkept"/>); a range is placed by its bounds unless they found the literal parted
        /// over it (<see cref="LeafReads.Parted"/>), and is equal only to a literal it holds.
        /// </summary>
        static CosmosValueClass[]? Place(PathCell cell, string literal, CosmosAnswer comparator, ValueSet<(CosmosAnswer, string)> readings, LeafReads reads)
        {
            switch (cell)
            {
                case PathCell.Point or PathCell.Strings when IsString(literal) && reads.Unplaced.Contains(literal):
                    return [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above];

                case PathCell.Point { Value: var v } when IsString(v) && IsString(literal):
                    return Unkept(cell, reads) ? [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above] : [Side(Compare(comparator, StringOf(v), StringOf(literal)))];

                case PathCell.Strings s when IsString(literal):
                    var (t, parted) = (StringOf(literal), reads.Parted.Contains(literal));
                    return Sides(cell, literal, parted == false && s.High is string h && string.CompareOrdinal(StringOf(h), t) <= 0,
                        parted == false && s.Low is string l && string.CompareOrdinal(StringOf(l), t) >= 0);

                case PathCell.Point { Value: var v } when IsNumber(v) && TryNumberOf(literal, out var c):
                    return Numeric(cell, c, comparator, readings);

                case PathCell.Numbers when TryNumberOf(literal, out var c):
                    return Numeric(cell, c, comparator, readings);

                default:
                    return null;
            }
        }

        /// <summary>One side where every value lies there; otherwise either, and the literal where the cell holds it.</summary>
        static CosmosValueClass[] Sides(PathCell cell, string literal, bool below, bool above) =>
            below ? [CosmosValueClass.Below] : above ? [CosmosValueClass.Above]
            : cell.Contains(literal) ? [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above] : [CosmosValueClass.Below, CosmosValueClass.Above];

        static CosmosValueClass Side(int order) => order < 0 ? CosmosValueClass.Below : order > 0 ? CosmosValueClass.Above : CosmosValueClass.Equal;

        /// <summary>
        /// Two strings under a comparator of strings, the one the harness holds the service's order to: by UTF-16 unit, or by
        /// code point, a pair the one it encodes and a lone surrogate its own unit, the order of the bytes the service holds
        /// (<see cref="ByCodePoint"/>). Under the emulator's, the stored value against the literal as the statement sends it,
        /// each in the stored spelling with both escapes (<see cref="EmulatorStoredText.Of"/>): what it compares, measured
        /// (<c>Service.EmulatorComparesTheSpellingItIsSent</c>). The strings are taken as they are: what a target stores of
        /// one written otherwise is S's (<see cref="LeafReads.Unkept"/>). Each comparator's own evaluation; one of another
        /// kind is none, and S hands none (<see cref="AnswerMeanings.ReadBy"/>).
        /// </summary>
        internal static int Compare(CosmosAnswer comparator, string a, string b) => comparator switch
        {
            CosmosAnswer.CodePoint => ByCodePoint(a, b),
            CosmosAnswer.Utf16 => string.CompareOrdinal(a, b),
            CosmosAnswer.StoredCodePoint => ByCodePoint(EmulatorStoredText.Of(a), EmulatorStoredText.Of(b)),
            _ => throw new ArgumentOutOfRangeException(nameof(comparator), comparator, "No comparator of strings: S's roles hand a string's order none other."),
        };

        /// <summary>
        /// Whether a string matches a pattern of runs under a matcher, one the test's entry names: by code point, as by UTF-16
        /// unit over well-formed text (<see cref="AffixPattern.Matches"/>); under the emulator's, the string's stored spelling
        /// against the pattern's, which is what it matches (<c>Service.EmulatorComparesTheSpellingItIsSent</c>). The matcher's
        /// own evaluation on two concrete strings, as <see cref="Compare"/> is the comparator's; an answer that is no matcher
        /// has none, and S hands none (<see cref="AnswerMeanings.ReadBy"/>).
        /// </summary>
        internal static bool Matches(CosmosAnswer matcher, string text, string pattern) => matcher switch
        {
            CosmosAnswer.CodePoint => AffixPattern.Matches(text, pattern),
            CosmosAnswer.StoredCodePoint => AffixPattern.Matches(EmulatorStoredText.Of(text), EmulatorStoredText.Of(pattern)),
            _ => throw new ArgumentOutOfRangeException(nameof(matcher), matcher, "No matcher: S's roles hand an affix's entry none other."),
        };

        /// <summary>
        /// Whether a matcher's spelling splits a pattern of runs, so that a range's affix cut, of strings as written, does not
        /// say what it matches: under the emulator's, where the stored spelling of the pattern is not its runs' own
        /// (<see cref="AffixPattern.StoredAlike"/>); never under one that matches what it is sent. The matcher's own
        /// evaluation of a concrete pattern, as <see cref="Matches"/> is of a string.
        /// </summary>
        internal static bool Splits(CosmosAnswer matcher, string pattern) => matcher == CosmosAnswer.StoredCodePoint && AffixPattern.StoredAlike(pattern) == false;

        /// <summary>
        /// Strings by code point, a pair the one it encodes and a lone surrogate its own unit: the order of their UTF-8 bytes,
        /// a lone surrogate's generalised as the service holds one written as raw bytes.
        /// </summary>
        static int ByCodePoint(string a, string b)
        {
            for (int i = 0, j = 0; ; i += Width(a, i), j += Width(b, j))
            {
                if (i == a.Length || j == b.Length)
                    return (i < a.Length).CompareTo(j < b.Length);

                if (PointAt(a, i).CompareTo(PointAt(b, j)) is var order and not 0)
                    return order;
            }
        }

        /// <summary>The units the code point at an index takes: two for a pair, one for any other unit.</summary>
        static int Width(string s, int i) => i + 1 < s.Length && char.IsSurrogatePair(s[i], s[i + 1]) ? 2 : 1;

        /// <summary>The code point at an index: a pair's, or the unit itself.</summary>
        static int PointAt(string s, int i) => Width(s, i) == 2 ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];

        /// <summary>
        /// Where a number or a cell of numbers lies against a literal by the value the service compares
        /// (<c>Service.NumbersBeyondLongCompareAsDoubles</c>): an int64 as itself and any other number as its double, by
        /// value, [2^63, 2^64) among the others. A whole number within int64 is read as the comparator's readings say, which
        /// S handed its order's entry (<see cref="MeasuredEntry.Readings"/>, <c>Service.IntegersOrderExactly</c>): by its value,
        /// by its double, both where a target that holds one written with a point as a double reads it, the cell keeping no
        /// spelling, and anywhere by neither (G2); a literal sent as a double is placed against only where that double is it.
        /// </summary>
        static CosmosValueClass[]? Numeric(PathCell cell, decimal c, CosmosAnswer comparator, ValueSet<(CosmosAnswer, string)> readings) =>
            cell.SidesOf(c, readings.Contains((comparator, "value")), readings.Contains((comparator, "double"))) is not { } sides ? null
            : sides.Contains(null) ? [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above] : [.. sides.Select(s => Side(s!.Value)).Distinct()];

    }

}
