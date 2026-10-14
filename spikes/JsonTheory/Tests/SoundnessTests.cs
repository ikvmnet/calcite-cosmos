using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static JsonTheory.JsonValues;

namespace JsonTheory.Tests
{

    /// <summary>
    /// The theory never derives a falsehood from truths. Each trial generates a document, states facts that are true
    /// of it, saturates, and checks that everything derived is true of the same document, that the context is not
    /// contradictory, and that every predicate decided always or never is decided rightly for it.
    /// </summary>
    /// <remarks>
    /// A derived fact false of a document that satisfies every premise is an unsound rule: in the adapter, a plan that
    /// drops rows. This is the oracle that stands behind the rules, rather than agreement with any other engine.
    /// </remarks>
    [TestClass]
    public sealed class SoundnessTests
    {

        static readonly JsonContexts Contexts = new();

        static readonly JsonPathSpec[] Concrete =
            new[]
            {
                "$", "$.a", "$.b", "$.c", "$.a.a", "$.a.b", "$.a.c", "$.b.a",
                "$.a[0]", "$.a[1]", "$.a[2]", "$.a[3]", "$.a[4]", "$.b[0]", "$.b[1]",
                "$.a[0].a", "$.a[1].b", "$.a[0][0]", "$.a[1][1]", "$.a.a[0]",
            }.Select(JsonPathSpec.Parse).ToArray();

        static readonly JsonPathSpec[] Patterns =
            new[] { "$.a[*]", "$.b[*]", "$.a[*].a", "$.a[*].b", "$.a[*][*]", "$.a[*][0]", "$.a.a[*]" }.Select(JsonPathSpec.Parse).ToArray();

        static readonly JsonPathSpec[] Vocabulary = Concrete.Concat(Patterns).ToArray();

        [TestMethod]
        public void NothingDerivedIsFalseOfADocumentTheFactsAreTrueOf()
        {
            var random = new Random(20261014);
            var derived = 0;

            for (var trial = 0; trial < 12_000; trial++)
            {
                var document = Documents.Object(random, 0);
                // Most facts are about one path and its relatives, so that facts which only combine on one path, or on
                // an array and its elements, meet in a trial.
                var focus = Relatives(Vocabulary[random.Next(Vocabulary.Length)]);
                var premises = Enumerable.Range(0, 1 + random.Next(6))
                    .Select(_ => TrueFact(random, document, random.Next(4) != 0 ? focus[random.Next(focus.Count)] : null)).ToList();
                foreach (var premise in premises)
                    Assert.IsTrue(Documents.IsTrue(document, premise), $"the generator stated a falsehood: {premise} of {document}");

                using var scope = Contexts.Open(null, premises, Vocabulary);
                string Case() => $"document {document}\npremises:\n  {string.Join("\n  ", premises)}";

                Assert.IsFalse(scope.IsContradictory, $"contradiction from true facts\n{Case()}");

                foreach (var fact in Derived(scope))
                {
                    derived++;
                    if (Documents.IsTrue(document, fact) == false)
                        Assert.Fail($"derived {fact}, false of the document\n{Case()}");
                }

                for (var i = 0; i < 12; i++)
                {
                    var atom = RandomAtom(random);
                    var truth = Contexts.Decide(scope, atom);
                    var actual = Documents.IsTrue(document, atom);
                    if (truth == Truth.Always && actual == false || truth == Truth.Never && actual)
                        Assert.Fail($"decided {atom} {truth}, but it is {actual} of the document\n{Case()}");
                }
            }

            Console.WriteLine($"{derived:N0} derived facts checked");
        }

        /// <summary>Everything the context holds about the vocabulary, as facts.</summary>
        static IEnumerable<JsonFact> Derived(Scope scope)
        {
            var r = Contexts.Rules;
            foreach (var path in Vocabulary)
            {
                var p = path.ToTerm(r);
                if (scope.Check(r.Present.Of(p)))
                {
                    // A pattern says what each element is if it is there; that one is there is never its to say.
                    if (path.IsPattern)
                        Assert.Fail($"presence derived of the pattern {path}");
                    yield return new JsonFact.Present(path);
                }

                if (scope.Check(r.Absent.Of(p)))
                    yield return new JsonFact.Absent(path);
                if (scope.TryGet(r.Types.Of(p), out var types))
                    yield return new JsonFact.OfType(path, types);
                if (scope.TryGet(r.Dom.Of(p), out var dom))
                    yield return new JsonFact.OneOf(path, dom);
                if (scope.TryGet(r.Lo.Of(p), out var lo))
                    yield return new JsonFact.Lower(path, lo);
                if (scope.TryGet(r.Hi.Of(p), out var hi))
                    yield return new JsonFact.Upper(path, hi);
                if (scope.TryGet(r.MinLength.Of(p), out var minLength))
                    yield return new JsonFact.MinLength(path, minLength);
                if (scope.TryGet(r.MaxLength.Of(p), out var maxLength))
                    yield return new JsonFact.MaxLength(path, maxLength);
                if (scope.TryGet(r.MinItems.Of(p), out var minItems))
                    yield return new JsonFact.MinItems(path, minItems);
                if (scope.TryGet(r.MaxItems.Of(p), out var maxItems))
                    yield return new JsonFact.MaxItems(path, maxItems);
                if (scope.TryGet(r.Closed.Of(p), out var closed))
                    yield return new JsonFact.ClosedTo(path, closed);
                if (scope.TryGet(r.MultipleOf.Of(p), out var factor))
                    yield return new JsonFact.MultipleOf(path, factor);
                if (scope.Check(r.Unique.Of(p)))
                    yield return new JsonFact.UniqueItems(path);
                if (scope.TryGet(r.MinProperties.Of(p), out var minProperties))
                    yield return new JsonFact.MinProperties(path, minProperties);
                if (scope.TryGet(r.MaxProperties.Of(p), out var maxProperties))
                    yield return new JsonFact.MaxProperties(path, maxProperties);
                if (scope.TryGet(r.PresentNames.Of(p), out var named))
                    foreach (var name in named)
                        yield return new JsonFact.Present(path[name]);
                if (scope.TryGet(r.RequiredNames.Of(p), out var requiredNames))
                    foreach (var name in requiredNames)
                        yield return new JsonFact.Requires(path, name);

                foreach (var value in Documents.Universe)
                {
                    if (scope.Check(r.Ne.Of(p, value)))
                        yield return new JsonFact.NotEqual(path, value);
                    if (scope.Check(r.Contains.Of(p, value)))
                        yield return new JsonFact.Holds(path, value);
                }

                foreach (var key in Documents.Keys)
                    if (scope.Check(r.Required.Of(p, key)))
                        yield return new JsonFact.Requires(path, key);
            }
        }

        /// <summary>A path of the vocabulary, with the vocabulary's paths for its elements and its parent.</summary>
        static List<JsonPathSpec> Relatives(JsonPathSpec path)
        {
            var parent = path.Segments.Count > 0 ? new JsonPathSpec(path.Segments.Take(path.Segments.Count - 1).ToArray()) : null;
            return Vocabulary.Where(v => v.Equals(path) || v.Equals(path.Elements) || parent is not null && v.Equals(parent)).ToList();
        }

        /// <summary>A fact about a path of the vocabulary, true of the document.</summary>
        static JsonFact TrueFact(Random random, J document, JsonPathSpec? focus)
        {
            while (true)
            {
                var path = focus ?? Vocabulary[random.Next(Vocabulary.Length)];
                var values = Documents.Values(document, path);
                var scalars = values.Where(v => v.IsScalar).Select(v => v.Encoded).ToList();
                var numbers = values.OfType<J.Num>().Select(n => n.Value).ToList();
                var strings = values.OfType<J.Str>().Select(s => (long)s.Value.Length).ToList();
                var arrays = values.OfType<J.Arr>().Select(a => (long)a.Elements.Count).ToList();
                var objects = values.OfType<J.Obj>().ToList();

                // Facts bearing on what the document holds there are drawn more often than those that hold vacuously.
                var kinds = Enumerable.Range(0, 18).ToList();
                if (objects.Count > 0)
                    kinds.AddRange(new[] { 10, 11, 16, 17, 10, 11, 16, 17 });
                if (arrays.Count > 0)
                    kinds.AddRange(new[] { 8, 9, 15, 8, 9, 15 });
                if (numbers.Count > 0)
                    kinds.AddRange(new[] { 4, 5, 14, 4, 5, 14 });
                if (strings.Count > 0)
                    kinds.AddRange(new[] { 6, 7 });
                if (scalars.Count > 0)
                    kinds.AddRange(new[] { 2, 3, 2 });

                JsonFact? fact = kinds[random.Next(kinds.Count)] switch
                {
                    0 when path.IsPattern == false => values.Count > 0 ? new JsonFact.Present(path) : new JsonFact.Absent(path),
                    1 => new JsonFact.OfType(path, ValueSet.From(values.Select(v => v.Type).Concat(AllTypes.Where(_ => random.Next(3) == 0)))),
                    2 when values.All(v => v.IsScalar) => new JsonFact.OneOf(path, ValueSet.From(scalars.Concat(Documents.Universe.Where(_ => random.Next(2) == 0 && random.Next(4) == 0)))),
                    3 => Documents.Universe.Where(u => scalars.Contains(u) == false).ToList() is { Count: > 0 } others
                        ? new JsonFact.NotEqual(path, others[random.Next(others.Count)]) : null,
                    4 => new JsonFact.Lower(path, LowerBelow(random, numbers)),
                    5 => new JsonFact.Upper(path, UpperAbove(random, numbers)),
                    6 => new JsonFact.MinLength(path, Math.Max(0, (strings.Count > 0 ? strings.Min() : 3) - random.Next(2))),
                    7 => new JsonFact.MaxLength(path, (strings.Count > 0 ? strings.Max() : random.Next(3)) + random.Next(2)),
                    8 => new JsonFact.MinItems(path, Math.Max(0, (arrays.Count > 0 ? arrays.Min() : 3) - random.Next(2))),
                    9 => new JsonFact.MaxItems(path, (arrays.Count > 0 ? arrays.Max() : random.Next(3)) + random.Next(2)),
                    10 => new JsonFact.ClosedTo(path, ValueSet.From(objects.SelectMany(o => o.Properties.Keys).Concat(Documents.Keys.Where(_ => random.Next(3) == 0)))),
                    11 => Documents.Keys.Where(k => objects.All(o => o.Properties.ContainsKey(k))).ToList() is { Count: > 0 } keys
                        ? new JsonFact.Requires(path, keys[random.Next(keys.Count)]) : null,
                    12 when path.IsPattern == false => values.SingleOrDefault() is J.Arr a && a.Elements.Where(e => e.IsScalar).ToList() is { Count: > 0 } held
                        ? new JsonFact.Holds(path, held[random.Next(held.Count)].Encoded) : null,
                    13 when path.IsPattern == false => values.Count > 0 ? new JsonFact.Present(path) : new JsonFact.Absent(path),
                    14 => new[] { 0.5m, 1m, 2m, 2.5m, 5m, 10m }.Where(m => numbers.All(x => Multiples.IsMultiple(x, m))).ToList() is { Count: > 0 } factors
                        ? new JsonFact.MultipleOf(path, factors[random.Next(factors.Count)]) : null,
                    15 => values.OfType<J.Arr>().All(a => a.Elements.Select(Documents.Key).Distinct().Count() == a.Elements.Count) ? new JsonFact.UniqueItems(path) : null,
                    16 => new JsonFact.MinProperties(path, Math.Max(0, (objects.Count > 0 ? objects.Min(o => o.Properties.Count) : 2) - random.Next(2))),
                    17 => new JsonFact.MaxProperties(path, (objects.Count > 0 ? objects.Max(o => o.Properties.Count) : random.Next(3)) + random.Next(2)),
                    _ => null,
                };

                if (fact is not null)
                    return fact;
            }
        }

        static Bound LowerBelow(Random random, List<decimal> numbers)
        {
            if (numbers.Count == 0)
                return new Bound(random.Next(-2, 12), random.Next(2) == 0);

            var least = numbers.Min();
            return random.Next(3) switch
            {
                0 => Bound.AtLeast(least),
                1 => Bound.Above(least - 0.5m),
                _ => Bound.AtLeast(least - random.Next(3)),
            };
        }

        static Bound UpperAbove(Random random, List<decimal> numbers)
        {
            if (numbers.Count == 0)
                return new Bound(random.Next(-2, 12), random.Next(2) == 0);

            var most = numbers.Max();
            return random.Next(3) switch
            {
                0 => Bound.AtMost(most),
                1 => Bound.Below(most + 0.5m),
                _ => Bound.AtMost(most + random.Next(3)),
            };
        }

        static JsonAtom RandomAtom(Random random)
        {
            var path = Concrete[random.Next(Concrete.Length)];
            var value = Documents.Universe[random.Next(Documents.Universe.Length)];
            Bound? Edge() => random.Next(3) == 0 ? null : new Bound(random.Next(-2, 12) + (random.Next(2) == 0 ? 0.5m : 0m), random.Next(2) == 0);
            long? Count() => random.Next(3) == 0 ? null : random.Next(0, 6);

            return random.Next(6) switch
            {
                0 => new JsonAtom.Defined(path),
                1 => new JsonAtom.HasType(path, new[] { "string", "number", "integer", "boolean", "null", "object", "array" }[random.Next(7)]),
                2 => new JsonAtom.EqualTo(path, value),
                3 => new JsonAtom.NumberIn(path, Edge(), Edge()),
                4 => new JsonAtom.LengthIn(path, Count(), Count()),
                _ => new JsonAtom.ArrayHolds(path, value),
            };
        }

    }

}
