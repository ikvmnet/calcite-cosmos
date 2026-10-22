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

        public static OutcomeVector On(ServiceForm leaf, CellPartition cells, ServiceTable table, ValueSet<string> parted)
        {
            if (leaf.Members.Count > 0)
                throw new ArgumentException("A leaf tests one path; a connective is evaluated from its parts.", nameof(leaf));
            if (leaf.Path is { } own && own != cells.Path)
                throw new ArgumentException("A leaf is evaluated on its own path's cells, and the partition rule hands no other.", nameof(cells));

            var rows = new HashSet<string>(StringComparer.Ordinal);
            var outcomes = cells.Cells.Select(cell => new CellOutcome(Outcomes(leaf, cell, table, parted, rows), NonBoolean(leaf, cell))).ToList();
            return new OutcomeVector(cells, outcomes, ValueSet.From(rows));
        }

        static ServiceOutcomes Outcomes(ServiceForm leaf, PathCell cell, ServiceTable table, ValueSet<string> parted, ISet<string> rows)
        {
            // A kind the theory does not know says nothing of the cell.
            if (ClassOf(cell) is not CosmosValueClass kind)
                return ServiceOutcomes.Widest;

            switch (leaf.Kind)
            {
                case ServiceFormKind.Test or ServiceFormKind.Bare:
                    return table.Outcomes(MeasuredKey.Of(leaf.Test, CosmosValueClass.None, kind), rows);

                case ServiceFormKind.Constant:
                    return table.Outcomes(MeasuredKey.Of(leaf.Test, ClassOf(leaf.Literal!), kind), rows);

                case ServiceFormKind.Compare:
                    var operand = ClassOf(leaf.Literal!);
                    return operand == kind && operand is CosmosValueClass.String or CosmosValueClass.Number
                        ? Within(leaf, cell, operand, table, parted, rows)
                        : table.Outcomes(MeasuredKey.Of(leaf.Test, operand, kind), rows);

                default:
                    return ServiceOutcomes.Widest;
            }
        }

        /// <summary>A comparison with a literal of the cell's type: by the side or sides each comparator places the cell on.</summary>
        static ServiceOutcomes Within(ServiceForm leaf, PathCell cell, CosmosValueClass operand, ServiceTable table, ValueSet<string> parted, ISet<string> rows)
        {
            var order = MeasuredKey.Of(CosmosServiceTest.Order, operand, CosmosValueClass.None);
            if (table.TryGet(order, out var comparators) == false)
            {
                rows.Add("missing:" + order);
                return ServiceOutcomes.Widest;
            }

            // An entry that names no comparator orders nothing; what else it answers, a failure say, stands beside.
            rows.UnionWith(comparators.Evidence);
            var placing = comparators.Answers.Where(a => a is CosmosAnswer.CodePoint or CosmosAnswer.Utf16 or CosmosAnswer.Numeric or CosmosAnswer.StoredCodePoint).ToList();
            if (placing.Count == 0)
                return ServiceOutcomes.Widest;

            var outcomes = comparators.Outcomes;
            foreach (var comparator in placing)
            {
                if (Place(cell, leaf.Literal!, comparator, parted) is not { } sides)
                    return ServiceOutcomes.Widest;

                foreach (var side in sides)
                    outcomes |= table.Outcomes(MeasuredKey.Of(leaf.Test, operand, side), rows);
            }

            return outcomes;
        }

        static CosmosValueClass NonBoolean(ServiceForm leaf, PathCell cell) =>
            leaf.Kind == ServiceFormKind.Bare && ClassOf(cell) is CosmosValueClass kind and not (CosmosValueClass.True or CosmosValueClass.False) ? kind : CosmosValueClass.None;

        /// <summary>
        /// Where a cell's values lie against a literal under a comparator; <c>null</c> where the comparator does not order
        /// them, or not exactly.
        /// </summary>
        static CosmosValueClass[]? Place(PathCell cell, string literal, CosmosAnswer comparator, ValueSet<string> parted)
        {
            switch (comparator, cell)
            {
                // What the emulator stores for U+0000 is not what it is sent: a literal holding one lies anywhere, and so does
                // a range that may hold one, equal only to a literal it holds or one holding the high band, as U+FDD0 is.
                case (CosmosAnswer.StoredCodePoint, PathCell.Point or PathCell.Strings) when IsString(literal) && CharactersOf(literal).Contains(NulClass):
                    return [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above];

                case (CosmosAnswer.StoredCodePoint, PathCell.Strings s) when IsString(literal) && s.Classes.Contains(NulClass):
                    return cell.Contains(literal) || CharactersOf(literal).Contains(HighBand)
                        ? [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above] : [CosmosValueClass.Below, CosmosValueClass.Above];

                case (CosmosAnswer.CodePoint or CosmosAnswer.Utf16 or CosmosAnswer.StoredCodePoint, PathCell.Point { Value: var v }) when IsString(v) && IsString(literal):
                    return [Side(Compare(comparator, StringOf(v), StringOf(literal)))];

                case (CosmosAnswer.CodePoint or CosmosAnswer.Utf16 or CosmosAnswer.StoredCodePoint, PathCell.Strings s) when IsString(literal):
                    var t = StringOf(literal);
                    return Sides(cell, literal, parted.Contains(literal) == false && s.High is string h && string.CompareOrdinal(StringOf(h), t) <= 0,
                        parted.Contains(literal) == false && s.Low is string l && string.CompareOrdinal(StringOf(l), t) >= 0);

                case (CosmosAnswer.Numeric, PathCell.Point { Value: var v }) when IsNumber(v) && TryNumberOf(literal, out var c):
                    return Numeric(cell, c);

                case (CosmosAnswer.Numeric, PathCell.Numbers) when TryNumberOf(literal, out var c):
                    return Numeric(cell, c);

                default:
                    return null;
            }
        }

        /// <summary>One side where every value lies there; otherwise either, and the literal where the cell holds it.</summary>
        static CosmosValueClass[] Sides(PathCell cell, string literal, bool below, bool above) =>
            below ? [CosmosValueClass.Below] : above ? [CosmosValueClass.Above]
            : cell.Contains(literal) ? [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above] : [CosmosValueClass.Below, CosmosValueClass.Above];

        static CosmosValueClass Side(int order) => order < 0 ? CosmosValueClass.Below : order > 0 ? CosmosValueClass.Above : CosmosValueClass.Equal;

        /// <summary>Two strings under a comparator of strings, the one the harness holds the service's order to.</summary>
        internal static int Compare(CosmosAnswer comparator, string a, string b) => comparator switch
        {
            CosmosAnswer.Utf16 => string.CompareOrdinal(a, b),
            CosmosAnswer.StoredCodePoint => ByCodePoint(a.Replace("\0", (char)0xFDD0 + "0"), b.Replace("\0", (char)0xFDD0 + "0")),
            _ => ByCodePoint(a, b),
        };

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
        /// alike written with a point, which the emulator holds as a double and Azure as an int64, and one answer serves
        /// both until a run of the probe account separates them (<c>TODO.md</c>); a literal sent as a double is placed
        /// against only where that double is it.
        /// </summary>
        static CosmosValueClass[]? Numeric(PathCell cell, decimal c)
        {
            var d = DoubleBound.Of(c);
            if ((c == decimal.Truncate(c) && c >= long.MinValue && c <= long.MaxValue) == false && DoubleBound.Compare(d, c) != 0)
                return null;

            int?[] sides = cell switch
            {
                PathCell.Point { Value: var v } when TryNumberOf(v, out var x) && x == decimal.Truncate(x) && x >= long.MinValue && x <= long.MaxValue =>
                    [x.CompareTo(c), new PathCell.Numbers(true, Bound.AtLeast(x), Bound.AtMost(x), ValueSet<string>.Empty).RoundedSide(c)],
                PathCell.Point { Value: var v } => [Math.Sign(DoubleBound.Of(v).CompareTo(d)) is var side && side == 0 ? DoubleBound.Compare(d, c) : side],
                PathCell.Numbers n when n.Integral && (n.Upper is not Bound u || u.Value >= long.MinValue) && (n.Lower is not Bound l || l.Value <= long.MaxValue) => [n.Side(c), n.RoundedSide(c)],
                PathCell.Numbers n => [n.RoundedSide(c)],
                _ => [null],
            };

            return sides.Contains(null) ? [CosmosValueClass.Below, CosmosValueClass.Equal, CosmosValueClass.Above] : [.. sides.Select(s => Side(s!.Value)).Distinct()];
        }

        static CosmosValueClass? ClassOf(PathCell cell) => cell switch
        {
            PathCell.Absent => CosmosValueClass.Absent,
            PathCell.Null => CosmosValueClass.Null,
            PathCell.Structure s => s.Type == ObjectType ? CosmosValueClass.Object : CosmosValueClass.Array,
            PathCell.Strings => CosmosValueClass.String,
            PathCell.Numbers => CosmosValueClass.Number,
            PathCell.Point p => ClassOf(p.Value),
            _ => null,
        };

        static CosmosValueClass ClassOf(string value) => TypeOf(value) switch
        {
            StringType => CosmosValueClass.String,
            BooleanType => value == Of(true) ? CosmosValueClass.True : CosmosValueClass.False,
            NullType => CosmosValueClass.Null,
            _ => CosmosValueClass.Number,
        };

    }

}
