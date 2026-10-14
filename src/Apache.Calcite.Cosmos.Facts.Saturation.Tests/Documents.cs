using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation.Tests
{

    /// <summary>A JSON value, small enough to enumerate and generate.</summary>
    abstract record J
    {

        public sealed record Null : J;

        public sealed record Bool(bool Value) : J;

        public sealed record Num(decimal Value) : J;

        public sealed record Str(string Value) : J;

        public sealed record Obj(SortedDictionary<string, J> Properties) : J;

        public sealed record Arr(List<J> Elements) : J;

        public string Type => this switch
        {
            Null => JsonValues.NullType,
            Bool => JsonValues.BooleanType,
            Num n => n.Value == decimal.Truncate(n.Value) ? IntegerType : FractionType,
            Str => JsonValues.StringType,
            Obj => JsonValues.ObjectType,
            _ => JsonValues.ArrayType,
        };

        public bool IsScalar => this is not (Obj or Arr);

        /// <summary>The scalar's tagged spelling, as the theory writes it.</summary>
        public string Encoded => this switch
        {
            Null => NullValue,
            Bool b => Of(b.Value),
            Num n => Of(n.Value),
            Str s => Of(s.Value),
            _ => throw new InvalidOperationException("not a scalar"),
        };

        public sealed override string ToString() => this switch
        {
            Null => "null",
            Bool b => b.Value ? "true" : "false",
            Num n => n.Value.ToString(CultureInfo.InvariantCulture),
            Str s => "\"" + s.Value + "\"",
            Obj o => "{" + string.Join(", ", o.Properties.Select(kv => $"\"{kv.Key}\": {kv.Value}")) + "}",
            Arr a => "[" + string.Join(", ", a.Elements) + "]",
            _ => "?",
        };

    }

    /// <summary>
    /// Documents, the values a path holds in one, and whether a fact or a predicate is true of one: the ground truth
    /// the theory is checked against.
    /// </summary>
    static class Documents
    {

        public static readonly string[] Keys = { "a", "b", "c", "dd" };

        static readonly decimal[] NumbersSeen = { -1m, 0m, 1m, 2m, 2.5m, 10m, 499.5m, 500m };

        static readonly string[] StringsSeen = { "", "x", "yy", "abc" };

        /// <summary>Scalars a fact or a predicate may name: those a document can hold, and some it cannot.</summary>
        public static readonly string[] Universe =
            new[] { NullValue, Of(true), Of(false) }
                .Concat(NumbersSeen.Concat(new[] { 3m, 7.5m }).Select(Of))
                .Concat(StringsSeen.Concat(new[] { "zz" }).Select(Of))
                .ToArray();

        public static J Generate(Random random, int depth = 0)
        {
            var roll = random.Next(depth >= 2 ? 6 : 8);
            return roll switch
            {
                0 => new J.Null(),
                1 => new J.Bool(random.Next(2) == 0),
                2 or 3 => new J.Num(NumbersSeen[random.Next(NumbersSeen.Length)]),
                4 or 5 => new J.Str(StringsSeen[random.Next(StringsSeen.Length)]),
                6 => Object(random, depth + 1),
                _ => new J.Arr(Enumerable.Range(0, random.Next(5)).Select(_ => Generate(random, depth + 1)).ToList()),
            };
        }

        public static J.Obj Object(Random random, int depth)
        {
            var properties = new SortedDictionary<string, J>(StringComparer.Ordinal);
            foreach (var key in Keys)
                if (random.Next(10) < 6)
                    properties[key] = Generate(random, depth);

            return new J.Obj(properties);
        }

        /// <summary>A copy of a document whose property <paramref name="to"/> holds what <paramref name="from"/> does.</summary>
        public static J.Obj Copying(J.Obj document, string from, string to)
        {
            var properties = new SortedDictionary<string, J>(document.Properties, StringComparer.Ordinal);
            if (properties.TryGetValue(from, out var value))
                properties[to] = value;
            else
                properties.Remove(to);

            return new J.Obj(properties);
        }

        /// <summary>
        /// The values at every path a path stands for: none or one for a concrete path, every element's for a pattern.
        /// </summary>
        public static List<J> Values(J document, JsonPathSpec path)
        {
            IEnumerable<J> current = new[] { document };
            foreach (var segment in path.Segments)
                current = current.SelectMany(value => segment switch
                {
                    string name => value is J.Obj o && o.Properties.TryGetValue(name, out var child) ? new[] { child } : System.Array.Empty<J>(),
                    long index => value is J.Arr a && index < a.Elements.Count ? new[] { a.Elements[(int)index] } : System.Array.Empty<J>(),
                    _ => value is J.Arr a ? a.Elements : (IEnumerable<J>)System.Array.Empty<J>(),
                });

            return current.ToList();
        }

        /// <summary>Whether a fact is true of a document.</summary>
        public static bool IsTrue(J document, JsonFact fact)
        {
            var values = Values(document, fact.Path);
            switch (fact)
            {
                case JsonFact.Discriminates:
                    return true;
                case JsonFact.Same same:
                    var others = Values(document, same.Other);
                    return values.Count == 0 && others.Count == 0
                        || values.Count == 1 && others.Count == 1 && Key(values[0]) == Key(others[0]);
                case JsonFact.Present:
                    return values.Count > 0;
                case JsonFact.Absent:
                    return values.Count == 0;
                case JsonFact.Holds holds:
                    return values.Any(v => v is J.Arr a && a.Elements.Any(e => e.IsScalar && e.Encoded == holds.Value));
            }

            // Every other fact is conditional on there being a value, and holds of each one there is.
            return values.All(v => fact switch
            {
                JsonFact.OfType f => f.Types.Contains(v.Type),
                JsonFact.OneOf f => v.IsScalar && f.Values.Contains(v.Encoded),
                JsonFact.NotEqual f => v.IsScalar == false || v.Encoded != f.Value,
                JsonFact.Lower f => v is not J.Num n || f.Bound.AdmitsAbove(n.Value),
                JsonFact.Upper f => v is not J.Num n || f.Bound.AdmitsBelow(n.Value),
                JsonFact.MinLength f => v is not J.Str s || s.Value.Length >= f.Length,
                JsonFact.MaxLength f => v is not J.Str s || s.Value.Length <= f.Length,
                JsonFact.MinItems f => v is not J.Arr a || a.Elements.Count >= f.Count,
                JsonFact.MaxItems f => v is not J.Arr a || a.Elements.Count <= f.Count,
                JsonFact.ClosedTo f => v is not J.Obj o || o.Properties.Keys.All(f.Properties.Contains),
                JsonFact.Requires f => v is not J.Obj o || o.Properties.ContainsKey(f.Property),
                JsonFact.MultipleOf f => v is not J.Num n || Multiples.IsMultiple(n.Value, f.Factor),
                JsonFact.UniqueItems => v is not J.Arr a || a.Elements.Select(Key).Distinct().Count() == a.Elements.Count,
                JsonFact.MinProperties f => v is not J.Obj o || o.Properties.Count >= f.Count,
                JsonFact.MaxProperties f => v is not J.Obj o || o.Properties.Count <= f.Count,
                JsonFact.ContainsAtLeast f => v is not J.Arr a || a.Elements.Count(e => e.IsScalar && e.Encoded == f.Value) >= f.Count,
                JsonFact.ContainsAtMost f => v is not J.Arr a || a.Elements.Count(e => e.IsScalar && e.Encoded == f.Value) <= f.Count,
                JsonFact.NameMinLength f => v is not J.Obj o || o.Properties.Keys.All(k => k.Length >= f.Length),
                JsonFact.NameMaxLength f => v is not J.Obj o || o.Properties.Keys.All(k => k.Length <= f.Length),
                _ => throw new NotSupportedException(fact.GetType().Name),
            });
        }

        /// <summary>A value's identity under JSON equality.</summary>
        public static string Key(J value) => value.IsScalar ? value.Encoded : value.ToString();

        /// <summary>Whether a conditional rule is true of a document: its body false of it, or its head true.</summary>
        public static bool IsTrue(J document, JsonGuardedRule rule) =>
            rule.When.All(f => IsTrue(document, f)) == false || rule.Then.All(f => IsTrue(document, f));

        /// <summary>Whether a predicate is true of a document.</summary>
        public static bool IsTrue(J document, JsonAtom atom)
        {
            var values = Values(document, atom.Path);
            if (values.Count != 1)
                return false;

            if (atom is JsonAtom.SameAs same)
                return Values(document, same.Other) is { Count: 1 } other && Key(other[0]) == Key(values[0]);

            var v = values[0];
            return atom switch
            {
                JsonAtom.Defined => true,
                JsonAtom.HasType t => JsonValues.Types(t.Type).Contains(v.Type),
                JsonAtom.EqualTo e => v.IsScalar && v.Encoded == e.Value,
                JsonAtom.NumberIn n => v is J.Num x && (n.Lower is not Bound l || l.AdmitsAbove(x.Value)) && (n.Upper is not Bound u || u.AdmitsBelow(x.Value)),
                JsonAtom.LengthIn n => v is J.Arr a && a.Elements.Count >= (n.Min ?? 0) && a.Elements.Count <= (n.Max ?? long.MaxValue),
                JsonAtom.ArrayHolds h => v is J.Arr a && a.Elements.Any(e => e.IsScalar && e.Encoded == h.Value),
                _ => throw new NotSupportedException(atom.GetType().Name),
            };
        }

    }

}
