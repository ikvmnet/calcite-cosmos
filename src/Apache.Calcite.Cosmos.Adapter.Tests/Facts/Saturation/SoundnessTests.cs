using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog;

using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
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
    public sealed class SoundnessTests
    {

        static readonly JsonContexts Contexts = new();

        static readonly JsonPathSpec[] Concrete =
            new[]
            {
                "$", "$.a", "$.b", "$.c", "$.a.a", "$.a.b", "$.a.c", "$.b.a",
                "$.a[0]", "$.a[1]", "$.a[2]", "$.a[3]", "$.a[4]", "$.b[0]", "$.b[1]",
                "$.a[0].a", "$.a[1].b", "$.a[0][0]", "$.a[1][1]", "$.a.a[0]", "$.dd", "$.a.dd", "$.b.b", "$.b[0].a",
            }.Select(JsonPathSpec.Parse).ToArray();

        static readonly JsonPathSpec[] Patterns =
            new[] { "$.a[*]", "$.b[*]", "$.a[*].a", "$.a[*].b", "$.a[*][*]", "$.a[*][0]", "$.a.a[*]" }.Select(JsonPathSpec.Parse).ToArray();

        static readonly JsonPathSpec[] Vocabulary = Concrete.Concat(Patterns).ToArray();

        static readonly (JsonPathSpec, JsonPathSpec)[] Pairs =
            Concrete.SelectMany((a, i) => Concrete.Skip(i + 1).Select(b => (a, b))).ToArray();

        [Fact]
        public void NothingDerivedIsFalseOfADocumentTheFactsAreTrueOf()
        {
            var random = new Random(20261014);
            var derived = 0;

            for (var trial = 0; trial < 12_000; trial++)
            {
                // A third of the documents hold one value at two places, so that equalities between paths are true often.
                var document = Documents.Object(random, 0);
                if (random.Next(3) == 0)
                    document = Documents.Copying(document, "a", "b");
                // Most facts are about one path and its relatives, so that facts which only combine on one path, or on
                // an array and its elements, meet in a trial.
                var focus = Relatives(Vocabulary[random.Next(Vocabulary.Length)]);
                var premises = Enumerable.Range(0, 1 + random.Next(6))
                    .Select(_ => TrueFact(random, document, random.Next(4) != 0 ? focus[random.Next(focus.Count)] : null)).ToList();
                // Half the trials carry a discriminator and conditional rules, so that splits and lifts are checked too.
                var rules = new List<JsonGuardedRule>();
                if (random.Next(2) == 0)
                {
                    var discriminator = Discriminator(random, document);
                    if (discriminator is not null)
                        premises.AddRange(discriminator.Value.Facts);
                    for (var n = 1 + random.Next(3); n > 0; n--)
                        rules.Add(TrueRule(random, document, discriminator?.Path, discriminator?.Domain, focus));
                }

                foreach (var premise in premises)
                    Documents.IsTrue(document, premise).Should().BeTrue($"the generator stated a falsehood: {premise} of {document}");
                foreach (var rule in rules)
                    Documents.IsTrue(document, rule).Should().BeTrue($"the generator stated a false rule: {rule} of {document}");

                using var scope = Contexts.Open(null, premises, Vocabulary, rules);
                string Case() => $"document {document}\npremises:\n  {string.Join("\n  ", premises)}\nrules:\n  {string.Join("\n  ", rules)}";

                scope.IsContradictory.Should().BeFalse($"contradiction from true facts\n{Case()}");

                foreach (var fact in Derived(scope))
                {
                    derived++;
                    if (Documents.IsTrue(document, fact) == false)
                        Assert.Fail($"derived {fact}, false of the document\n{Case()}");
                }

                foreach (var (left, right) in Pairs)
                    if (scope.AreEqual(scope.Make((ConstructorTerm<JPath>)left.ToTerm(Contexts.Rules)), right.ToTerm(Contexts.Rules))
                        && Documents.IsTrue(document, new JsonFact.Same(left, right)) == false)
                        Assert.Fail($"derived {left} = {right}, false of the document\n{Case()}");

                for (var i = 0; i < 12; i++)
                {
                    var atom = RandomAtom(random);
                    var truth = Contexts.Decide(scope, atom);
                    var actual = Documents.IsTrue(document, atom);
                    if (truth == Truth.Always && actual == false || truth == Truth.Never && actual)
                        Assert.Fail($"decided {atom} {truth}, but it is {actual} of the document\n{Case()}");
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{derived:N0} derived facts checked");
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
                if (scope.TryGet(r.NameMinLength.Of(p), out var nameMin))
                    yield return new JsonFact.NameMinLength(path, nameMin);
                if (scope.TryGet(r.NameMaxLength.Of(p), out var nameMax))
                    yield return new JsonFact.NameMaxLength(path, nameMax);
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
                    if (scope.TryGet(r.ContainsAtLeast.Of(p, value), out var atLeast))
                        yield return new JsonFact.ContainsAtLeast(path, value, atLeast);
                    if (scope.TryGet(r.ContainsAtMost.Of(p, value), out var atMost))
                        yield return new JsonFact.ContainsAtMost(path, value, atMost);
                }

                foreach (var key in Documents.Keys)
                    if (scope.Check(r.Required.Of(p, key)))
                        yield return new JsonFact.Requires(path, key);

                foreach (var form in Documents.Forms)
                    if (scope.Check(r.Form.Of(p, form)))
                        yield return new JsonFact.Form(path, form);

                foreach (var key in Documents.Extensions)
                    if (scope.Check(r.Extension.Of(p, key)))
                        yield return new JsonFact.Extension(path, key);
            }
        }

        static readonly HashSet<Type> BodyKinds = new() { typeof(JsonFact.Present), typeof(JsonFact.Absent), typeof(JsonFact.OneOf), typeof(JsonFact.OfType), typeof(JsonFact.NotEqual) };

        static readonly HashSet<Type> HeadKinds = new()
        {
            typeof(JsonFact.Present), typeof(JsonFact.Absent), typeof(JsonFact.OfType), typeof(JsonFact.OneOf), typeof(JsonFact.NotEqual),
            typeof(JsonFact.Lower), typeof(JsonFact.Upper), typeof(JsonFact.MinItems), typeof(JsonFact.MaxItems), typeof(JsonFact.Requires), typeof(JsonFact.ClosedTo),
            typeof(JsonFact.Form), typeof(JsonFact.Extension),
        };

        /// <summary>A fact of one of the kinds, true of the document.</summary>
        static JsonFact TrueFactOf(Random random, J document, HashSet<Type> kinds, JsonPathSpec? focus = null)
        {
            for (var attempt = 0; ; attempt++)
            {
                var fact = TrueFact(random, document, attempt < 20 ? focus : null);
                if (kinds.Contains(fact.GetType()))
                    return fact;
            }
        }

        /// <summary>
        /// A conditional rule true of the document. Its body is true of it or not; where it is not, the head is drawn
        /// from another document, so that a rule which fires when its body is false is caught stating a falsehood.
        /// </summary>
        static JsonGuardedRule TrueRule(Random random, J document, JsonPathSpec? discriminator, IReadOnlyList<string>? domain, List<JsonPathSpec> focus)
        {
            while (true)
            {
                var other = Documents.Object(random, 0);
                var when = new List<JsonFact>();

                // A body naming one or two of the discriminator's values, the second of which reads a domain within a set.
                // Or refusing one of them, the way gap 2's `kind <> 'invoice'` does.
                if (discriminator is not null)
                    switch (random.Next(3))
                    {
                        case 0:
                            when.Add(new JsonFact.OneOf(discriminator, ValueSet.From(domain!.OrderBy(_ => random.Next()).Take(1 + random.Next(2)))));
                            break;
                        case 1:
                            when.Add(new JsonFact.NotEqual(discriminator, domain![random.Next(domain.Count)]));
                            break;
                    }
                for (var n = when.Count == 0 ? 1 + random.Next(2) : random.Next(2); n > 0; n--)
                    when.Add(TrueFactOf(random, random.Next(2) == 0 ? document : other, BodyKinds, random.Next(2) == 0 ? focus[random.Next(focus.Count)] : null));

                var holds = when.All(f => Documents.IsTrue(document, f));
                var then = Enumerable.Range(0, 1 + random.Next(2)).Select(_ => TrueFactOf(random, holds ? document : other, HeadKinds)).ToList();
                var rule = new JsonGuardedRule(when, then);
                if (Documents.IsTrue(document, rule))
                    return rule;
            }
        }

        /// <summary>A discriminator over a concrete path holding a scalar, with a domain of its value and others.</summary>
        static (JsonPathSpec Path, IReadOnlyList<string> Domain, JsonFact[] Facts)? Discriminator(Random random, J document)
        {
            var candidates = Concrete.Where(c => Documents.Values(document, c) is { Count: 1 } v && v[0].IsScalar).ToList();
            if (candidates.Count == 0)
                return null;

            var path = candidates[random.Next(candidates.Count)];
            var value = Documents.Values(document, path)[0].Encoded;
            var domain = new[] { value }.Concat(Documents.Universe.Where(u => u != value).OrderBy(_ => random.Next()).Take(1 + random.Next(3))).ToList();
            return (path, domain, new JsonFact[] { new JsonFact.Present(path), new JsonFact.OneOf(path, ValueSet.From(domain)), new JsonFact.Discriminates(path) });
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
                var kinds = Enumerable.Range(0, 25).ToList();
                if (objects.Count > 0)
                    kinds.AddRange(new[] { 10, 11, 16, 17, 21, 22, 10, 11, 16, 17, 21, 22 });
                if (arrays.Count > 0)
                    kinds.AddRange(new[] { 8, 9, 15, 19, 20, 8, 9, 15, 19, 20 });
                if (path.IsPattern == false && values.Count > 0)
                    kinds.AddRange(new[] { 18, 18, 18 });
                if (numbers.Count > 0)
                    kinds.AddRange(new[] { 4, 5, 14, 4, 5, 14 });
                if (strings.Count > 0)
                    kinds.AddRange(new[] { 6, 7, 23, 23 });
                if (values.OfType<J.Huge>().Any())
                    kinds.AddRange(new[] { 2, 4, 5, 2, 4, 5 });
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
                    18 when path.IsPattern == false => Concrete.Where(o => o.Equals(path) == false && Documents.IsTrue(document, new JsonFact.Same(path, o))).ToList() is { Count: > 0 } same
                        ? new JsonFact.Same(path, same[random.Next(same.Count)]) : null,
                    19 => Counted(random, values) is var v1
                        ? new JsonFact.ContainsAtLeast(path, v1, Math.Max(0, values.OfType<J.Arr>().Select(a => (long)a.Elements.Count(e => e.IsScalar && e.Encoded == v1)).DefaultIfEmpty(2).Min() - random.Next(2))) : null,
                    20 => Counted(random, values) is var v2
                        ? new JsonFact.ContainsAtMost(path, v2, values.OfType<J.Arr>().Select(a => (long)a.Elements.Count(e => e.IsScalar && e.Encoded == v2)).DefaultIfEmpty(random.Next(2)).Max() + random.Next(2)) : null,
                    21 => new JsonFact.NameMinLength(path, Math.Max(0, objects.SelectMany(o => o.Properties.Keys).Select(k => (long)k.Length).DefaultIfEmpty(2).Min() - random.Next(2))),
                    22 => new JsonFact.NameMaxLength(path, objects.SelectMany(o => o.Properties.Keys).Select(k => (long)k.Length).DefaultIfEmpty(1).Max() + random.Next(2) - (objects.Count == 0 ? 0 : 0)),
                    23 => new JsonFact.Form(path, Documents.Forms[random.Next(Documents.Forms.Length)]),
                    24 => new JsonFact.Extension(path, Documents.Extensions[random.Next(Documents.Extensions.Length)]),
                    _ => null,
                };

                // A proposal false of the document is drawn again: a bound beside a number held approximately, a form
                // beside a value that is not a string, a claim of an object beside a scalar.
                if (fact is not null && Documents.IsTrue(document, fact))
                    return fact;
            }
        }

        /// <summary>A value to count in arrays: half the time one an array there holds, so that counts are exact.</summary>
        static string Counted(Random random, List<J> values)
        {
            var held = values.OfType<J.Arr>().SelectMany(a => a.Elements).Where(e => e.IsScalar).Select(e => e.Encoded).ToList();
            return held.Count > 0 && random.Next(2) == 0 ? held[random.Next(held.Count)] : Documents.Universe[random.Next(Documents.Universe.Length)];
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

            return random.Next(7) switch
            {
                6 => new JsonAtom.SameAs(path, Concrete[random.Next(Concrete.Length)]),
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
