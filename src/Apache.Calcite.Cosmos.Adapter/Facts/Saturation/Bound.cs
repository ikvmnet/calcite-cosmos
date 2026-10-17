using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A bound on a number: the value, and whether the bound itself is excluded.
    /// </summary>
    internal readonly record struct Bound(decimal Value, bool Open)
    {

        public static Bound AtLeast(decimal value) => new(value, false);

        public static Bound Above(decimal value) => new(value, true);

        public static Bound AtMost(decimal value) => new(value, false);

        public static Bound Below(decimal value) => new(value, true);

        /// <summary>Whether a number satisfies this as a lower bound.</summary>
        public bool AdmitsAbove(decimal x) => x > Value || x == Value && Open == false;

        /// <summary>Whether a number satisfies this as an upper bound.</summary>
        public bool AdmitsBelow(decimal x) => x < Value || x == Value && Open == false;

        /// <summary>The stricter of two lower bounds: the larger, and the open one where they are equal.</summary>
        public static Bound StricterLower(Bound a, Bound b) =>
            a.Value > b.Value ? a : b.Value > a.Value ? b : new(a.Value, a.Open || b.Open);

        /// <summary>The stricter of two upper bounds: the smaller, and the open one where they are equal.</summary>
        public static Bound StricterUpper(Bound a, Bound b) =>
            a.Value < b.Value ? a : b.Value < a.Value ? b : new(a.Value, a.Open || b.Open);

        /// <summary>The weaker of two lower bounds: what holds whichever of two cases does.</summary>
        public static Bound WeakerLower(Bound a, Bound b) =>
            a.Value < b.Value ? a : b.Value < a.Value ? b : new(a.Value, a.Open && b.Open);

        /// <summary>The weaker of two upper bounds: what holds whichever of two cases does.</summary>
        public static Bound WeakerUpper(Bound a, Bound b) =>
            a.Value > b.Value ? a : b.Value > a.Value ? b : new(a.Value, a.Open && b.Open);

        /// <summary>Whether no number lies between a lower and an upper bound.</summary>
        public static bool NoNumberBetween(Bound lower, Bound upper) =>
            lower.Value > upper.Value || lower.Value == upper.Value && (lower.Open || upper.Open);

        /// <summary>Whether no integer lies between a lower and an upper bound.</summary>
        public static bool NoIntegerBetween(Bound lower, Bound upper)
        {
            var least = Math.Ceiling(lower.Value);
            if (least == lower.Value && lower.Open)
                least += 1;

            var most = Math.Floor(upper.Value);
            if (most == upper.Value && upper.Open)
                most -= 1;

            return least > most;
        }

        public override string ToString() => (Open ? "open " : "") + Value.ToString(CultureInfo.InvariantCulture);

    }

}
