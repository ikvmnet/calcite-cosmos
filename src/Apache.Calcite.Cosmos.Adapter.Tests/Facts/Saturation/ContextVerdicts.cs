using System;
using System.Linq;

using Eggnog;

using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// What the theory's own tests read a context's verdict on a <see cref="JsonAtom"/> by: the tables the rules saturated,
    /// read as one document's predicate would be answered over every document the context admits.
    /// </summary>
    /// <remarks>
    /// A reference reading, and the tests' alone. It restates a few of the theory's rules over the tables — no number lies
    /// between crossed bounds, nor an integer between bounds closer than one; a value outside a length, a multiple or an
    /// exclusion cannot be held — so that a scenario can say what a schema's facts decide in one line. The planner never
    /// asks it: the oracle's verdicts are <c>PredicateRules</c>' Decide stratum, run by the engine, and what C# still decides
    /// of the documents is listed in <c>DESIGN.md</c>, <em>What is left in C#</em>. It was <c>JsonContexts.Decide</c>, which
    /// nothing in production called.
    /// </remarks>
    internal static class ContextVerdicts
    {

        /// <summary>What every document the context admits says about a predicate.</summary>
        public static Truth Decide(this JsonContexts contexts, Scope scope, JsonAtom atom)
        {
            // A context no document satisfies keeps no row, whatever is asked of it.
            if (scope.IsContradictory)
                return Truth.Never;

            var r = contexts.Rules;
            var p = atom.Path.ToTerm(r);
            var present = scope.Check(r.Present.Of(p));
            var absent = scope.Check(r.Absent.Of(p));
            var types = scope.TryGet(r.Types.Of(p), out var t) ? t : AllTypes;
            var dom = scope.TryGet(r.Dom.Of(p), out var d) ? d : null;

            if (absent)
                return Truth.Never;

            switch (atom)
            {
                case JsonAtom.Defined:
                    return present ? Truth.Always : Truth.Unknown;

                case JsonAtom.HasType has:
                {
                    var wanted = JsonValues.Types(has.Type);
                    if (types.Any(wanted.Contains) == false)
                        return Truth.Never;
                    return present && types.IsSubsetOf(wanted) ? Truth.Always : Truth.Unknown;
                }

                case JsonAtom.EqualTo eq:
                {
                    if (CannotHold(r, scope, p, types, dom, eq.Value))
                        return Truth.Never;
                    return present && dom is not null && dom.IsSubsetOf(ValueSet.Of(eq.Value)) ? Truth.Always : Truth.Unknown;
                }

                case JsonAtom.NumberIn range:
                {
                    if (types.Any(Numbers.Contains) == false)
                        return Truth.Never;

                    var lo = scope.TryGet(r.Lo.Of(p), out var l) ? l : (Bound?)null;
                    var hi = scope.TryGet(r.Hi.Of(p), out var h) ? h : (Bound?)null;
                    var lower = Stricter(lo, range.Lower, Bound.StricterLower);
                    var upper = Stricter(hi, range.Upper, Bound.StricterUpper);
                    if (lower is Bound a && upper is Bound b)
                    {
                        var integral = types.Contains(FractionType) == false;
                        if (Bound.NoNumberBetween(a, b) || integral && Bound.NoIntegerBetween(a, b))
                            return Truth.Never;
                    }

                    if (dom is not null)
                    {
                        // A number held approximately may lie anywhere, so it keeps the range from being refuted and
                        // from being proven.
                        var numbers = dom.Where(IsExactNumber).Select(NumberOf).ToList();
                        var approximate = dom.Any(v => IsNumber(v) && IsExactNumber(v) == false);
                        if (approximate == false && numbers.Any(x => In(x, range.Lower, range.Upper)) == false)
                            return Truth.Never;
                        if (present && dom.All(IsExactNumber) && numbers.All(x => In(x, range.Lower, range.Upper)))
                            return Truth.Always;
                    }

                    var within = (range.Lower is null || lo is Bound pl && Bound.StricterLower(pl, range.Lower.Value) == pl)
                              && (range.Upper is null || hi is Bound ph && Bound.StricterUpper(ph, range.Upper.Value) == ph);
                    return present && types.IsSubsetOf(Numbers) && within ? Truth.Always : Truth.Unknown;
                }

                case JsonAtom.LengthIn length:
                {
                    if (types.Contains(ArrayType) == false)
                        return Truth.Never;

                    var min = Math.Max(scope.TryGet(r.MinItems.Of(p), out var m) ? m : 0, length.Min ?? 0);
                    var max = Math.Min(scope.TryGet(r.MaxItems.Of(p), out var x) ? x : long.MaxValue, length.Max ?? long.MaxValue);
                    if (min > max)
                        return Truth.Never;

                    var known = scope.TryGet(r.MinItems.Of(p), out var km) ? km : 0;
                    var knownMax = scope.TryGet(r.MaxItems.Of(p), out var kx) ? kx : long.MaxValue;
                    var within = known >= (length.Min ?? 0) && knownMax <= (length.Max ?? long.MaxValue);
                    return present && types.IsSubsetOf(Types(ArrayType)) && within ? Truth.Always : Truth.Unknown;
                }

                case JsonAtom.ArrayHolds holds:
                {
                    if (types.Contains(ArrayType) == false)
                        return Truth.Never;
                    if (scope.TryGet(r.MaxItems.Of(p), out var most) && most == 0)
                        return Truth.Never;

                    var each = r.Each.Of(p);
                    if (scope.Check(r.Absent.Of(each)))
                        return Truth.Never;
                    var eachTypes = scope.TryGet(r.Types.Of(each), out var et) ? et : AllTypes;
                    var eachDom = scope.TryGet(r.Dom.Of(each), out var ed) ? ed : null;
                    if (CannotHold(r, scope, each, eachTypes, eachDom, holds.Value))
                        return Truth.Never;

                    if (scope.TryGet(r.ContainsAtMost.Of(p, holds.Value), out var none) && none == 0)
                        return Truth.Never;

                    var held = scope.Check(r.Contains.Of(p, holds.Value))
                        || present && types.IsSubsetOf(Types(ArrayType)) && scope.TryGet(r.ContainsAtLeast.Of(p, holds.Value), out var least) && least >= 1;
                    return held ? Truth.Always : Truth.Unknown;
                }

                case JsonAtom.SameAs same:
                {
                    var q = same.Other.ToTerm(r);
                    if (scope.Check(r.Absent.Of(q)))
                        return Truth.Never;

                    var otherTypes = scope.TryGet(r.Types.Of(q), out var ot) ? ot : AllTypes;
                    if (types.Any(otherTypes.Contains) == false)
                        return Truth.Never;
                    if (dom is not null && scope.TryGet(r.Dom.Of(q), out var od) && dom.Any(od.Contains) == false && (types.IsSubsetOf(Scalars) || otherTypes.IsSubsetOf(Scalars)))
                        return Truth.Never;

                    var equal = scope.AreEqual(scope.Make((ConstructorTerm<JPath>)p), q);
                    return equal && present ? Truth.Always : Truth.Unknown;
                }

                default:
                    throw new NotSupportedException(atom.GetType().Name);
            }
        }

        /// <summary>Whether the facts about a path leave no room for it to hold a value.</summary>
        static bool CannotHold(JsonRules r, Scope scope, Term<JPath> p, ValueSet<string> types, ValueSet<string>? dom, string value)
        {
            if (types.Contains(TypeOf(value)) == false)
                return true;
            if (dom is not null && dom.Contains(value) == false)
                return true;
            if (scope.Check(r.Ne.Of(p, value)))
                return true;

            if (TryNumberOf(value, out var x))
            {
                if (scope.TryGet(r.MultipleOf.Of(p), out var m) && Multiples.IsMultiple(x, m) == false)
                    return true;
                if (scope.TryGet(r.Lo.Of(p), out var lo) && lo.AdmitsAbove(x) == false)
                    return true;
                if (scope.TryGet(r.Hi.Of(p), out var hi) && hi.AdmitsBelow(x) == false)
                    return true;
            }

            if (IsString(value))
            {
                var length = StringOf(value).Length;
                if (scope.TryGet(r.MinLength.Of(p), out var min) && length < min)
                    return true;
                if (scope.TryGet(r.MaxLength.Of(p), out var max) && length > max)
                    return true;
            }

            return false;
        }

        static Bound? Stricter(Bound? a, Bound? b, Func<Bound, Bound, Bound> stricter) =>
            a is Bound x ? b is Bound y ? stricter(x, y) : x : b;

        static bool In(decimal x, Bound? lower, Bound? upper) =>
            (lower is not Bound l || l.AdmitsAbove(x)) && (upper is not Bound u || u.AdmitsBelow(x));

    }

}
