using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using JsonFact = Apache.Calcite.Cosmos.Facts.JsonFact;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// Random filters over a declaration's paths, in the typed IR, of the shapes the loader builds: each reading compared
    /// with a literal of the kind it yields or with a parameter, a membership, a null test, and the connectives.
    /// </summary>
    static class Statements
    {

        static readonly string[] Texts = { "30", "30.0", "true", "x", "", "1e30", "1.0E30", "abc", "7", "2.5", "k0", "k1" };

        static readonly decimal[] Numbers = { 30m, 7m, -1m, 0m, 2.5m, 10m };

        static readonly string[] Orderings = { PredicateAtom.Less, PredicateAtom.LessOrEqual, PredicateAtom.Greater, PredicateAtom.GreaterOrEqual };

        /// <summary>
        /// Draws a statement of one to four conjuncts. A third of them share an atom with another, so that one conjunct's
        /// facts meet another's terms: <c>a = 'x' AND NOT (a = 'x' AND b = 'y')</c>.
        /// </summary>
        public static IReadOnlyList<SqlPredicate> Draw(Random random, Declaration declaration)
        {
            var atoms = new List<SqlPredicate>();
            var conjuncts = new List<SqlPredicate>();
            for (var n = 1 + random.Next(4); n > 0; n--)
                conjuncts.Add(Predicate(random, declaration, 0, atoms));

            return conjuncts;
        }

        /// <summary>
        /// Draws one predicate, sharing an atom already drawn now and then.
        /// </summary>
        public static SqlPredicate Predicate(Random random, Declaration declaration, int depth, List<SqlPredicate> atoms)
        {
            var roll = random.Next(20);
            if (depth < 2 && roll < 4)
                return new SqlPredicate.Not(Predicate(random, declaration, depth + 1, atoms));

            if (depth < 2 && roll < 8)
            {
                var arms = Enumerable.Range(0, 2 + random.Next(2)).Select(_ => Predicate(random, declaration, depth + 1, atoms)).ToArray();
                var junction = random.Next(2) == 0 ? SqlPredicate.AllOf(arms) : SqlPredicate.AnyOf(arms);
                return junction;
            }

            if (roll == 8)
                return new SqlPredicate.Opaque(random.Next(3));

            if (atoms.Count > 0 && random.Next(3) == 0)
                return atoms[random.Next(atoms.Count)];

            var atom = Atom(random, declaration);
            atoms.Add(atom);
            return atom;
        }

        static SqlPredicate Atom(Random random, Declaration declaration)
        {
            var paths = new[] { Declaration.Kind, Declaration.A, Declaration.A, Declaration.B, Declaration.N, Declaration.N, Declaration.V, Declaration.O };
            var path = paths[random.Next(paths.Length)];
            var reading = Reading(random, path);

            var roll = random.Next(20);
            if (roll < 3)
                return new SqlPredicate.IsNull(reading);
            if (roll < 4)
                return new SqlPredicate.Not(new SqlPredicate.IsNull(reading));
            if (roll < 6)
                return new SqlPredicate.Compare(Op(random), reading, Parameter(random, reading));
            if (roll < 10)
                return new SqlPredicate.In(reading, ValueSet.From(Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Literal(random, declaration, path, reading).Value)));
            if (roll == 19)
                return new SqlPredicate.IsDefined(new SqlValue.Raw(path));

            return new SqlPredicate.Compare(Op(random), reading, Literal(random, declaration, path, reading));
        }

        /// <summary>
        /// Draws, from the stream of characters, conjuncts that compare a path with strings from all three bands: an
        /// equality, a membership, or a disjunction of two of them, over the text or the raw value. Half the time the
        /// strings are those the declaration's own domain of the path names, so that a filter and a declaration meet.
        /// </summary>
        /// <remarks>
        /// What a filter's <c>=</c> and <c>IN</c> say of a path's characters is the theory's to derive, per disjunct; these
        /// are what it is checked on, appended to what the trial's stream drew, which they leave as it was.
        /// </remarks>
        public static IEnumerable<SqlPredicate> Banded(Random bands, Declaration declaration)
        {
            if (bands.Next(2) == 0)
                yield break;

            var paths = new[] { Declaration.A, Declaration.B, Declaration.N };

            SqlPredicate Atom(JsonDocumentPath path)
            {
                var pool = declaration.BandedNames.TryGetValue(path, out var named) && bands.Next(2) == 0
                    ? named
                    : Declaration.BandedStrings.Append("x").Append("30").ToList();

                SqlValue reading = bands.Next(3) == 0 ? new SqlValue.Raw(path) : new SqlValue.Text(path);
                return bands.Next(2) == 0
                    ? new SqlPredicate.Compare(PredicateAtom.Equal, reading, SqlValue.Of(pool[bands.Next(pool.Count)]))
                    : new SqlPredicate.In(reading, ValueSet.From(Enumerable.Range(0, 1 + bands.Next(3)).Select(_ => SqlValue.Of(pool[bands.Next(pool.Count)]).Value)));
            }

            var first = paths[bands.Next(paths.Length)];
            yield return bands.Next(3) == 0
                ? SqlPredicate.AnyOf(new[] { Atom(first), Atom(bands.Next(3) == 0 ? paths[bands.Next(paths.Length)] : first) })
                : Atom(first);
        }

        /// <summary>
        /// Draws, from a stream of its own, strings computed from the declaration's paths and from strings of every class:
        /// a concatenation, a case mapping, a window, a reversal, a repetition, a replacement, a trim and a conditional,
        /// nested two deep, as the loader reads a call of each. What the theory derives of their characters is checked on
        /// them (<see cref="PopulationD"/>).
        /// </summary>
        public static IReadOnlyList<SqlValue> Computed(Random random, Declaration declaration)
        {
            var paths = new[] { Declaration.A, Declaration.B, Declaration.N, Declaration.V };

            SqlValue Leaf()
            {
                if (random.Next(3) > 0)
                    return new SqlValue.Text(paths[random.Next(paths.Length)]);

                var pool = Declaration.BandedStrings.Append("x").Append("").Append(" a ").Concat(declaration.BandedNames.Values.SelectMany(named => named)).ToArray();
                return SqlValue.Of(pool[random.Next(pool.Length)]);
            }

            SqlValue Number(int least, int most) => SqlValue.Of((decimal)random.Next(least, most + 1));

            SqlValue Term(int depth)
            {
                if (depth == 0 || random.Next(4) == 0)
                    return Leaf();

                SqlValue Next() => Term(depth - 1);

                return random.Next(12) switch
                {
                    0 => new SqlValue.Call("||", [Next(), Next()]),
                    1 => new SqlValue.Call("CONCAT", [Next(), Next(), Next()]),
                    2 or 3 => new SqlValue.Call("UPPER", [Next()]),
                    4 => new SqlValue.Call("LOWER", [Next()]),
                    5 => new SqlValue.Call("SUBSTRING", [Next(), Number(-1, 3), Number(0, 3)]),
                    6 => new SqlValue.Call(random.Next(2) == 0 ? "LEFT" : "RIGHT", [Next(), Number(0, 2)]),
                    7 => new SqlValue.Call("REVERSE", [Next()]),
                    8 => new SqlValue.Call("REPEAT", [Next(), Number(0, 2)]),
                    9 => new SqlValue.Call("REPLACE", [Next(), SqlValue.Of(random.Next(2) == 0 ? "a" : "x"), Next()]),
                    10 => new SqlValue.Call(new[] { "TRIM BOTH", "TRIM LEADING", "TRIM TRAILING" }[random.Next(3)], [SqlValue.Of(" "), Next()]),
                    _ => new SqlValue.Call("CASE", [Next(), Next()]),
                };
            }

            // Each case mapping over a string of one class, beside three terms drawn whole: the mapping is where a class
            // can be gained, and a term drawn whole reaches one over a single class only now and then.
            SqlValue Banded() => SqlValue.Of(Declaration.BandedStrings[random.Next(Declaration.BandedStrings.Length)]);

            return Enumerable.Range(0, 3).Select(_ => Term(2))
                .Append(new SqlValue.Call("UPPER", [Banded()]))
                .Append(new SqlValue.Call("LOWER", [Banded()]))
                .ToArray();
        }

        static string Op(Random random) => random.Next(10) switch
        {
            < 4 => PredicateAtom.Equal,
            < 6 => PredicateAtom.NotEqual,
            _ => Orderings[random.Next(Orderings.Length)],
        };

        /// <summary>
        /// A reading of a path: the text accessor most often, then the raw value, a <c>RETURNING</c>, a numeric cast and
        /// the cast to text over a raw value.
        /// </summary>
        public static SqlValue Reading(Random random, JsonDocumentPath path) => random.Next(20) switch
        {
            < 9 => new SqlValue.Text(path),
            < 11 => new SqlValue.Raw(path),
            < 14 => new SqlValue.Typed(path, new[] { "INTEGER", "BIGINT", "DOUBLE", "DECIMAL", "BOOLEAN" }[random.Next(5)]),
            < 18 => new SqlValue.Cast(new[] { "INTEGER", "BIGINT", "DOUBLE" }[random.Next(3)], random.Next(3) == 0 ? new SqlValue.Raw(path) : new SqlValue.Text(path)),
            _ => new SqlValue.Cast("VARCHAR", new SqlValue.Raw(path)),
        };

        /// <summary>
        /// A literal of the kind the reading is compared with, half the time one the declaration names for the path — or,
        /// for a rendering, the text one renders as.
        /// </summary>
        public static SqlValue.Literal Literal(Random random, Declaration declaration, JsonDocumentPath path, SqlValue reading)
        {
            var named = declaration.Mentioned.TryGetValue(path, out var list) && list.Count > 0 && random.Next(3) > 0
                ? list[random.Next(list.Count)]
                : (JsonScalar?)null;

            switch (reading)
            {
                case SqlValue.Text or SqlValue.Cast { Target: "VARCHAR" }:
                    if (named is JsonScalar n && n.IsNull == false && IrEval.Rendering(Scalar(n)) is string rendered)
                        return SqlValue.Of(rendered);
                    return SqlValue.Of(path.Equals(Declaration.Kind) && random.Next(2) == 0 ? Declaration.Kinds[random.Next(Declaration.Kinds.Length)] : Texts[random.Next(Texts.Length)]);

                case SqlValue.Typed { Type: "BOOLEAN" }:
                    return SqlValue.Of(random.Next(2) == 0);

                case SqlValue.Raw:
                    if (named is JsonScalar any && any.IsNull == false)
                        return SqlValue.Of(any);
                    return random.Next(3) switch
                    {
                        0 => SqlValue.Of(Texts[random.Next(Texts.Length)]),
                        1 => SqlValue.Of(random.Next(2) == 0),
                        _ => SqlValue.Of(Numbers[random.Next(Numbers.Length)]),
                    };

                default:
                    if (named is JsonScalar number && number.ToClrValue() is decimal)
                        return SqlValue.Of(number);
                    return SqlValue.Of(Numbers[random.Next(Numbers.Length)]);
            }
        }

        static SqlValue Parameter(Random random, SqlValue reading) => new SqlValue.Parameter(random.Next(2), reading switch
        {
            SqlValue.Text or SqlValue.Cast { Target: "VARCHAR" } => "VARCHAR",
            SqlValue.Raw => "ANY",
            SqlValue.Typed { Type: "BOOLEAN" } => "BOOLEAN",
            SqlValue.Typed { Type: "DOUBLE" } or SqlValue.Cast { Target: "DOUBLE" } => "DOUBLE",
            _ => "INTEGER",
        });

        static J Scalar(JsonScalar value) => value.ToClrValue() switch
        {
            null => new J.Null(),
            bool b => new J.Bool(b),
            string s => new J.Str(s),
            decimal m => new J.Num(m),
            double d => new J.Huge(d),
            _ => throw new NotSupportedException(),
        };

    }

}
