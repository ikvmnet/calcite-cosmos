using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// One cell of a path's import: a set of the values it may hold, inside one <see cref="PredicateAtom.State"/>, never
    /// empty. A value is <see cref="JsonValues"/>' spelling, a structure its type and <c>|</c>, and <c>null</c> the absent
    /// path. Strings are cut by UTF-16 unit, numbers by value, and every cell kept has an inhabitant (<c>DESIGN.md</c>,
    /// <i>Cells, and Calcite on one</i>).
    /// </summary>
    internal abstract record PathCell : IComparable<PathCell>
    {

        public sealed record Absent : PathCell;

        public sealed record Null : PathCell;

        /// <summary>Any object, or any array.</summary>
        public sealed record Structure(string Type) : PathCell;

        /// <summary>One scalar other than null.</summary>
        public sealed record Point(string Value) : PathCell;

        /// <summary>
        /// The strings strictly between two, either of which may be missing, whose characters are of the classes, but these.
        /// </summary>
        public sealed record Strings(string? Low, string? High, ValueSet<string> Except, ValueSet<string> Classes) : PathCell
        {

            internal bool Holds(string text) =>
                (Low is null || string.CompareOrdinal(text, StringOf(Low)) > 0) && (High is null || string.CompareOrdinal(text, StringOf(High)) < 0)
                && BandsOfText(text).IsSubsetOf(Classes);

        }

        /// <summary>
        /// The numbers of one kind within the bounds whose double lies within the double bounds, both ends held, but these.
        /// </summary>
        public sealed record Numbers(bool Integral, Bound? Lower, Bound? Upper, ValueSet<string> Except, double? DoubleLow = null, double? DoubleHigh = null) : PathCell
        {

            /// <summary>The bounds, an integer's held at the least and the most integer within them.</summary>
            (Bound? Lower, Bound? Upper) Held => Integral
                ? (Lower is Bound l ? Bound.AtLeast(l.Open && Math.Floor(l.Value) == l.Value ? l.Value + 1 : Math.Ceiling(l.Value)) : null,
                    Upper is Bound u ? Bound.AtMost(u.Open && Math.Ceiling(u.Value) == u.Value ? u.Value - 1 : Math.Floor(u.Value)) : null)
                : (Lower, Upper);

            /// <summary>Which side of a number every value lies on: -1 below, 1 above, or <c>null</c> where not every one.</summary>
            internal int? Side(decimal c) => Held is var (lower, upper) && upper is Bound u && (u.Value < c || u.Value == c && u.Open) ? -1
                : lower is Bound l && (l.Value > c || l.Value == c && l.Open) ? 1 : null;

            /// <summary>
            /// Which side of a double every value's double lies on: -1, 1, 0 where each is it, or <c>null</c>. A bound's double
            /// bounds the doubles within it, since rounding never falls as a number rises.
            /// </summary>
            internal int? DoubleSide(double d)
            {
                var (lower, upper) = Held;
                var low = lower is Bound l ? Math.Max(DoubleBound.Of(l.Value), DoubleLow ?? double.NegativeInfinity) : DoubleLow;
                var high = upper is Bound u ? Math.Min(DoubleBound.Of(u.Value), DoubleHigh ?? double.PositiveInfinity) : DoubleHigh;
                return high < d ? -1 : low > d ? 1 : low == d && high == d ? 0 : null;
            }

            /// <summary>Which side of a number every value's double lies on, by value: below the number's double is below it.</summary>
            internal int? RoundedSide(decimal c) => DoubleSide(DoubleBound.Of(c)) is 0 ? DoubleBound.Compare(DoubleBound.Of(c), c) : DoubleSide(DoubleBound.Of(c));

            /// <summary>Whether a number's double lies within the double bounds.</summary>
            internal bool Rounds(string value) => DoubleBound.Of(value) is var d && d >= (DoubleLow ?? d) && d <= (DoubleHigh ?? d);

        }

        /// <summary>A kind the theory does not know, of which nothing is said.</summary>
        public sealed record Unknown : PathCell;

        /// <summary>The state the cell lies in, which the coarse reading answers on.</summary>
        public PredicateAtom.State Coarse => this switch
        {
            Absent => PredicateAtom.State.AbsentState,
            Null => PredicateAtom.State.NullState,
            Structure s => new PredicateAtom.State.Structure(s.Type),
            Point p => new PredicateAtom.State.Scalar(p.Value),
            Strings s => new PredicateAtom.State.AnyString(s.Except),
            Numbers n => new PredicateAtom.State.AnyNumber(n.Integral, n.Lower, n.Upper),
            _ => new PredicateAtom.State.Unknown(),
        };

        /// <summary>The cell spelled one way, which it is ordered by.</summary>
        public string Text => this switch
        {
            Absent => "absent",
            Null => "null",
            Structure s => s.Type,
            Point p => p.Value,
            Strings s => "strings" + (s.Low is null ? "" : " > " + SqlValue.Quote(StringOf(s.Low))) + (s.High is null ? "" : " < " + SqlValue.Quote(StringOf(s.High)))
                + " in [" + string.Join(",", s.Classes.AsEnumerable()) + "]" + But(s.Except),
            Numbers n => (n.Integral ? "integers" : "fractions") + (n.Lower is Bound l ? (l.Open ? " > " : " >= ") + Number(l.Value) : "")
                + (n.Upper is Bound h ? (h.Open ? " < " : " <= ") + Number(h.Value) : "")
                + (n.DoubleLow is double dl ? " double >= " + dl.ToString("R", CultureInfo.InvariantCulture) : "")
                + (n.DoubleHigh is double dh ? " double <= " + dh.ToString("R", CultureInfo.InvariantCulture) : "") + But(n.Except),
            _ => "unknown",
        };

        static string But(ValueSet<string> except) => except.Count == 0 ? "" : " but " + string.Join(", ", except.Select(SqlValue.Quote));

        static string Number(decimal value) => Of(value).Substring(Of(value).IndexOf('|') + 1);

        /// <inheritdoc />
        public int CompareTo(PathCell? other) => other is null ? 1 : string.CompareOrdinal(Text, other.Text);

        /// <inheritdoc />
        public sealed override string ToString() => Text;

        /// <summary>Whether the cell holds a value, spelled as the remarks say.</summary>
        public bool Contains(string? value) => value is null ? this is Absent : this switch
        {
            Null => value == NullValue,
            Structure s => value == s.Type + "|",
            Point p => value == p.Value,
            Strings s => IsString(value) && s.Holds(StringOf(value)) && s.Except.Contains(value) == false,
            Numbers n => IsNumber(value) && TypeOf(value) == (n.Integral ? IntegerType : FractionType) && Within(value, n.Lower, n.Upper) && n.Rounds(value) && n.Except.Contains(value) == false,
            Unknown => AllTypes.Contains(TypeOf(value)) == false,
            _ => false,
        };

        /// <summary>
        /// Where the cell's numbers lie against a number as a value is boxed, by the service
        /// (<c>Service.NumbersBeyondLongCompareAsDoubles</c>) and by Calcite's raw columns (<c>Calcite.RawNullIsSqlNull</c>):
        /// a whole number within a long by its value, which the theory spells alike written with a point, so both ways, and
        /// any other by its double, by value. A side for each reading, <c>null</c> where not every number lies one way; none
        /// for a cell of no number; and <c>null</c> for a number neither a long nor a double, which no box holds as itself.
        /// </summary>
        public int?[]? SidesOf(decimal c)
        {
            var d = DoubleBound.Of(c);
            if ((c == decimal.Truncate(c) && c >= long.MinValue && c <= long.MaxValue) == false && DoubleBound.Compare(d, c) != 0)
                return null;

            return this switch
            {
                Point { Value: var v } when TryNumberOf(v, out var x) && x == decimal.Truncate(x) && x >= long.MinValue && x <= long.MaxValue =>
                    [x.CompareTo(c), new Numbers(true, Bound.AtLeast(x), Bound.AtMost(x), ValueSet<string>.Empty).RoundedSide(c)],
                Point { Value: var v } when IsNumber(v) => [DoubleBound.Of(v).CompareTo(d) is var side && side == 0 ? DoubleBound.Compare(d, c) : Math.Sign(side)],
                Numbers n when n.Integral && (n.Upper is not Bound u || u.Value >= long.MinValue) && (n.Lower is not Bound l || l.Value <= long.MaxValue) => [n.Side(c), n.RoundedSide(c)],
                Numbers n => [n.RoundedSide(c)],
                _ => [],
            };
        }

        /// <summary>One value the cell holds: <c>null</c> for the absent path, and for an interval nothing lies in.</summary>
        public string? Witness() => this switch
        {
            Absent => null,
            Null => NullValue,
            Structure s => s.Type + "|",
            Point p => p.Value,
            Strings s => LeastString(s),
            Numbers n when (n.DoubleLow ?? n.DoubleHigh) is not null => Rounded(n),
            Numbers { Integral: true } n => LeastInteger(n),
            Numbers n => SomeFraction(n),
            _ => throw new InvalidOperationException("Nothing is known of a kind the theory does not know, not a value of it either."),
        };

        /// <summary>
        /// Marks the cells a narrower value of the path admits nothing of — what the facts the atom's conjunct was rendered
        /// from say it holds, which only a document an earlier round's conjunct drops is outside — by cutting that value where
        /// the import was cut: a cell holding no inhabitant of the narrower value's cells. The import's cells are kept whole,
        /// and anything marks nothing.
        /// </summary>
        /// <remarks>
        /// Each of the narrower value's cells lies within one of the import's, since it is cut at the same points and admits
        /// no more, so a cell its inhabitants miss holds none of its values.
        /// </remarks>
        public static CellPartition SetAside(CellPartition cells, PathValues assumed, ValueSet<string> classes, ValueSet<CutPoint> cuts)
        {
            if (assumed == PathValues.Anything)
                return cells;

            var inside = Split(cells.Path, assumed, classes, cuts).Cells.ToList();
            bool Holds(PathCell cell) => inside.Any(c => c is Unknown ? cell is Unknown : cell.Contains(c.Witness()));
            return ValueSet.From(cells.Cells.Where(cell => Holds(cell) == false)) is { Count: > 0 } outside ? cells with { Outside = outside } : cells;
        }

        /// <summary>
        /// Cuts a path's import into cells at the cut points. A string or a number cut at is a point where the import
        /// admits it and a boundary where not, and an interval between two is kept where something lies in it.
        /// </summary>
        /// <param name="path">The path, spelled as <c>SqlValue.Spell</c> spells it.</param>
        /// <param name="x">What it may hold, as imported whole.</param>
        /// <param name="classes">The classes its strings' characters may be in.</param>
        /// <param name="cuts">Where to cut it.</param>
        public static CellPartition Split(string path, PathValues x, ValueSet<string> classes, ValueSet<CutPoint> cuts)
        {
            bool Admits(string value) => x.Admits(value) && Within(value, x.Lower, x.Upper) && CharactersOf(value).IsSubsetOf(classes);

            var cells = new List<PathCell>();
            foreach (var kind in x.Kinds)
            {
                switch (kind)
                {
                    case PathValues.AbsentKind:
                        cells.Add(new Absent());
                        break;

                    case NullType:
                        if (x.Admits(NullValue))
                            cells.Add(new Null());
                        break;

                    case ObjectType or ArrayType:
                        // A domain is a set of scalars, so a path with one holds no structure.
                        if (x.Values is null)
                            cells.Add(new Structure(kind));
                        break;

                    case BooleanType:
                        cells.AddRange(new[] { Of(true), Of(false) }.Where(Admits).Select(v => new Point(v)));
                        break;

                    case StringType or IntegerType or FractionType when x.Values is not null:
                        cells.AddRange(x.Values.Where(v => TypeOf(v) == kind && Admits(v)).Select(v => new Point(v)));
                        break;

                    case StringType:
                    {
                        string? low = null;
                        foreach (var high in cuts.Select(c => c.Value).Where(IsString).Distinct().OrderBy(v => v, StringComparer.Ordinal).Append<string?>(null))
                        {
                            var between = new Strings(low, high, ValueSet<string>.Empty, classes);
                            cells.Add(between with { Except = x.Excluded.Where(between.Contains) });
                            if (high is not null && Admits(high))
                                cells.Add(new Point(high));
                            low = high;
                        }

                        break;
                    }

                    case IntegerType or FractionType:
                    {
                        var doubles = cuts.Where(c => c.Kind == CutKind.Double && IsExactNumber(c.Value)).Select(c => DoubleBound.Of(c.Value)).Distinct().Order().ToList();
                        var lower = x.Lower;
                        foreach (var cut in cuts.Select(c => c.Value).Where(IsExactNumber).Select(NumberOf).Distinct().OrderBy(n => n).Select(n => (decimal?)n).Append(null))
                        {
                            var upper = cut is decimal below ? (x.Upper is Bound u ? Bound.StricterUpper(Bound.Below(below), u) : Bound.Below(below)) : x.Upper;
                            foreach (var between in Slots(new Numbers(kind == IntegerType, lower, upper, ValueSet<string>.Empty), doubles))
                                cells.Add(between with { Except = x.Excluded.Where(between.Contains) });
                            if (cut is not decimal point)
                                break;

                            if (TypeOf(Of(point)) == kind && Admits(Of(point)))
                                cells.Add(new Point(Of(point)));
                            lower = x.Lower is Bound l ? Bound.StricterLower(Bound.Above(point), l) : Bound.Above(point);
                        }

                        break;
                    }

                    default:
                        cells.Add(new Unknown());
                        break;
                }
            }

            return new CellPartition(path, ValueSet.From(cells.Where(c => c is not (Strings or Numbers) || c.Witness() is not null)));
        }

        /// <summary>
        /// An interval parted at each double a literal rounds to that its values' doubles may reach: the values whose double
        /// lies below it, at it and above it, so that a reading or the service comparing doubles answers one way on each.
        /// </summary>
        static IEnumerable<Numbers> Slots(Numbers interval, List<double> doubles)
        {
            double? low = null;
            foreach (var d in doubles.Where(d => (interval.Lower is not Bound l || d >= DoubleBound.Of(l.Value)) && (interval.Upper is not Bound u || d <= DoubleBound.Of(u.Value))))
            {
                yield return interval with { DoubleLow = low, DoubleHigh = Math.BitDecrement(d) };
                yield return interval with { DoubleLow = d, DoubleHigh = d };
                low = Math.BitIncrement(d);
            }

            yield return interval with { DoubleLow = low };
        }

        /// <summary>
        /// Whether a value lies within bounds, which bind numbers alone: by its value, or where a decimal does not hold it,
        /// by its double.
        /// </summary>
        static bool Within(string value, Bound? lower, Bound? upper)
        {
            if (IsNumber(value) == false)
                return true;

            if (TryNumberOf(value, out var x))
                return (lower is not Bound l || l.AdmitsAbove(x)) && (upper is not Bound u || u.AdmitsBelow(x));

            var d = ApproximateOf(value) ?? double.NaN;
            return (lower is not Bound lo || (lo.Open ? d > (double)lo.Value : d >= (double)lo.Value))
                && (upper is not Bound hi || (hi.Open ? d < (double)hi.Value : d <= (double)hi.Value));
        }

        /// <summary>Which UTF-16 units each set of classes allows, read once a set.</summary>
        static readonly ConcurrentDictionary<ValueSet<string>, bool[]> Units = new();

        /// <summary>
        /// The least string of an interval that is not excepted: the least above its low end whose units its classes allow,
        /// then each next in order — the last with the least unit allowed appended, nothing lying between — until one is
        /// not excepted, or one reaches the high end.
        /// </summary>
        static string? LeastString(Strings s)
        {
            var allowed = Units.GetOrAdd(s.Classes, k => Enumerable.Range(0, char.MaxValue + 1).Select(u => BandsOfText(((char)u).ToString()).IsSubsetOf(k)).ToArray());
            var least = Array.IndexOf(allowed, true);
            for (var text = s.Low is null ? "" : Above(StringOf(s.Low), allowed, least); text is not null; text = least < 0 ? null : text + (char)least)
            {
                if (s.High is not null && string.CompareOrdinal(text, StringOf(s.High)) >= 0)
                    return null;

                if (s.Except.Contains(Of(text)) == false)
                    return Of(text);
            }

            return null;
        }

        /// <summary>
        /// The least string above another whose units are all allowed: the other and the least unit, where its own units
        /// are allowed; otherwise a greater unit in place of the last of them one can stand in for.
        /// </summary>
        static string? Above(string low, bool[] allowed, int least)
        {
            var kept = 0;
            while (kept < low.Length && allowed[low[kept]])
                kept++;

            if (kept == low.Length && least >= 0)
                return low + (char)least;

            for (var i = Math.Min(kept, low.Length - 1); i >= 0; i--)
                for (var unit = low[i] + 1; unit <= char.MaxValue; unit++)
                    if (allowed[unit])
                        return low.Substring(0, i) + (char)unit;

            return null;
        }

        /// <summary>The integer of an interval nearest its lower bound, or else its upper, or else zero, that is not excepted.</summary>
        static string? LeastInteger(Numbers n)
        {
            if (n.Lower is Bound l && n.Upper is Bound h && Bound.NoIntegerBetween(l, h))
                return null;

            try
            {
                var (from, step) = n.Lower is Bound lo ? (Math.Ceiling(lo.Value) + (lo.Open && Math.Ceiling(lo.Value) == lo.Value ? 1 : 0), 1m)
                    : n.Upper is Bound hi ? (Math.Floor(hi.Value) - (hi.Open && Math.Floor(hi.Value) == hi.Value ? 1 : 0), -1m)
                    : (0m, 1m);

                for (var x = from; ; x += step)
                {
                    if (n.Contains(Of(x)))
                        return Of(x);

                    // Out of the interval, and not for being excepted: past its far end.
                    if (n.Except.Contains(Of(x)) == false)
                        return null;
                }
            }
            catch (OverflowException)
            {
                // Past a decimal's range: a number the theory holds approximately, as a document may.
                var beyond = Of(n.Lower is null ? -1e30 : 1e30);
                return n.Contains(beyond) ? beyond : null;
            }
        }

        /// <summary>
        /// A value of an interval whose double lies within its double bounds, not excepted. A double never falls as its number
        /// rises, so halving finds the least integer at or past the low double, or a number between the double bounds, beside
        /// which a fraction is stepped to; past a decimal's range, a number held approximately.
        /// </summary>
        static string? Rounded(Numbers n)
        {
            var (low, high) = (n.Lower?.Value ?? decimal.MinValue, n.Upper?.Value ?? decimal.MaxValue);
            var candidates = new List<decimal> { low, high };
            try
            {
                if (n.Integral)
                {
                    // The least integer at or past the low double, and upward; or the greatest within the high, and down.
                    var (a, b, up) = (Math.Ceiling(low), Math.Floor(high), n.DoubleLow is not null);
                    while (a < b)
                    {
                        // Halved from one end, which no rounding carries onto the other: past 28 digits a / 2 + b / 2 may
                        // round to b, and the search would stand still. Zero lies between ends of either sign.
                        var m = a < 0 && b > 0 ? 0 : up ? a + Math.Floor((b - a) / 2) : b - Math.Floor((b - a) / 2);
                        (a, b) = (up ? DoubleBound.Of(m) < n.DoubleLow : DoubleBound.Of(m) > n.DoubleHigh) ? (up ? (m + 1, b) : (a, m - 1)) : (up ? (a, m) : (m, b));
                    }

                    for (var i = 0; i < n.Except.Count + 2; i++)
                        candidates.Add(up ? a + i : a - i);
                }
                else
                {
                    // A value between the double bounds; then the least such past the low double, or the greatest within the
                    // high, near which fractions lie however large the interval; then steps beside it.
                    var (p, q, inside) = (low, high, (decimal?)null);
                    for (var m = p / 2 + q / 2; inside is null && m > p && m < q; m = p / 2 + q / 2)
                    {
                        if (DoubleBound.Of(m) < n.DoubleLow)
                            p = m;
                        else if (DoubleBound.Of(m) > n.DoubleHigh)
                            q = m;
                        else
                            inside = m;
                    }

                    if (inside is decimal edge)
                    {
                        var up = n.DoubleLow is not null;
                        for (var (a, b) = up ? (p, edge) : (edge, q); a / 2 + b / 2 is var m && m > a && m < b;)
                        {
                            if (up ? DoubleBound.Of(m) < n.DoubleLow : DoubleBound.Of(m) > n.DoubleHigh)
                                (a, b) = up ? (m, b) : (a, m);
                            else
                                (a, b, edge) = up ? (a, m, m) : (m, b, m);
                        }

                        candidates.Add(edge);
                        for (var k = (byte)0; k <= 28; k++)
                            candidates.AddRange([edge + new decimal(1, 0, 0, false, k), edge - new decimal(1, 0, 0, false, k)]);
                    }
                }
            }
            catch (OverflowException)
            {
                // Past a decimal's range, where only a number held approximately lies.
            }

            return candidates.Select(Of).Concat(new[] { 1e30, -1e30, 1e-30, -1e-30 }.Select(Of)).FirstOrDefault(n.Contains);
        }

        /// <summary>
        /// A fraction of an interval that is not excepted: halfway from its lower end to the least excepted value above it
        /// or its upper end, halved toward the lower until it is no integer.
        /// </summary>
        static string? SomeFraction(Numbers n)
        {
            // An interval of one value, or of none.
            if (n.Lower is Bound l && n.Upper is Bound h && (Bound.NoNumberBetween(l, h) || l.Value == h.Value))
                return n.Contains(Of(l.Value)) ? Of(l.Value) : null;

            try
            {
                var excepted = n.Except.AsEnumerable().Where(IsExactNumber).Select(NumberOf).ToList();
                var low = n.Lower?.Value ?? excepted.Append(n.Upper?.Value ?? 1m).Min() - 1;
                var high = excepted.Where(e => e > low).Append(n.Upper?.Value ?? low + 1).Min();
                for (var x = low / 2 + high / 2; x > low; x = low / 2 + x / 2)
                    if (n.Contains(Of(x)))
                        return Of(x);
            }
            catch (OverflowException)
            {
                // No decimal lies there, and no double past 2^53 is a fraction.
            }

            return null;
        }

    }

}
