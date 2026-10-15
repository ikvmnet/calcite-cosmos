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
    /// A container's declaration drawn at random — a discriminator, and claims about a handful of paths, some outright
    /// and some under a kind — and the documents it admits, drawn from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The documents are the declaration's, not the query's.</b> What is drawn satisfies every rule and nothing else:
    /// a path no claim speaks of holds anything — a string, a number spelled two ways, a boolean, a null, an object or an
    /// array — or is absent. That is population D of the soundness review: every fact a scope proves of the rows a filter
    /// keeps is checked on the rows of every declared document the filter keeps, and none of them was chosen to make the
    /// filter's own facts true.
    /// </para>
    /// <para>
    /// <b>Values whose renderings collide</b>: <c>30</c>, <c>30.0</c>, <c>"30"</c>, <c>1e30</c>, <c>true</c> and
    /// <c>"true"</c>, strings that only look like numbers, and a structure at a path a predicate reads as a scalar.
    /// </para>
    /// </remarks>
    sealed class Declaration
    {

        public static readonly JsonDocumentPath Kind = P("kind");
        public static readonly JsonDocumentPath A = P("a");
        public static readonly JsonDocumentPath B = P("b");
        public static readonly JsonDocumentPath N = P("n");
        public static readonly JsonDocumentPath O = P("o");
        public static readonly JsonDocumentPath V = P("o", "v");

        /// <summary>The paths a claim may be made of, the discriminator aside; <c>o.v</c> before <c>o</c>, which holds it.</summary>
        public static readonly JsonDocumentPath[] Claimed = { A, B, N, V, O };

        /// <summary>Every path a document has a place for.</summary>
        public static readonly JsonDocumentPath[] Paths = { Kind, A, B, N, O, V };

        public static readonly string[] Kinds = { "k0", "k1", "k2" };

        /// <summary>What a path may hold: every kind of JSON value, numbers and strings that render alike among them.</summary>
        public static readonly J[] Values =
        {
            new J.Str("30"), new J.Str("30.0"), new J.Str("true"), new J.Str("x"), new J.Str(""), new J.Str("1e30"), new J.Str(" 30 "), new J.Str("abc"),
            new J.Num(30m), new J.Num(30.0m), new J.Num(2.5m), new J.Num(-1m), new J.Num(0m), new J.Num(7m), new J.Num(7.0m), new J.Huge(1e30),
            new J.Bool(true), new J.Bool(false),
            new J.Null(),
            new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal)),
            new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal) { ["x"] = new J.Num(1m) }),
            new J.Arr(new List<J>()), new J.Arr(new List<J> { new J.Num(30m) }), new J.Arr(new List<J> { new J.Str("x") }),
        };

        /// <summary>The scalars a claim names: a const, a member of an enum, a value a path is unequal to.</summary>
        static readonly JsonScalar[] Named = { "30", "x", "true", "", 30m, 30m, 2.5m, 7m, 7m, -1m, true, false, JsonScalar.Null, 1e30 };

        static readonly decimal[] Edges = { -1m, 0m, 2.5m, 7m, 10m, 30m };

        static readonly JsonType[] Types = { JsonType.String, JsonType.Integer, JsonType.Number, JsonType.Boolean, JsonType.Object, JsonType.Array, JsonType.Null };

        readonly List<JsonFactRule> _rules = new();

        Declaration()
        {
        }

        /// <summary>Gets the rules, as a container's theory is built from them.</summary>
        public IReadOnlyList<JsonFactRule> Rules => _rules;

        /// <summary>Gets the scalars the declaration names, by path: what a predicate over the path is likely to be about.</summary>
        public Dictionary<JsonDocumentPath, List<JsonScalar>> Mentioned { get; } = new();

        static JsonDocumentPath P(params string[] names) => names.Aggregate(JsonDocumentPath.Root, (path, name) => path.Property(name));

        /// <summary>
        /// Draws a declaration that admits at least one document.
        /// </summary>
        public static Declaration Random(Random random)
        {
            while (true)
            {
                var declaration = new Declaration();
                declaration.Fill(random);
                if (declaration.TryGenerate(random, out _))
                    return declaration;
            }
        }

        void Fill(Random random)
        {
            // The discriminator: a domain of kinds, required most of the time.
            _rules.Add(Rule(Kind, new JsonClaim.OneOf(Kinds.Select(k => (JsonScalar)k).ToArray())));
            if (random.Next(10) < 7)
                _rules.Add(Rule(Kind, new JsonClaim.Present()));

            foreach (var path in Claimed)
                foreach (var claim in Claims(random, path, outright: true))
                    _rules.Add(Rule(path, claim));

            foreach (var kind in Kinds)
            {
                // Most guards name one kind, as a oneOf's branch does; some name two, and some refuse one, as gap 2's
                // `kind <> 'invoice'` does.
                var body = random.Next(6) switch
                {
                    0 => new[] { new JsonFact(Kind, new JsonClaim.Present()), new JsonFact(Kind, new JsonClaim.OneOf(new JsonScalar[] { kind, Kinds[(Array.IndexOf(Kinds, kind) + 1) % Kinds.Length] })) },
                    1 => new[] { new JsonFact(Kind, new JsonClaim.NotEqualTo(kind)) },
                    _ => new[] { new JsonFact(Kind, new JsonClaim.Present()), new JsonFact(Kind, new JsonClaim.EqualTo(kind)) },
                };

                foreach (var path in Claimed)
                    foreach (var claim in Claims(random, path, outright: false))
                        _rules.Add(new JsonFactRule(body, new JsonFact(path, claim)));
            }
        }

        IEnumerable<JsonClaim> Claims(Random random, JsonDocumentPath path, bool outright)
        {
            var scale = outright ? 1 : 2;
            if (random.Next(10 * scale) < 4)
                yield return new JsonClaim.OfType(Types[random.Next(Types.Length)], random.Next(3) == 0);
            if (random.Next(10 * scale) < 3)
                yield return new JsonClaim.Present();
            if (random.Next(10 * scale) < 2)
                yield return new JsonClaim.EqualTo(Name(random, path));
            if (random.Next(10 * scale) < 2)
                yield return new JsonClaim.OneOf(Enumerable.Range(0, 2 + random.Next(2)).Select(_ => Name(random, path)).Distinct().ToArray());
            if (random.Next(10 * scale) < 1)
                yield return new JsonClaim.NotEqualTo(Name(random, path));
            if (random.Next(10 * scale) < 1)
                yield return new JsonClaim.LowerBound(Edges[random.Next(Edges.Length)], random.Next(2) == 0);
            if (random.Next(10 * scale) < 1)
                yield return new JsonClaim.UpperBound(Edges[random.Next(Edges.Length)], random.Next(2) == 0);
        }

        JsonScalar Name(Random random, JsonDocumentPath path)
        {
            var value = Named[random.Next(Named.Length)];
            if (Mentioned.TryGetValue(path, out var list) == false)
                Mentioned[path] = list = new List<JsonScalar>();

            if (list.Contains(value) == false)
                list.Add(value);

            return value;
        }

        static JsonFactRule Rule(JsonDocumentPath path, JsonClaim claim) => JsonFactRule.Unconditional(new JsonFact(path, claim));

        /// <summary>
        /// Draws a document the declaration admits: the kind first, and each path from what the rules that kind fires
        /// leave it.
        /// </summary>
        public J.Obj Generate(Random random)
        {
            if (TryGenerate(random, out var document) == false)
                throw new InvalidOperationException("The declaration admits no document it once did.");

            return document;
        }

        /// <summary>
        /// One kind a document may be — a value of the discriminator, or none — with what each path may hold under it.
        /// </summary>
        sealed record Branch(J? Kind, IReadOnlyDictionary<JsonDocumentPath, IReadOnlyList<J?>> Choices, IReadOnlyList<(J? V, IReadOnlyList<J?> Holders)> Objects);

        IReadOnlyList<Branch>? _branches;

        /// <summary>A path's candidates: every value, the scalars twice as often, and nothing.</summary>
        static readonly J?[] Candidates = Values.Append(null).Concat(Values.Where(v => v.IsScalar)).ToArray();

        /// <summary>
        /// The kinds a document may be, each with what its rules leave each path: every kind whose rules some value of
        /// every path satisfies.
        /// </summary>
        IReadOnlyList<Branch> Branches()
        {
            if (_branches is not null)
                return _branches;

            var branches = new List<Branch>();
            var outright = _rules.Where(r => r.Body.Count == 0 && r.Head.Path.Equals(Kind)).Select(r => r.Head.Claim).ToList();
            foreach (var kind in Kinds.Select(k => (J?)new J.Str(k)).Append(null))
            {
                if (outright.All(c => Holds(kind, c)) == false)
                    continue;

                var probe = new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal));
                if (kind is not null)
                    probe.Properties["kind"] = kind;

                // A guard reads the discriminator alone, so the rules a kind fires are known before anything else is drawn.
                var fired = _rules.Where(rule => rule.Body.All(atom => Holds(probe, atom))).Select(rule => rule.Head).ToList();
                bool Fits(JsonDocumentPath path, J? value) => fired.Where(f => f.Path.Equals(path)).All(f => Holds(value, f.Claim));

                var choices = new Dictionary<JsonDocumentPath, IReadOnlyList<J?>>();
                foreach (var path in new[] { A, B, N })
                    choices[path] = Candidates.Where(c => Fits(path, c)).ToList();

                // o.v first, then an o that holds it: an object with it where it is there, anything without it where not.
                var objects = Candidates.Where(v => Fits(V, v))
                    .Select(v => (V: v, Holders: (IReadOnlyList<J?>)Holders(v).Where(o => Fits(O, o)).ToList()))
                    .Where(pair => pair.Holders.Count > 0)
                    .ToList();

                if (choices.Values.All(c => c.Count > 0) && objects.Count > 0)
                    branches.Add(new Branch(kind, choices, objects));
            }

            return _branches = branches;
        }

        static IEnumerable<J?> Holders(J? v) => v is null
            ? Values.Append(null)
            : new J?[] { new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal) { ["v"] = v }), new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal) { ["v"] = v, ["x"] = new J.Num(1m) }) };

        bool TryGenerate(Random random, out J.Obj document)
        {
            var branches = Branches();
            if (branches.Count == 0)
            {
                document = null!;
                return false;
            }

            var branch = branches[random.Next(branches.Count)];
            var properties = new SortedDictionary<string, J>(StringComparer.Ordinal);
            if (branch.Kind is not null)
                properties["kind"] = branch.Kind;

            foreach (var (path, choices) in branch.Choices)
                if (choices[random.Next(choices.Count)] is J value)
                    properties[path.Names[^1]] = value;

            var (_, holders) = branch.Objects[random.Next(branch.Objects.Count)];
            if (holders[random.Next(holders.Count)] is J o)
                properties["o"] = o;

            document = new J.Obj(properties);
            if (Admits(document) == false)
                throw new InvalidOperationException($"The generator drew {Documents.Json(document)}, which the declaration does not admit.");

            return true;
        }

        /// <summary>
        /// Determines whether a document satisfies every rule: its body false of it, or its head true.
        /// </summary>
        public bool Admits(J.Obj document) =>
            _rules.All(rule => rule.Body.All(atom => Holds(document, atom)) == false || Holds(document, rule.Head));

        /// <summary>
        /// Determines whether a fact is true of a document, as the theory reads a claim: of the value there if there is
        /// one, and presence of whether there is.
        /// </summary>
        public static bool Holds(J.Obj document, JsonFact fact) => Holds(IrEval.At(document, fact.Path), fact.Claim);

        /// <summary>
        /// Determines whether a claim is true of what a path holds, <c>null</c> where it holds nothing.
        /// </summary>
        public static bool Holds(J? value, JsonClaim claim)
        {
            if (claim is JsonClaim.Present)
                return value is not null;

            if (value is null)
                return true;

            return claim switch
            {
                JsonClaim.OfType t => JsonFactEncoding.Types(t.Type, t.OrNull).Contains(value.Type),
                JsonClaim.EqualTo e => value.IsScalar && value.Encoded == JsonFactEncoding.Value(e.Value),
                JsonClaim.OneOf d => value.IsScalar && JsonFactEncoding.Domain(d.Values).Contains(value.Encoded),
                JsonClaim.NotEqualTo n => value.IsScalar == false || value.Encoded != JsonFactEncoding.Value(n.Value),
                JsonClaim.LowerBound l => value switch
                {
                    J.Num x => l.Admits(x.Value),
                    J.Huge h => l.Exclusive ? h.Value > (double)l.Value : h.Value >= (double)l.Value,
                    _ => true,
                },
                JsonClaim.UpperBound u => value switch
                {
                    J.Num x => u.Admits(x.Value),
                    J.Huge h => u.Exclusive ? h.Value < (double)u.Value : h.Value <= (double)u.Value,
                    _ => true,
                },
                _ => throw new NotSupportedException(claim.GetType().Name),
            };
        }

        /// <inheritdoc />
        public override string ToString() => string.Join("\n", _rules.Select(r =>
            (r.Body.Count == 0 ? "" : "when " + string.Join(" and ", r.Body.Select(Spell)) + ": ") + Spell(r.Head)));

        static string Spell(JsonFact fact) => SqlValue.Spell(fact.Path) + " " + fact.Claim switch
        {
            JsonClaim.Present => "present",
            JsonClaim.OfType t => "of type " + t.Type + (t.OrNull ? " or null" : ""),
            JsonClaim.EqualTo e => "= " + e.Value,
            JsonClaim.OneOf d => "in {" + string.Join(", ", d.Values) + "}",
            JsonClaim.NotEqualTo n => "<> " + n.Value,
            JsonClaim.LowerBound l => (l.Exclusive ? "> " : ">= ") + l.Value,
            JsonClaim.UpperBound u => (u.Exclusive ? "< " : "<= ") + u.Value,
            var other => other.ToString(),
        };

    }

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
