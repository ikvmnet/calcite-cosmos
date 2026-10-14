using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Eggnog;

using static JsonTheory.JsonValues;

namespace JsonTheory
{

    /// <summary>
    /// A path written the way a person writes one: <c>$</c>, <c>$.a.b</c>, <c>$.tags[10]</c>, and <c>$.items[*].id</c>
    /// for property <c>id</c> of every element of <c>items</c>.
    /// </summary>
    public sealed record JsonPathSpec(IReadOnlyList<object> Segments)
    {

        /// <summary>The segment that stands for every element of an array.</summary>
        public static readonly object Every = new();

        public static JsonPathSpec Root { get; } = new(System.Array.Empty<object>());

        public JsonPathSpec this[string property] => new(Segments.Append(property).ToArray());

        public JsonPathSpec this[long index] => new(Segments.Append((object)index).ToArray());

        public JsonPathSpec Elements => new(Segments.Append(Every).ToArray());

        public bool IsPattern => Segments.Any(s => ReferenceEquals(s, Every));

        public static JsonPathSpec Parse(string text)
        {
            if (text.StartsWith('$') == false)
                throw new FormatException(text);

            var path = Root;
            var i = 1;
            while (i < text.Length)
            {
                if (text[i] == '.')
                {
                    var end = i + 1;
                    while (end < text.Length && text[end] != '.' && text[end] != '[')
                        end++;

                    path = path[text.Substring(i + 1, end - i - 1)];
                    i = end;
                }
                else if (text[i] == '[')
                {
                    var end = text.IndexOf(']', i);
                    var inside = text.Substring(i + 1, end - i - 1);
                    path = inside == "*" ? path.Elements : path[long.Parse(inside, CultureInfo.InvariantCulture)];
                    i = end + 1;
                }
                else
                {
                    throw new FormatException(text);
                }
            }

            return path;
        }

        public static implicit operator JsonPathSpec(string text) => Parse(text);

        public Term<JPath> ToTerm(JsonRules rules)
        {
            Term<JPath> term = rules.Root.Of();
            foreach (var segment in Segments)
                term = segment switch
                {
                    string name => rules.Prop.Of(term, name),
                    long index => rules.Index.Of(term, index),
                    _ => rules.Each.Of(term),
                };

            return term;
        }

        public bool Equals(JsonPathSpec? other) => other is not null && Segments.SequenceEqual(other.Segments);

        public override int GetHashCode() => Segments.Aggregate(17, (h, s) => h * 31 + (ReferenceEquals(s, Every) ? 7 : s.GetHashCode()));

        public override string ToString()
        {
            var builder = new StringBuilder("$");
            foreach (var segment in Segments)
                builder.Append(segment switch
                {
                    string name => "." + name,
                    long index => "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                    _ => "[*]",
                });

            return builder.ToString();
        }

    }

    /// <summary>A statement about a path, to be assumed into a context.</summary>
    public abstract record JsonFact(JsonPathSpec Path)
    {

        public sealed record Present(JsonPathSpec Path) : JsonFact(Path);

        public sealed record Absent(JsonPathSpec Path) : JsonFact(Path);

        public sealed record OfType(JsonPathSpec Path, ValueSet<string> Types) : JsonFact(Path);

        public sealed record OneOf(JsonPathSpec Path, ValueSet<string> Values) : JsonFact(Path);

        public sealed record NotEqual(JsonPathSpec Path, string Value) : JsonFact(Path);

        public sealed record Lower(JsonPathSpec Path, Bound Bound) : JsonFact(Path);

        public sealed record Upper(JsonPathSpec Path, Bound Bound) : JsonFact(Path);

        public sealed record MinLength(JsonPathSpec Path, long Length) : JsonFact(Path);

        public sealed record MaxLength(JsonPathSpec Path, long Length) : JsonFact(Path);

        public sealed record MinItems(JsonPathSpec Path, long Count) : JsonFact(Path);

        public sealed record MaxItems(JsonPathSpec Path, long Count) : JsonFact(Path);

        public sealed record ClosedTo(JsonPathSpec Path, ValueSet<string> Properties) : JsonFact(Path);

        public sealed record Requires(JsonPathSpec Path, string Property) : JsonFact(Path);

        public sealed record Holds(JsonPathSpec Path, string Value) : JsonFact(Path);

        public sealed record MultipleOf(JsonPathSpec Path, decimal Factor) : JsonFact(Path);

        public sealed record UniqueItems(JsonPathSpec Path) : JsonFact(Path);

        public sealed record MinProperties(JsonPathSpec Path, long Count) : JsonFact(Path);

        public sealed record MaxProperties(JsonPathSpec Path, long Count) : JsonFact(Path);

        public Fact ToFact(JsonRules r)
        {
            var p = Path.ToTerm(r);
            return this switch
            {
                Present => r.Present.Of(p),
                Absent => r.Absent.Of(p),
                OfType f => r.Types.Of(p).Is(f.Types),
                OneOf f => r.Dom.Of(p).Is(f.Values),
                NotEqual f => r.Ne.Of(p, f.Value),
                Lower f => r.Lo.Of(p).Is(f.Bound),
                Upper f => r.Hi.Of(p).Is(f.Bound),
                MinLength f => r.MinLength.Of(p).Is(f.Length),
                MaxLength f => r.MaxLength.Of(p).Is(f.Length),
                MinItems f => r.MinItems.Of(p).Is(f.Count),
                MaxItems f => r.MaxItems.Of(p).Is(f.Count),
                ClosedTo f => r.Closed.Of(p).Is(f.Properties),
                Requires f => r.Required.Of(p, f.Property),
                Holds f => r.Contains.Of(p, f.Value),
                MultipleOf f => r.MultipleOf.Of(p).Is(f.Factor),
                UniqueItems => r.Unique.Of(p),
                MinProperties f => r.MinProperties.Of(p).Is(f.Count),
                MaxProperties f => r.MaxProperties.Of(p).Is(f.Count),
                _ => throw new NotSupportedException(GetType().Name),
            };
        }

    }

    /// <summary>
    /// A predicate a query can contain, in JSON's terms. Each is true of a document only where its path is there and
    /// holds what it asks, which is how Cosmos DB evaluates the comparison it stands for: a comparison over an absent
    /// path, or across types, is undefined, and a filter keeps no row for it.
    /// </summary>
    public abstract record JsonAtom(JsonPathSpec Path)
    {

        /// <summary><c>IS_DEFINED</c>.</summary>
        public sealed record Defined(JsonPathSpec Path) : JsonAtom(Path);

        /// <summary><c>IS_STRING</c>, <c>IS_NUMBER</c> and the rest; <c>number</c> covers both kinds of number.</summary>
        public sealed record HasType(JsonPathSpec Path, string Type) : JsonAtom(Path);

        /// <summary>An equality with a scalar.</summary>
        public sealed record EqualTo(JsonPathSpec Path, string Value) : JsonAtom(Path);

        /// <summary>A range over a number, either side open or absent.</summary>
        public sealed record NumberIn(JsonPathSpec Path, Bound? Lower, Bound? Upper) : JsonAtom(Path);

        /// <summary>A range over <c>ARRAY_LENGTH</c>.</summary>
        public sealed record LengthIn(JsonPathSpec Path, long? Min, long? Max) : JsonAtom(Path);

        /// <summary><c>ARRAY_CONTAINS</c> of a scalar.</summary>
        public sealed record ArrayHolds(JsonPathSpec Path, string Value) : JsonAtom(Path);

    }

    /// <summary>What every document a context admits says about a predicate.</summary>
    public enum Truth
    {

        /// <summary>Some documents satisfy it and some may not, or the context cannot tell.</summary>
        Unknown,

        /// <summary>Every document the context admits satisfies it.</summary>
        Always,

        /// <summary>No document the context admits satisfies it.</summary>
        Never,

    }

    /// <summary>
    /// A graph whose root holds the rules of JSON, frozen, beneath which contexts for schemas and queries are made.
    /// </summary>
    public sealed class JsonContexts
    {

        /// <param name="documentsAreObjects">Whether every document is an object, as every Cosmos DB item is.</param>
        public JsonContexts(bool documentsAreObjects = true)
        {
            Graph = new EGraph();
            Rules = new JsonRules(Graph);
            Saturation = Schedule.Saturate(Rules.Json);

            var root = Graph.Root;
            if (documentsAreObjects)
                root.Assume(Rules.Present.Of(Rules.Root.Of()), Rules.Types.Of(Rules.Root.Of()).Is(Types(ObjectType)));

            root.Run(Saturation);
            root.Freeze();
        }

        public EGraph Graph { get; }

        public JsonRules Rules { get; }

        public Schedule Saturation { get; }

        /// <summary>
        /// Makes a child context of <paramref name="parent"/> (the root where it is <c>null</c>), mentions the paths it
        /// will be asked about, assumes the facts and saturates. Freeze it to put contexts beneath it.
        /// </summary>
        public Scope Open(Scope? parent, IEnumerable<JsonFact> facts, IEnumerable<JsonPathSpec>? mentions = null)
        {
            var scope = (parent ?? Graph.Root).CreateChild();
            Mention(scope, mentions ?? System.Array.Empty<JsonPathSpec>());
            var list = facts.ToList();
            if (list.Count > 0)
                scope.Assume(list.Select(f => f.ToFact(Rules)).ToArray());

            scope.Run(Saturation);
            return scope;
        }

        /// <summary>
        /// Makes the terms of paths a context will be asked about. Rules reason over paths that exist, so a path no
        /// fact and no question mentions has nothing proven of it.
        /// </summary>
        public void Mention(Scope scope, IEnumerable<JsonPathSpec> paths)
        {
            foreach (var path in paths)
                foreach (var prefix in Prefixes(path))
                    scope.Make((ConstructorTerm<JPath>)prefix.ToTerm(Rules));
        }

        static IEnumerable<JsonPathSpec> Prefixes(JsonPathSpec path)
        {
            for (var n = path.Segments.Count; n >= 1; n--)
                yield return new JsonPathSpec(path.Segments.Take(n).ToArray());
        }

        /// <summary>What every document the context admits says about a predicate.</summary>
        public Truth Decide(Scope scope, JsonAtom atom)
        {
            // A context no document satisfies keeps no row, whatever is asked of it.
            if (scope.IsContradictory)
                return Truth.Never;

            var r = Rules;
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
                    if (CannotHold(scope, p, types, dom, eq.Value))
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
                        var numbers = dom.Where(IsNumber).Select(NumberOf).ToList();
                        if (numbers.Any(x => In(x, range.Lower, range.Upper)) == false)
                            return Truth.Never;
                        if (present && dom.All(IsNumber) && numbers.All(x => In(x, range.Lower, range.Upper)))
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
                    if (CannotHold(scope, each, eachTypes, eachDom, holds.Value))
                        return Truth.Never;

                    return scope.Check(r.Contains.Of(p, holds.Value)) ? Truth.Always : Truth.Unknown;
                }

                default:
                    throw new NotSupportedException(atom.GetType().Name);
            }
        }

        /// <summary>Whether the facts about a path leave no room for it to hold a value.</summary>
        bool CannotHold(Scope scope, Term<JPath> p, ValueSet<string> types, ValueSet<string>? dom, string value)
        {
            var r = Rules;
            if (types.Contains(TypeOf(value)) == false)
                return true;
            if (dom is not null && dom.Contains(value) == false)
                return true;
            if (scope.Check(r.Ne.Of(p, value)))
                return true;

            if (IsNumber(value))
            {
                var x = NumberOf(value);
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
