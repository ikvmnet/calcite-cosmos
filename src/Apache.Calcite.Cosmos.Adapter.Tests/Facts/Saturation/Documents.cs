using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// Documents, the values a path holds in one, and whether a fact or a predicate is true of one: the ground truth
    /// the theory is checked against.
    /// </summary>
    static class Documents
    {

        public static readonly string[] Keys = { "a", "b", "c", "dd" };

        /// <summary>
        /// Numbers a document holds. <c>30</c> and <c>30.0</c> are one value to the theory and two renderings to the text
        /// accessor, which is the collision a predicate over the rendering has to keep apart.
        /// </summary>
        static readonly decimal[] NumbersSeen = { -1m, 0m, 1m, 2m, 2.5m, 10m, 499.5m, 500m, 30m, 30.0m };

        /// <summary>
        /// Strings a document holds, among them those whose text a number or a boolean also renders as, and those that
        /// only look like a number.
        /// </summary>
        static readonly string[] StringsSeen = { "", "x", "yy", "abc", "30", "30.0", "true", "1e30" };

        /// <summary>Numbers beyond a decimal's range and below its precision, which the theory holds approximately.</summary>
        static readonly double[] HugeSeen = { 1e30, -1e30, 1.5e-30 };

        /// <summary>The stored forms a fact may name: any string, and a string of at most one character.</summary>
        public static readonly string[] Forms = { "text", "short" };

        /// <summary>The extension claims a fact may name: here, that the value is an object.</summary>
        public static readonly string[] Extensions = { "object" };

        /// <summary>Scalars a fact or a predicate may name: those a document can hold, and some it cannot.</summary>
        public static readonly string[] Universe =
            new[] { NullValue, Of(true), Of(false) }
                .Concat(NumbersSeen.Concat(new[] { 3m, 7.5m }).Select(Of))
                .Concat(HugeSeen.Select(Of))
                .Concat(StringsSeen.Concat(new[] { "zz" }).Select(Of))
                .Distinct()
                .ToArray();

        public static J Generate(Random random, int depth = 0)
        {
            var roll = random.Next(depth >= 2 ? 6 : 8);
            return roll switch
            {
                0 => new J.Null(),
                1 => new J.Bool(random.Next(2) == 0),
                2 or 3 when random.Next(12) == 0 => new J.Huge(HugeSeen[random.Next(HugeSeen.Length)]),
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
                JsonFact.Lower f => v is J.Huge h ? Above(h.Value, f.Bound) : v is not J.Num n || f.Bound.AdmitsAbove(n.Value),
                JsonFact.Upper f => v is J.Huge h ? Below(h.Value, f.Bound) : v is not J.Num n || f.Bound.AdmitsBelow(n.Value),
                JsonFact.Form f => v is J.Null || v is J.Str s && (f.Name != "short" || s.Value.Length <= 1),
                JsonFact.Extension f => f.Key == "object" && v is J.Obj,
                JsonFact.MinLength f => v is not J.Str s || s.Value.Length >= f.Length,
                JsonFact.MaxLength f => v is not J.Str s || s.Value.Length <= f.Length,
                JsonFact.MinItems f => v is not J.Arr a || a.Elements.Count >= f.Count,
                JsonFact.MaxItems f => v is not J.Arr a || a.Elements.Count <= f.Count,
                JsonFact.ClosedTo f => v is not J.Obj o || o.Properties.Keys.All(f.Properties.Contains),
                JsonFact.Requires f => v is not J.Obj o || o.Properties.ContainsKey(f.Property),
                // Whether a number held approximately is a multiple is not decided here, so none is claimed to be.
                JsonFact.MultipleOf f => v is not (J.Num or J.Huge) || v is J.Num n && Multiples.IsMultiple(n.Value, f.Factor),
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

        /// <summary>Whether a number far from every bound lies above a lower one; compared as a double, which is exact there.</summary>
        static bool Above(double x, Bound lower) => lower.Open ? x > (double)lower.Value : x >= (double)lower.Value;

        /// <summary>Whether a number far from every bound lies below an upper one.</summary>
        static bool Below(double x, Bound upper) => upper.Open ? x < (double)upper.Value : x <= (double)upper.Value;

        /// <summary>A value's identity under JSON equality.</summary>
        public static string Key(J value) => value.IsScalar ? value.Encoded : value.ToString();

        /// <summary>
        /// A value as JSON text, compactly, with a number spelled as it is held: <c>30.0</c> stays <c>30.0</c>, which is
        /// what a reader of the text — Calcite's <c>JSON_VALUE</c> — renders it from.
        /// </summary>
        public static string Json(J value) => value switch
        {
            J.Null => "null",
            J.Bool b => b.Value ? "true" : "false",
            J.Num n => n.Value.ToString(CultureInfo.InvariantCulture),
            J.Huge h => h.Value.ToString("R", CultureInfo.InvariantCulture),
            J.Str s => System.Text.Json.JsonSerializer.Serialize(s.Value),
            J.Obj o => "{" + string.Join(",", o.Properties.Select(kv => System.Text.Json.JsonSerializer.Serialize(kv.Key) + ":" + Json(kv.Value))) + "}",
            J.Arr a => "[" + string.Join(",", a.Elements.Select(Json)) + "]",
            _ => throw new NotSupportedException(value.GetType().Name),
        };

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
                JsonAtom.NumberIn n => v is J.Num x && (n.Lower is not Bound l || l.AdmitsAbove(x.Value)) && (n.Upper is not Bound u || u.AdmitsBelow(x.Value))
                    || v is J.Huge h && (n.Lower is not Bound hl || Above(h.Value, hl)) && (n.Upper is not Bound hu || Below(h.Value, hu)),
                JsonAtom.LengthIn n => v is J.Arr a && a.Elements.Count >= (n.Min ?? 0) && a.Elements.Count <= (n.Max ?? long.MaxValue),
                JsonAtom.ArrayHolds h => v is J.Arr a && a.Elements.Any(e => e.IsScalar && e.Encoded == h.Value),
                _ => throw new NotSupportedException(atom.GetType().Name),
            };
        }

    }

}
