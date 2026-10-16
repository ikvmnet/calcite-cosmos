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
