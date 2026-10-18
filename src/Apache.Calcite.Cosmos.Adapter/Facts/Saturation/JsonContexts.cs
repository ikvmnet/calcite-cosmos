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
                    JsonFact.Characters f => r.HeadChars.Of(id, p, f.Bands),
                    JsonFact.Written f => r.HeadWritten.Of(id, p, f.Bands),
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

        /// <summary>
        /// Returns a child of a frozen context that mentions one path more, saturated and frozen: what the context knows of
        /// a path it was never told of, which is what the rules derive of it from what they already hold — a path beneath
        /// one that holds no object is absent, beneath one that is closed to it too.
        /// </summary>
        /// <remarks>
        /// It assumes nothing, so it narrows no document the context admits and lifts no case: a split there decides only
        /// what something here narrows, and the context it is made beneath has already lifted what its own split proves.
        /// So it knows of the path what the context would have, told of it when it was opened, short of what only a split
        /// on a discriminator would prove; less, never more.
        /// </remarks>
        /// <param name="parent">The context, frozen.</param>
        /// <param name="path">The path.</param>
        /// <param name="schedule">The rules the context ran, without its splits.</param>
        /// <param name="options">The run's limits, or <c>null</c> for the engine's.</param>
        /// <returns>The child; or <c>null</c> where its run stops short, or finds that the context admits no document,
        /// which is the context's to say and no answer about the path.</returns>
        public Scope? Mentioning(Scope parent, JsonPathSpec path, Schedule schedule, RunOptions? options)
        {
            var scope = parent.CreateChild();
            try
            {
                Mention(scope, new[] { path });
                var report = scope.Run(schedule, options);
                if (report.StopReason != RunStopReason.Completed || scope.IsContradictory)
                {
                    scope.Dispose();
                    return null;
                }

                scope.Freeze();
                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
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
