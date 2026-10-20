using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// One cell of a path's import: a set of the values it may hold, inside one <see cref="PredicateAtom.State"/>, and never
    /// empty. <see cref="Split"/> cuts an import at the values a pair compares with, so that each side answers one way on
    /// as many cells as it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A value is <see cref="JsonValues"/>' spelling</b>, a structure its type and <c>|</c> (<c>object|</c>), and
    /// <c>null</c> the absent path. A number is its value, so no cell parts a stored <c>30</c> from <c>30.0</c>.
    /// </para>
    /// <para>
    /// <b>Strings are cut in Calcite's order, by UTF-16 unit</b> (<c>Calcite.TextOrderIsByCodeUnit</c>), and an interval
    /// holds only strings whose classes the path's allow, read through <see cref="BandsOfText"/>: a string's classes are its
    /// units'. Numbers are cut by value; the double a number rounds to is not yet a cut.
    /// </para>
    /// <para>
    /// <b>Every cell has an inhabitant</b> (<see cref="Witness"/>), and <see cref="Split"/> keeps no interval without one: a
    /// refutation names a cell, and a cell nothing lies in refutes nothing. The search is complete — an interval's strings are
    /// tried from its least, in order, and its numbers from its bounds — so an interval is dropped only where the import
    /// admits nothing in it: below <c>""</c>, between <c>l</c> and <c>l</c> + U+0000, where no number of the kind lies
    /// between the bounds.
    /// </para>
    /// </remarks>
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

        /// <summary>The numbers of one kind within the bounds, but these.</summary>
        public sealed record Numbers(bool Integral, Bound? Lower, Bound? Upper, ValueSet<string> Except) : PathCell;

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
                + (n.Upper is Bound h ? (h.Open ? " < " : " <= ") + Number(h.Value) : "") + But(n.Except),
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
            Numbers n => IsNumber(value) && TypeOf(value) == (n.Integral ? IntegerType : FractionType) && Within(value, n.Lower, n.Upper) && n.Except.Contains(value) == false,
            Unknown => AllTypes.Contains(TypeOf(value)) == false,
            _ => false,
        };

        /// <summary>One value the cell holds: <c>null</c> for the absent path, and for an interval nothing lies in.</summary>
        public string? Witness() => this switch
        {
            Absent => null,
            Null => NullValue,
            Structure s => s.Type + "|",
            Point p => p.Value,
            Strings s => LeastString(s),
            Numbers { Integral: true } n => LeastInteger(n),
            Numbers n => SomeFraction(n),
            _ => throw new InvalidOperationException("Nothing is known of a kind the theory does not know, not a value of it either."),
        };

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
                        var lower = x.Lower;
                        foreach (var cut in cuts.Select(c => c.Value).Where(IsExactNumber).Select(NumberOf).Distinct().OrderBy(n => n).Select(n => (decimal?)n).Append(null))
                        {
                            var upper = cut is decimal below ? (x.Upper is Bound u ? Bound.StricterUpper(Bound.Below(below), u) : Bound.Below(below)) : x.Upper;
                            var between = new Numbers(kind == IntegerType, lower, upper, ValueSet<string>.Empty);
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
