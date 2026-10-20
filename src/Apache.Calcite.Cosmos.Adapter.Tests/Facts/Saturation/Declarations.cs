using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
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
    /// <para>
    /// <b>And the characters of a string</b>, a fact of the theory every source states its own way: a declared
    /// <c>pattern</c> as <see cref="JsonClaim.Characters"/>, and an <c>enum</c> or a <c>const</c> of strings from all three
    /// bands, from whose values the theory derives them. Drawn from a stream of their own, beside the declaration, and
    /// held to documents of their own that hold a string of any band wherever the rules let one stand. The declaration
    /// draws from the trial's stream what it drew before the claims existed; a domain is kept only where the declaration
    /// still admits a document, and its values are named apart (<see cref="BandedNames"/>), so that the filter drawn from
    /// the trial's stream is the one it was.
    /// </para>
    /// <para>
    /// <b>Some declare no conditional rule</b>, what every container that declares no schema has: drawn as any other,
    /// and the claims under a kind taken back before a document is drawn (<see cref="Random"/>). Nothing a guard reads is
    /// then a discriminator, so no stratum splits, and the strata are the ones a schema-less container's filters open.
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

        /// <summary>
        /// Strings from every class a <see cref="JsonClaim.Characters"/> claim speaks of: a high unit; a supplementary
        /// character, which is two surrogates; both; characters of the basic plane below U+D800 that are no ASCII, ß and
        /// the Kelvin sign among them, which Calcite's case mapping and the service's part over; a tab, ASCII and trimmed by
        /// the service; U+00A0 and U+3000, trimmed and no ASCII; and U+0000, ASCII, which the service's searches stop at.
        /// </summary>
        public static readonly string[] BandedStrings =
        {
            char.ConvertFromUtf32(0xE000), "a" + char.ConvertFromUtf32(0x1F600), char.ConvertFromUtf32(0xFF21) + char.ConvertFromUtf32(0x1F600), "\u4E2D",
            "a\u00DFb", "\u212A", "a\tb", "\u00A0a\u3000", "a\0b",
        };

        static readonly J[] Banded = BandedStrings.Select(text => (J)new J.Str(text)).ToArray();

        /// <summary>The paths a domain of banded strings may be declared of: those a document holds a scalar at.</summary>
        static readonly JsonDocumentPath[] Scalars = { A, B, N };

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

        /// <summary>Gets whether every rule holds outright, as a declaration with no schema's conditional rules does.</summary>
        public bool IsUnconditional => _rules.All(r => r.Body.Count == 0);

        /// <summary>Gets the scalars the declaration names, by path: what a predicate over the path is likely to be about.</summary>
        public Dictionary<JsonDocumentPath, List<JsonScalar>> Mentioned { get; } = new();

        /// <summary>
        /// Gets the strings a domain drawn from the stream of characters names, by path: what a filter drawn from that
        /// stream compares the path with.
        /// </summary>
        public Dictionary<JsonDocumentPath, List<string>> BandedNames { get; } = new();

        static JsonDocumentPath P(params string[] names) => names.Aggregate(JsonDocumentPath.Root, (path, name) => path.Property(name));

        /// <summary>
        /// Draws a declaration that admits at least one document.
        /// </summary>
        /// <param name="random">The trial's stream.</param>
        /// <param name="bands">A stream of its own for the characters a string is said to hold, or <c>null</c> for none.
        /// A claim of them admits every value <see cref="Values"/> holds, so it never changes which declaration is
        /// drawn.</param>
        /// <param name="unconditional">Whether to take back every claim made under a kind once the declaration is drawn,
        /// so that it states only what holds outright. The trial's stream is drawn from as it would be otherwise until
        /// then.</param>
        public static Declaration Random(Random random, Random? bands = null, bool unconditional = false)
        {
            while (true)
            {
                var declaration = new Declaration();
                declaration.Fill(random, bands);
                if (unconditional)
                    declaration._rules.RemoveAll(r => r.Body.Count > 0);

                if (declaration.TryGenerate(random, out _))
                {
                    if (bands is not null)
                        declaration.FillDomains(bands);

                    return declaration;
                }
            }
        }

        /// <summary>
        /// Adds domains of strings from all three bands, outright and under a kind, from the stream of characters: an
        /// <c>enum</c> of a few, or a <c>const</c>. One that would leave the declaration admitting no document is taken
        /// back.
        /// </summary>
        void FillDomains(Random bands)
        {
            var kinds = _rules.Where(r => r.Body.Count > 0).Select(r => r.Body).Distinct().ToList();

            foreach (var path in Scalars)
            {
                var body = bands.Next(3) == 0 && kinds.Count > 0 ? kinds[bands.Next(kinds.Count)] : null;
                if (bands.Next(body is null ? 4 : 6) != 0)
                    continue;

                // Mostly with a string a document is drawn with beside the banded ones, so that a document the trial's
                // stream draws can still hold the path.
                var values = BandedStrings.OrderBy(_ => bands.Next()).Take(1 + bands.Next(2)).ToList();
                if (bands.Next(4) > 0)
                    values.Add(bands.Next(2) == 0 ? "x" : "abc");

                JsonClaim claim = values.Count == 1 ? new JsonClaim.EqualTo(values[0]) : new JsonClaim.OneOf(values.Select(v => (JsonScalar)v).ToArray());
                var rule = body is null ? Rule(path, claim) : new JsonFactRule(body, new JsonFact(path, claim));

                _rules.Add(rule);
                _branches = null;
                if (Branches().Count == 0)
                {
                    _rules.Remove(rule);
                    _branches = null;
                    continue;
                }

                if (BandedNames.TryGetValue(path, out var named) == false)
                    BandedNames[path] = named = new List<string>();

                named.AddRange(values.Where(v => named.Contains(v) == false));
            }
        }

        void Fill(Random random, Random? bands)
        {
            var guards = new List<JsonFact[]>();

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

                guards.Add(body);
            }

            if (bands is null)
                return;

            foreach (var path in Claimed)
            {
                foreach (var claim in Characters(bands, outright: true))
                    _rules.Add(Rule(path, claim));

                foreach (var body in guards)
                    foreach (var claim in Characters(bands, outright: false))
                        _rules.Add(new JsonFactRule(body, new JsonFact(path, claim)));
            }
        }

        /// <summary>
        /// What a pattern's reader may state of a path: any set of the classes, ASCII and nothing the service trims among
        /// them. Every value <see cref="Values"/> holds is ASCII and trims as Calcite's does, so a claim of them admits each
        /// and never changes which declaration is drawn.
        /// </summary>
        static IEnumerable<JsonClaim> Characters(Random bands, bool outright)
        {
            if (bands.Next(outright ? 6 : 12) == 0)
                yield return new JsonClaim.Characters((JsonCharacterBand)bands.Next((int)JsonCharacterBand.Any + 1));
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
        /// Draws a document the declaration admits from a stream of its own, holding a string of either band at a path
        /// wherever the rules its kind fires let one stand there.
        /// </summary>
        public J.Obj GenerateBanded(Random bands)
        {
            if (TryGenerate(bands, out var document, banded: true) == false)
                throw new InvalidOperationException("The declaration admits no document it once did.");

            return document;
        }

        /// <summary>
        /// One kind a document may be — a value of the discriminator, or none — with what each path may hold under it,
        /// and which of the banded strings.
        /// </summary>
        sealed record Branch(J? Kind, IReadOnlyDictionary<JsonDocumentPath, IReadOnlyList<J?>> Choices, IReadOnlyList<(J? V, IReadOnlyList<J?> Holders)> Objects, IReadOnlyDictionary<JsonDocumentPath, IReadOnlyList<J>> Banded);

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
                var banded = new Dictionary<JsonDocumentPath, IReadOnlyList<J>>();
                foreach (var path in new[] { A, B, N })
                {
                    choices[path] = Candidates.Where(c => Fits(path, c)).ToList();
                    banded[path] = Banded.Where(b => Fits(path, b)).ToList();
                }

                // o.v first, then an o that holds it: an object with it where it is there, anything without it where not.
                var objects = Candidates.Where(v => Fits(V, v))
                    .Select(v => (V: v, Holders: (IReadOnlyList<J?>)Holders(v).Where(o => Fits(O, o)).ToList()))
                    .Where(pair => pair.Holders.Count > 0)
                    .ToList();

                if (choices.Values.All(c => c.Count > 0) && objects.Count > 0)
                    branches.Add(new Branch(kind, choices, objects, banded));
            }

            return _branches = branches;
        }

        static IEnumerable<J?> Holders(J? v) => v is null
            ? Values.Append(null)
            : new J?[] { new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal) { ["v"] = v }), new J.Obj(new SortedDictionary<string, J>(StringComparer.Ordinal) { ["v"] = v, ["x"] = new J.Num(1m) }) };

        bool TryGenerate(Random random, out J.Obj document, bool banded = false)
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
            {
                if (choices[random.Next(choices.Count)] is J value)
                    properties[path.Names[^1]] = value;

                if (banded && branch.Banded[path] is { Count: > 0 } strings && random.Next(2) == 0)
                    properties[path.Names[^1]] = strings[random.Next(strings.Count)];
            }

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

                // Of a string's units, read here unit by unit and not as the theory reads them.
                JsonClaim.Characters c => value is not J.Str s || s.Value.All(unit =>
                    (char.IsSurrogate(unit) == false || c.Bands.HasFlag(JsonCharacterBand.Surrogates))
                    && (unit < 0xE000 || c.Bands.HasFlag(JsonCharacterBand.HighBmp))
                    && (unit < 0x80 || unit >= 0xD800 || c.Bands.HasFlag(JsonCharacterBand.NonAscii))
                    && (Documents.Trimmed.IndexOf(unit) < 0 || c.Bands.HasFlag(JsonCharacterBand.Trimmed))
                    && (unit != '\0' || c.Bands.HasFlag(JsonCharacterBand.Nul))),
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
            JsonClaim.Characters c => "holds ASCII the service trims as Calcite does" + (c.Bands == JsonCharacterBand.None ? "" : " and characters of " + c.Bands),
            var other => other.ToString(),
        };

    }

}
