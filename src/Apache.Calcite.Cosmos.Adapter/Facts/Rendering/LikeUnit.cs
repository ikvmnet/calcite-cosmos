using System;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What one side's <c>LIKE</c> reads one unit of a pattern as (<c>likeunits</c> for Calcite, <c>likereads</c> for each
    /// target, G7), as a row measured it: a run of any characters, one code point in no class <see cref="Except"/> names,
    /// the character itself, or a reading no evaluation here reads (<see cref="Range"/>, <see cref="Unmeasured"/>). A unit
    /// spelled empty is every unit but those <see cref="Besides"/> names, the units the side's other rows name, which the
    /// reader reads off the rows beside it: so a unit is read by exactly one row of a side, or by none.
    /// </summary>
    /// <param name="Unit">The pattern's character, or empty for every other.</param>
    /// <param name="Reads"><see cref="Run"/>, <see cref="One"/>, <see cref="Itself"/>, <see cref="Range"/> or <see cref="Unmeasured"/>.</param>
    /// <param name="Except">The character class one code point is not in; empty for none.</param>
    /// <param name="Over">The character classes the reading was measured over, a code point of any other being unread by it;
    /// <c>null</c> for every class.</param>
    /// <param name="Besides">For the unit spelled empty, the units the side's other rows name; none for any other.</param>
    internal readonly record struct LikeUnit(string Unit, string Reads, string Except, ValueSet<string>? Over, ValueSet<string> Besides) : IComparable<LikeUnit>
    {

        /// <summary>Any run of characters, the empty one among them.</summary>
        public const string Run = "run";

        /// <summary>One code point.</summary>
        public const string One = "one";

        /// <summary>The character itself.</summary>
        public const string Itself = "itself";

        /// <summary>A bracket opening a range of characters, which no evaluation here reads.</summary>
        public const string Range = "range";

        /// <summary>What no witness measured, which no evaluation reads.</summary>
        public const string Unmeasured = "unmeasured";

        /// <summary>
        /// The row a side reads a unit of a pattern by: the one naming it, or the one spelled empty that names it not
        /// besides; <c>null</c> where none does, so a pattern holding it is unread. Two rows of one side reading one unit fail
        /// the run: the side's rows say two things of it.
        /// </summary>
        public static LikeUnit? Of(ValueSet<LikeUnit> units, string unit)
        {
            LikeUnit? row = null;
            foreach (var u in units)
                if (u.Unit == unit || u.Unit.Length == 0 && u.Besides.Contains(unit) == false)
                    row = row is { } other ? throw new InvalidOperationException($"LIKE reads {unit} by two rows: {other} and {u}.") : u;

            return row;
        }

        /// <summary>Whether the reading was measured over every class of a set: a code point of one it was not is unread.</summary>
        public bool Measures(ValueSet<string> classes) => Over is null || classes.IsSubsetOf(Over);

        /// <inheritdoc />
        public int CompareTo(LikeUnit other) => string.CompareOrdinal(ToString(), other.ToString());

        /// <summary>The unit spelled <c>unit:reads-except@over^besides</c>.</summary>
        public override string ToString() => Unit + ":" + Reads + (Except.Length == 0 ? "" : "-" + Except) + (Over is null ? "" : "@" + string.Join(",", Over.AsEnumerable()))
            + (Besides is { Count: > 0 } ? "^" + string.Join(",", Besides.AsEnumerable()) : "");

        /// <summary>A row of a run, read by the definition of an affix test, its argument's ends.</summary>
        internal static LikeUnit Ends { get; } = new("%", Run, "", null, ValueSet<string>.Empty);

        /// <summary>A row of one unit itself, read by the definition of an affix test: its argument is literal.</summary>
        internal static LikeUnit Literal(string unit) => new(unit, Itself, "", null, ValueSet<string>.Empty);

    }

}
