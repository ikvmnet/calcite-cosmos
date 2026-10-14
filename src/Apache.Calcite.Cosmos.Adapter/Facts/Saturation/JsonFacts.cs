using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A path written the way a person writes one: <c>$</c>, <c>$.a.b</c>, <c>$.tags[10]</c>, and <c>$.items[*].id</c>
    /// for property <c>id</c> of every element of <c>items</c>.
    /// </summary>
    internal sealed record JsonPathSpec(IReadOnlyList<object> Segments)
    {

        /// <summary>The segment that stands for every element of an array.</summary>
        public static readonly object Every = new();

        public static JsonPathSpec Root { get; } = new(System.Array.Empty<object>());

        public JsonPathSpec this[string property] => new(Segments.Append(property).ToArray());

        public JsonPathSpec this[long index] => new(Segments.Append((object)index).ToArray());

        public JsonPathSpec Elements => new(Segments.Append(Every).ToArray());

        public bool IsPattern => Segments.Any(s => ReferenceEquals(s, Every));

        /// <summary>The name of a hidden discriminator this stands for, which is no place in any document.</summary>
        public string? Hidden { get; init; }

        /// <summary>A hidden discriminator: which branch of an undiscriminated union applies.</summary>
        public static JsonPathSpec HiddenNamed(string name) => new(System.Array.Empty<object>()) { Hidden = name };

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
            if (Hidden is not null)
                return rules.Ghost.Of(Hidden);

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

        public bool Equals(JsonPathSpec? other) => other is not null && Hidden == other.Hidden && Segments.SequenceEqual(other.Segments);

        public override int GetHashCode() => Segments.Aggregate(Hidden?.GetHashCode() ?? 17, (h, s) => h * 31 + (ReferenceEquals(s, Every) ? 7 : s.GetHashCode()));

        public override string ToString()
        {
            if (Hidden is not null)
                return "hidden:" + Hidden;

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
    internal abstract record JsonFact(JsonPathSpec Path)
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

        /// <summary>The two paths hold one value, or are both absent: what <c>c.a = c.b</c> proves, and what a
        /// <c>CHECK (a = b)</c> over paths that are both there states.</summary>
        public sealed record Same(JsonPathSpec Path, JsonPathSpec Other) : JsonFact(Path);

        public sealed record ContainsAtLeast(JsonPathSpec Path, string Value, long Count) : JsonFact(Path);

        public sealed record ContainsAtMost(JsonPathSpec Path, string Value, long Count) : JsonFact(Path);

        public sealed record NameMinLength(JsonPathSpec Path, long Length) : JsonFact(Path);

        public sealed record NameMaxLength(JsonPathSpec Path, long Length) : JsonFact(Path);

        /// <summary>If the path holds a string, it is spelled in the named stored form.</summary>
        public sealed record Form(JsonPathSpec Path, string Name) : JsonFact(Path);

        /// <summary>The path holds what a host's claim, named by its key, says; the theory reads nothing of it.</summary>
        public sealed record Extension(JsonPathSpec Path, string Key) : JsonFact(Path);

        /// <summary>A split may open one case per value the path can hold. Says nothing about any document.</summary>
        public sealed record Discriminates(JsonPathSpec Path) : JsonFact(Path);

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
                Same f => p.Is(f.Other.ToTerm(r)),
                ContainsAtLeast f => r.ContainsAtLeast.Of(p, f.Value).Is(f.Count),
                ContainsAtMost f => r.ContainsAtMost.Of(p, f.Value).Is(f.Count),
                NameMinLength f => r.NameMinLength.Of(p).Is(f.Length),
                NameMaxLength f => r.NameMaxLength.Of(p).Is(f.Length),
                Form f => r.Form.Of(p, f.Name),
                Extension f => r.Extension.Of(p, f.Key),
                Discriminates => r.Discriminator.Of(p),
                _ => throw new NotSupportedException(GetType().Name),
            };
        }

    }

    /// <summary>
    /// A schema's conditional statement: where every fact of <see cref="When"/> is known, every fact of
    /// <see cref="Then"/> holds. A <c>oneOf</c> branch under its discriminator, an <c>if</c>/<c>then</c>.
    /// </summary>
    /// <remarks>
    /// A body fact is known the way the theory knows any fact: a domain within the set, types within the set, the
    /// path present or absent, a disequality stated or proven by a domain. So a body fact is a statement about the
    /// document like any other, and the rule is true of a document where its body is false of it or its head true.
    /// </remarks>
    internal sealed record JsonGuardedRule(IReadOnlyList<JsonFact> When, IReadOnlyList<JsonFact> Then)
    {

        public override string ToString() => $"if {string.Join(" and ", When)} then {string.Join(" and ", Then)}";

    }

    /// <summary>
    /// A predicate a query can contain, in JSON's terms. Each is true of a document only where its path is there and
    /// holds what it asks, which is how Cosmos DB evaluates the comparison it stands for: a comparison over an absent
    /// path, or across types, is undefined, and a filter keeps no row for it.
    /// </summary>
    internal abstract record JsonAtom(JsonPathSpec Path)
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

        /// <summary>An equality between two paths, true where both are there and hold one value.</summary>
        public sealed record SameAs(JsonPathSpec Path, JsonPathSpec Other) : JsonAtom(Path);

    }

    /// <summary>What every document a context admits says about a predicate.</summary>
    internal enum Truth
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
    internal sealed class JsonContexts
    {

        /// <param name="documentsAreObjects">Whether every document is an object, as every Cosmos DB item is.</param>
        public JsonContexts(bool documentsAreObjects = true)
        {
            Graph = new EGraph();
            Rules = new JsonRules(Graph);
            Saturation = Rules.Settle;

            // The rules of JSON hold everywhere: they are the graph's own, and the root is frozen with them.
            var root = Graph.Root;
            if (documentsAreObjects)
                root.Assume(Rules.Present.Of(Rules.Root.Of()), Rules.Types.Of(Rules.Root.Of()).Is(Types(ObjectType)));

            root.Run(Saturation);
            root.Freeze();

            // The rules of JSON Schema hold beneath their own context, a child of the root, and every schema's and
            // every query's context is made beneath that.
            JsonSchema = root.CreateChild();
            Rules.DefineSchemaRules(JsonSchema);
            JsonSchema.Run(Saturation);
            JsonSchema.Freeze();
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets the tables and rules the graph defines.</summary>
        public JsonRules Rules { get; }

        /// <summary>Gets the schedule that runs the rules of JSON and of JSON Schema to saturation, without splitting.</summary>
        public Schedule Saturation { get; }

        /// <summary>Gets the root context, frozen: the rules of JSON, and nothing else.</summary>
        public Scope Json => Graph.Root;

        /// <summary>
        /// Gets the JSON Schema context, frozen: a child of the root holding the rules of JSON Schema, beneath which
        /// every schema's context and every query's is made.
        /// </summary>
        public Scope JsonSchema { get; }

        long _next;

        /// <summary>The report of the last context opened on this thread, for measurement.</summary>
        [ThreadStatic]
        public static RunReport? LastReport;

        /// <summary>
        /// Makes a query's context beneath <paramref name="parent"/> (the JSON Schema context where it is <c>null</c>): mentions the
        /// paths it will be asked about, assumes the facts and saturates, splitting on the discriminators it mentions.
        /// </summary>
        /// <param name="parent">The schema's context, frozen; or <c>null</c> for the JSON Schema context.</param>
        /// <param name="facts">What the query establishes.</param>
        /// <param name="mentions">The paths the context will be asked about, beside the facts' own.</param>
        /// <param name="rules">Conditional rules of the query's own, as rows.</param>
        /// <param name="split">
        /// Whether to split on discriminators. A context beneath a schema that names none has nothing to split on, and
        /// the split step costs a run even where it opens no case.
        /// </param>
        /// <param name="options">The run's limits; <c>null</c> for the engine's defaults.</param>
        public Scope Open(Scope? parent, IEnumerable<JsonFact> facts, IEnumerable<JsonPathSpec>? mentions = null, IEnumerable<JsonGuardedRule>? rules = null, bool split = true, RunOptions? options = null) =>
            Open(parent, facts, mentions, rules, split ? Rules.QuerySaturation : Rules.Settle, mention: true, options);

        /// <summary>
        /// Makes a schema's context: its facts and its conditional rules, saturated with a split on every
        /// discriminator, so that what every branch implies holds outright. Freeze it to put queries beneath it.
        /// </summary>
        public Scope OpenSchema(IEnumerable<JsonFact> facts, IEnumerable<JsonGuardedRule>? rules = null, IEnumerable<JsonPathSpec>? mentions = null) =>
            Open(null, facts, mentions, rules, Rules.SchemaSaturation, mention: false, null);

        Scope Open(Scope? parent, IEnumerable<JsonFact> facts, IEnumerable<JsonPathSpec>? mentions, IEnumerable<JsonGuardedRule>? rules, Schedule schedule, bool mention, RunOptions? options)
        {
            var mentioned = (mentions ?? System.Array.Empty<JsonPathSpec>()).ToList();
            var list = facts.ToList();
            var assumed = list.Select(f => f.ToFact(Rules)).ToList();
            foreach (var rule in rules ?? System.Array.Empty<JsonGuardedRule>())
                assumed.AddRange(Declare(rule));

            // A query splits only on what it mentions: the paths it asks about and the facts it states.
            if (mention)
                foreach (var path in mentioned.Concat(list.Select(f => f.Path)).Distinct())
                    assumed.Add(Rules.Mentioned.Of(path.ToTerm(Rules)));

            // A context that fails to open is released here rather than left beneath its parent.
            var scope = (parent ?? JsonSchema).CreateChild();
            try
            {
                Mention(scope, mentioned);
                if (assumed.Count > 0)
                    scope.Assume(assumed.ToArray());

                LastReport = scope.Run(schedule, options);
            }
            catch
            {
                scope.Dispose();
                throw;
            }

            return scope;
        }

        /// <summary>A conditional rule as rows: its body as a chain of atoms, its head as facts keyed by its number.</summary>
        IEnumerable<Fact> Declare(JsonGuardedRule rule)
        {
            var r = Rules;
            if (rule.When.Count == 0)
            {
                foreach (var head in rule.Then)
                    yield return head.ToFact(r);
                yield break;
            }

            var id = System.Threading.Interlocked.Increment(ref _next);
            yield return r.Step.Of(id, 0L);
            yield return r.Length.Of(id, (long)rule.When.Count);

            foreach (var atom in rule.When)
                yield return r.AtomPath.Of(id, atom.Path.ToTerm(r));
            foreach (var head in rule.Then)
                yield return r.HeadPath.Of(id, head.Path.ToTerm(r));

            for (var k = 0; k < rule.When.Count; k++)
            {
                var atom = rule.When[k];
                var p = atom.Path.ToTerm(r);
                long at = k;
                yield return atom switch
                {
                    JsonFact.Present => r.AtomPresent.Of(id, at, p),
                    JsonFact.Absent => r.AtomAbsent.Of(id, at, p),
                    JsonFact.OneOf { Values.Count: 1 } one => r.AtomEqual.Of(id, at, p, one.Values[0]),
                    JsonFact.OneOf f => r.AtomIn.Of(id, at, p, f.Values),
                    JsonFact.OfType f => r.AtomType.Of(id, at, p, f.Types),
                    JsonFact.NotEqual f => r.AtomNe.Of(id, at, p, f.Value),
                    _ => throw new NotSupportedException("a body of " + atom.GetType().Name),
                };
            }

            foreach (var head in rule.Then)
            {
                var p = head.Path.ToTerm(r);
                yield return head switch
                {
                    JsonFact.Present => r.HeadPresent.Of(id, p),
                    JsonFact.Absent => r.HeadAbsent.Of(id, p),
                    JsonFact.OfType f => r.HeadType.Of(id, p, f.Types),
                    JsonFact.OneOf f => r.HeadDom.Of(id, p, f.Values),
                    JsonFact.NotEqual f => r.HeadNe.Of(id, p, f.Value),
                    JsonFact.Lower f => r.HeadLo.Of(id, p, f.Bound),
                    JsonFact.Upper f => r.HeadHi.Of(id, p, f.Bound),
                    JsonFact.MinItems f => r.HeadMinItems.Of(id, p, f.Count),
                    JsonFact.MaxItems f => r.HeadMaxItems.Of(id, p, f.Count),
                    JsonFact.Requires f => r.HeadRequired.Of(id, p, f.Property),
                    JsonFact.ClosedTo f => r.HeadClosed.Of(id, p, f.Properties),
                    JsonFact.Form f => r.HeadForm.Of(id, p, f.Name),
                    JsonFact.Extension f => r.HeadExtension.Of(id, p, f.Key),
                    _ => throw new NotSupportedException("a head of " + head.GetType().Name),
                };
            }
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
            if (path.Hidden is not null)
            {
                yield return path;
                yield break;
            }

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
                    if (CannotHold(scope, each, eachTypes, eachDom, holds.Value))
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
        bool CannotHold(Scope scope, Term<JPath> p, ValueSet<string> types, ValueSet<string>? dom, string value)
        {
            var r = Rules;
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
