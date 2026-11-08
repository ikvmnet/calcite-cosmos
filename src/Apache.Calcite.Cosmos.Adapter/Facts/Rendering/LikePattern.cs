using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A pattern as one side reads it (G7): each code point written, with the row that side reads it by
    /// (<see cref="LikeUnit"/>). Its spelling (<see cref="Spelled"/>, <see cref="AffixPattern"/>) cuts a cell and keys its
    /// halves: <c>%</c> a run, <c>_</c> one code point of any class, a backslash before a unit it makes literal, any other
    /// unit itself. A side whose <c>_</c> misses a class, or whose reading was not measured over one, parts from the
    /// spelling over a cell that may hold it, which <see cref="OnLike"/> and <see cref="OnNotLike"/> answer; over a string it
    /// is matched as read (<see cref="Matches"/>). Arithmetic on a pattern and the rows handed it: it reads no row itself.
    /// </summary>
    internal sealed class LikePattern
    {

        LikePattern(IReadOnlyList<(LikeUnit Row, string Unit)> units)
        {
            Units = units;
            var (spelled, run) = (new StringBuilder(), false);
            foreach (var (row, unit) in units)
            {
                if (row.Reads != LikeUnit.Run || run == false)
                    spelled.Append(row.Reads switch { LikeUnit.Run => "%", LikeUnit.One => "_", _ => AffixPattern.Literal(unit) });

                run = row.Reads == LikeUnit.Run;
            }

            Spelled = spelled.ToString();
        }

        /// <summary>Each code point written, in order, with the row it is read by.</summary>
        public IReadOnlyList<(LikeUnit Row, string Unit)> Units { get; }

        /// <summary>The pattern spelled as cells are cut by it, every run of <c>%</c> one (<see cref="AffixPattern"/>).</summary>
        public string Spelled { get; }

        /// <summary>Whether a unit is read as one code point.</summary>
        public bool HoldsOne => Units.Any(u => u.Row.Reads == LikeUnit.One);

        /// <summary>
        /// A pattern as a side reads it, each code point by the one row of the side that reads it; <c>null</c> where a unit is
        /// read by no row, read as a reading no evaluation here reads, or read as itself by a row that measured none of its
        /// classes: a pattern the side's rows leave unread.
        /// </summary>
        public static LikePattern? Read(string pattern, ValueSet<LikeUnit> units)
        {
            var read = new List<(LikeUnit, string)>();
            foreach (var unit in AffixPattern.CodePoints(pattern))
            {
                if (LikeUnit.Of(units, unit) is not { } row || row.Reads is not (LikeUnit.Run or LikeUnit.One or LikeUnit.Itself)
                    || row.Reads == LikeUnit.Itself && row.Measures(JsonValues.BandsOfText(unit)) == false)
                    return null;

                read.Add((row, unit));
            }

            return new(read);
        }

        /// <summary>
        /// What an affix test asks with its argument, by the test's definition: the argument's units each itself, a run after
        /// it for a prefix, before it for a suffix and on both sides for a containment.
        /// </summary>
        public static LikePattern Affix(bool before, string argument, bool after)
        {
            var units = new List<(LikeUnit, string)>();
            if (before)
                units.Add((LikeUnit.Ends, "%"));

            units.AddRange(AffixPattern.CodePoints(argument).Select(u => (LikeUnit.Literal(u), u)));
            if (after)
                units.Add((LikeUnit.Ends, "%"));

            return new(units);
        }

        /// <summary>
        /// Whether a string matches the pattern as read, unit by unit over its code points: TRUE where some reading of it
        /// matches, FALSE where none can, and <c>null</c> where only one that reads a code point its row was not measured
        /// over might.
        /// </summary>
        public bool? Matches(string text)
        {
            // Kleene's values a step: 0 FALSE, 1 unknown, 2 TRUE; a reading's is its least step's, the match the best reading's.
            var points = AffixPattern.CodePoints(text).ToList();
            var reach = new int[points.Count + 1];
            reach[0] = 2;
            foreach (var (row, unit) in Units)
            {
                var next = new int[points.Count + 1];
                for (var j = 0; j <= points.Count; j++)
                    next[j] = row.Reads switch
                    {
                        LikeUnit.Run => Math.Max(reach[j], j > 0 ? Math.Min(next[j - 1], Step(row, points[j - 1], true)) : 0),
                        LikeUnit.One => j > 0 ? Math.Min(reach[j - 1], Step(row, points[j - 1], false)) : 0,
                        _ => j > 0 && points[j - 1] == unit ? reach[j - 1] : 0,
                    };

                reach = next;
            }

            return reach[points.Count] switch { 2 => true, 0 => false, _ => null };
        }

        /// <summary>A code point read by a run or by one: unknown where its row was not measured over its classes, FALSE where one misses its class.</summary>
        static int Step(LikeUnit row, string point, bool run)
        {
            var classes = JsonValues.BandsOfText(point);
            return row.Measures(classes) == false ? 1 : run == false && row.Except.Length > 0 && classes.Contains(row.Except) ? 0 : 2;
        }

        /// <summary>
        /// The pattern as read, over a cell of strings its spelling matches, whose characters are of the classes: TRUE where
        /// every run and one was measured over them and no one misses any of them, as the spelling's matches; <c>null</c>
        /// otherwise.
        /// </summary>
        public bool? OnLike(ValueSet<string> classes) =>
            Units.All(u => u.Row.Reads == LikeUnit.Itself || u.Row.Measures(classes) && (u.Row.Reads == LikeUnit.Run || u.Row.Except.Length == 0 || classes.Contains(u.Row.Except) == false))
                ? true : null;

        /// <summary>
        /// The pattern as read, over a cell of strings its spelling misses: FALSE where every run and one was measured over
        /// the classes, a one that misses a class matching no more than the spelling's; <c>null</c> otherwise.
        /// </summary>
        public bool? OnNotLike(ValueSet<string> classes) => Units.All(u => u.Row.Reads == LikeUnit.Itself || u.Row.Measures(classes)) ? false : null;

        /// <summary>The pattern with each unit read as itself spelled as a function spells it, as a matcher that reads a stored spelling is sent it.</summary>
        public LikePattern Respelled(Func<string, string> spell) =>
            new([.. Units.SelectMany(u => u.Row.Reads == LikeUnit.Itself ? AffixPattern.CodePoints(spell(u.Unit)).Select(p => (LikeUnit.Literal(p), p)) : Enumerable.Repeat(u, 1))]);

        /// <summary>
        /// The pattern as it was written, every run of <c>%</c> one: what a <c>LIKE</c> is sent, which the service reads by its
        /// own rows.
        /// </summary>
        public string Written => string.Concat(Units.Where((u, i) => u.Row.Reads != LikeUnit.Run || i == 0 || Units[i - 1].Row.Reads != LikeUnit.Run).Select(u => u.Unit));

        /// <summary>The rows each unit is read by.</summary>
        public ValueSet<LikeUnit> Rows => ValueSet.From(Units.Select(u => u.Row));

    }

}
