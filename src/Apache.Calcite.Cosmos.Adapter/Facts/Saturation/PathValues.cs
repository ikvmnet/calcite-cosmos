using System.Linq;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// What a path may hold in the documents a context admits: whether it may be absent, the kinds of value it may hold,
    /// the scalars it may be, scalars it is not, and the bounds of a number. One value per path, so that a verdict over
    /// a predicate reads one row rather than the half-dozen tables it is gathered from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A meet of what the tables say, and nothing they do not.</b> <see cref="Kinds"/> is the JSON types and
    /// <see cref="AbsentKind"/>: presence removes the last, and a set of types narrows the rest. <see cref="Values"/>
    /// is a domain, <c>null</c> where none is known; <see cref="Excluded"/> the disequalities; the bounds bind
    /// numbers only, as the theory's do. A path no fact speaks of may hold <see cref="Anything"/>.
    /// </para>
    /// <para>
    /// <b>Every value is normalised</b>, so that one state of knowledge has one spelling and the lattice settles: a
    /// domain keeps only members of a kind the path may hold and within its bounds, and once a domain is known the
    /// disequalities are folded into it.
    /// </para>
    /// <para>
    /// <b>A bound is about the number's value</b>, whether a decimal holds it or not. The theory keeps a number it
    /// holds approximately whatever a bound says, because it will not compare one; a verdict reads the bound as the
    /// statement about values it is, which a stored <c>-1e30</c> beneath a declared <c>minimum: 0</c> violates.
    /// </para>
    /// </remarks>
    /// <param name="Kinds">The JSON types the path may hold, and <see cref="AbsentKind"/> where it may not be there.</param>
    /// <param name="Values">The scalars the path holds if it is there, or <c>null</c> where none are known.</param>
    /// <param name="Excluded">Scalars the path does not hold.</param>
    /// <param name="Lower">The least a number there may be, or <c>null</c>.</param>
    /// <param name="Upper">The most a number there may be, or <c>null</c>.</param>
    internal sealed record PathValues(ValueSet<string> Kinds, ValueSet<string>? Values, ValueSet<string> Excluded, Bound? Lower, Bound? Upper)
    {

        /// <summary>
        /// The kind of a path that is not there, beside the seven JSON types.
        /// </summary>
        public const string AbsentKind = "absent";

        /// <summary>
        /// Every JSON type, and absence.
        /// </summary>
        public static readonly ValueSet<string> AllKinds = AllTypes.Add(AbsentKind);

        /// <summary>
        /// The JSON types that are scalars other than null: what a text accessor renders.
        /// </summary>
        public static readonly ValueSet<string> Rendered = ValueSet.Of(StringType, IntegerType, FractionType, BooleanType);

        /// <summary>
        /// What a path no fact speaks of may hold.
        /// </summary>
        public static readonly PathValues Anything = new(AllKinds, null, ValueSet<string>.Empty, null, null);

        /// <summary>
        /// What a path that is there may hold.
        /// </summary>
        public static readonly PathValues AnythingThere = Anything with { Kinds = AllTypes };

        /// <summary>
        /// The lattice: knowledge only narrows, and a case join keeps what either case allows.
        /// </summary>
        public static readonly ValueLattice<PathValues> Lattice = new("narrower values", Meet, Join);

        /// <summary>
        /// Returns what may be held where it is there, of these kinds and nothing else.
        /// </summary>
        public static PathValues OfKinds(ValueSet<string> kinds) => Normal(Anything with { Kinds = kinds });

        /// <summary>
        /// Returns what may be held by a path of these types, if it is there.
        /// </summary>
        public static PathValues OfTypes(ValueSet<string> types) => Normal(Anything with { Kinds = types.Add(AbsentKind) });

        /// <summary>
        /// Returns what may be held by a path whose value, if it is there, is one of these.
        /// </summary>
        public static PathValues OneOf(ValueSet<string> values) => Normal(Anything with { Values = values });

        /// <summary>
        /// Returns what may be held by a path that does not hold this.
        /// </summary>
        public static PathValues Not(string value) => Anything with { Excluded = ValueSet.Of(value) };

        /// <summary>
        /// Returns what may be held by a path whose number, if it holds one, is at least this.
        /// </summary>
        public static PathValues Above(Bound lower) => Anything with { Lower = lower };

        /// <summary>
        /// Returns what may be held by a path whose number, if it holds one, is at most this.
        /// </summary>
        public static PathValues Below(Bound upper) => Anything with { Upper = upper };

        /// <summary>
        /// Gets the types the path may hold if it is there.
        /// </summary>
        public ValueSet<string> Types => Kinds.Remove(AbsentKind);

        /// <summary>
        /// Gets whether the path is there wherever the context admits a document.
        /// </summary>
        public bool IsPresent => Kinds.Contains(AbsentKind) == false;

        /// <summary>
        /// The meet of two states of knowledge: what both allow.
        /// </summary>
        public static PathValues Meet(PathValues a, PathValues b) => Normal(new(
            a.Kinds.Intersect(b.Kinds),
            a.Values is null ? b.Values : b.Values is null ? a.Values : a.Values.Intersect(b.Values),
            a.Excluded.Union(b.Excluded),
            Stricter(a.Lower, b.Lower, Bound.StricterLower),
            Stricter(a.Upper, b.Upper, Bound.StricterUpper)));

        /// <summary>
        /// The join of two states of knowledge: what either allows, which is what holds whichever case a document is in.
        /// </summary>
        public static PathValues Join(PathValues a, PathValues b)
        {
            // A value is excluded on a side whose domain leaves it out as surely as on one that excludes it outright,
            // and it stays excluded only where both sides exclude it.
            bool Out(PathValues side, string value) => side.Values is not null ? side.Values.Contains(value) == false : side.Excluded.Contains(value);

            var excluded = ValueSet.From(a.Excluded.Union(b.Excluded).Where(v => Out(a, v) && Out(b, v)));

            return Normal(new(
                a.Kinds.Union(b.Kinds),
                a.Values is null || b.Values is null ? null : a.Values.Union(b.Values),
                a.Values is null || b.Values is null ? excluded : ValueSet<string>.Empty,
                a.Lower is Bound la && b.Lower is Bound lb ? Bound.WeakerLower(la, lb) : null,
                a.Upper is Bound ua && b.Upper is Bound ub ? Bound.WeakerUpper(ua, ub) : null));
        }

        /// <summary>
        /// Whether a scalar may be the value: of a kind the path may hold, in its domain, not excluded, and within its
        /// bounds where it is a number whose value a decimal holds.
        /// </summary>
        public bool Admits(string value)
        {
            if (Kinds.Contains(TypeOf(value)) == false)
                return false;

            if (Values is not null && Values.Contains(value) == false)
                return false;

            if (Excluded.Contains(value))
                return false;

            if (TryNumberOf(value, out var x))
                return (Lower is not Bound l || l.AdmitsAbove(x)) && (Upper is not Bound u || u.AdmitsBelow(x));

            return true;
        }

        /// <summary>
        /// Normalises a state of knowledge: a domain keeps only what may be the value, and absorbs the exclusions.
        /// </summary>
        static PathValues Normal(PathValues x)
        {
            if (x.Values is null)
                return x;

            var values = x.Values.Where(v => x.Kinds.Contains(TypeOf(v))
                && x.Excluded.Contains(v) == false
                && (TryNumberOf(v, out var n) == false || (x.Lower is not Bound l || l.AdmitsAbove(n)) && (x.Upper is not Bound u || u.AdmitsBelow(n))));

            return x with { Values = values, Excluded = ValueSet<string>.Empty };
        }

        static Bound? Stricter(Bound? a, Bound? b, System.Func<Bound, Bound, Bound> stricter) =>
            a is Bound x ? b is Bound y ? stricter(x, y) : x : b;

        /// <inheritdoc />
        public override string ToString() =>
            $"[{string.Join(", ", Kinds)}]" + (Values is null ? "" : $" in {Values}") + (Excluded.Count == 0 ? "" : $" not {Excluded}")
                + (Lower is Bound lo ? $" >{(lo.Open ? "" : "=")} {lo.Value}" : "") + (Upper is Bound hi ? $" <{(hi.Open ? "" : "=")} {hi.Value}" : "");

    }

}
