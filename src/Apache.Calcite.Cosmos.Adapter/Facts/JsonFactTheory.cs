using System;
using System.Collections.Generic;
using System.Threading;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A container's declared schema, compiled to a context of the theory of JSON that the planner can ask questions of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three layers above it, and one per query below.</b> The rules of JSON are the graph's own, frozen in its root;
    /// the rules of JSON Schema are local to a frozen child of the root; and this declaration is a frozen context beneath
    /// that, holding its unconditional facts as facts and its conditional ones as rows a fixed interpreter reads. Asking
    /// it is <see cref="Derive"/>: what a query established becomes a context beneath this one, saturated, and read.
    /// </para>
    /// <para>
    /// <b>A conditional rule is a row, not a rule of the engine.</b> One engine rule per declared rule costs every query
    /// an evaluation of each, fired or not — half a second at thirty thousand. As rows, a query costs the rules it fires.
    /// </para>
    /// <para>
    /// <b>A disjunction is reasoned about by cases.</b> Where a query leaves a discriminator two or more values, the
    /// context splits on them, one case per value, and keeps what every case proves: <c>kind IN ('order', 'invoice')</c>
    /// proves what both kinds declare, which no single guard does.
    /// </para>
    /// <para>
    /// <b>Compiled when first asked, and again in each generation.</b> The graph a declaration compiles into is retired
    /// once it has interned enough (see <see cref="JsonFactGenerations"/>), and the next question compiles it into the
    /// one that replaced it. A statement asks a dozen times and a context costs milliseconds, so what a query
    /// established is remembered for the statements that follow: an established set is answered by one context.
    /// </para>
    /// <para>
    /// <b>Not every question needs the engine.</b> A declaration with no conditional rule, asked about paths it says
    /// nothing of, cannot meet what the query established, and the answer is read off the two directly: see
    /// <see cref="JsonFactOverlay"/>. That is every filter over a container that declares no schema.
    /// </para>
    /// </remarks>
    public sealed class JsonFactTheory
    {

        /// <summary>
        /// The theory of a container that declares nothing, which proves nothing and costs nothing.
        /// </summary>
        public static readonly JsonFactTheory Empty = new(Array.Empty<JsonFactRule>());

        /// <summary>
        /// How many established sets a compiled theory remembers the answers to.
        /// </summary>
        const int Remembered = 256;

        /// <summary>
        /// How a query's context runs: it stops once it is contradictory, there being nothing more to know of a predicate
        /// that keeps no row.
        /// </summary>
        static readonly RunOptions QueryRun = new() { StopOnContradiction = true };

        readonly JsonFactRule[] _rules;
        readonly bool _conditional;
        readonly bool _representations;
        readonly JsonFactVocabulary _vocabulary = new();
        readonly List<Saturation.JsonFact> _stated = new();
        readonly List<JsonGuardedRule> _guarded = new();
        readonly List<JsonPathSpec> _paths = new();
        readonly List<JsonDocumentPath> _statedPaths = new();
        readonly bool _splits;
        readonly object _compiling = new();
        Compiled? _compiled;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rules">The compiled rules.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <c>null</c>.</exception>
        public JsonFactTheory(IEnumerable<JsonFactRule> rules) :
            this(rules, null)
        {

        }

        /// <summary>
        /// Initializes a new instance that reasons in the given generations.
        /// </summary>
        /// <param name="rules">The compiled rules.</param>
        /// <param name="generations">The generations to compile into, or <c>null</c> for <see cref="JsonFactGenerations.Default"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <c>null</c>.</exception>
        internal JsonFactTheory(IEnumerable<JsonFactRule> rules, JsonFactGenerations? generations)
        {
            if (rules is null)
                throw new ArgumentNullException(nameof(rules));

            _rules = new List<JsonFactRule>(rules).ToArray();
            Generations = generations ?? JsonFactGenerations.Default;

            var paths = new HashSet<JsonPathSpec>();
            var discriminators = new HashSet<JsonPathSpec>();

            foreach (var rule in _rules)
            {
                if (rule.Head.Claim is JsonClaim.Represents)
                    _representations = true;

                if (rule.Body.Count > 0)
                    _conditional = true;

                var heads = new List<Saturation.JsonFact>(JsonFactEncoding.Statement(rule.Head, _vocabulary, register: true));
                if (heads.Count == 0)
                    continue;

                foreach (var head in heads)
                    paths.Add(head.Path);

                if (rule.Body.Count == 0)
                {
                    _stated.AddRange(heads);
                    _statedPaths.Add(rule.Head.Path);
                    continue;
                }

                // A body the theory cannot read is no rule at all: dropping it loses what it states, and a rule read in
                // part would state it of documents its body excludes.
                var body = new List<Saturation.JsonFact>(rule.Body.Count);
                foreach (var atom in rule.Body)
                {
                    if (JsonFactEncoding.Condition(atom) is not Saturation.JsonFact condition)
                    {
                        body = null;
                        break;
                    }

                    body.Add(condition);
                }

                if (body is null)
                    continue;

                foreach (var condition in body)
                {
                    paths.Add(condition.Path);

                    // A path a body reads a value of is one a split can decide the body on.
                    if (condition is Saturation.JsonFact.OneOf or Saturation.JsonFact.NotEqual)
                        discriminators.Add(condition.Path);
                }

                _guarded.Add(new JsonGuardedRule(body, heads));
            }

            foreach (var discriminator in discriminators)
                _stated.Add(new Saturation.JsonFact.Discriminates(discriminator));

            _paths.AddRange(paths);
            _splits = discriminators.Count > 0;
        }

        /// <summary>
        /// Gets the generations this theory compiles into.
        /// </summary>
        internal JsonFactGenerations Generations { get; }

        /// <summary>
        /// Gets whether every rule holds outright, so that nothing a query proves can add to them.
        /// </summary>
        /// <remarks>
        /// The question a caller asks before going to the trouble of reading facts off a predicate.
        /// A container that declares no schema is in exactly this state — what the service guarantees
        /// is conditional on nothing — so the common path does no work at all.
        /// </remarks>
        public bool IsUnconditional => _conditional == false;

        /// <summary>
        /// Gets whether anything here states a stored form, as distinct from a type or a value.
        /// </summary>
        /// <remarks>
        /// Asked by the rewrite, which has nothing to do without one: the service's guarantees say
        /// what a property <em>is</em> and never how it is written.
        /// </remarks>
        public bool HasRepresentations => _representations;

        /// <summary>
        /// Gets the compiled rules.
        /// </summary>
        public IReadOnlyList<JsonFactRule> Rules => _rules;

        /// <summary>
        /// Gets whether the container declares nothing.
        /// </summary>
        public bool IsEmpty => _rules.Length == 0;

        /// <summary>
        /// Saturates a query's established facts beneath the container's declaration.
        /// </summary>
        /// <remarks>
        /// The facts a query establishes are read off its predicate — an equality pins a value and says the path is
        /// there, an <c>IN</c> a domain, an <c>IS NOT NULL</c> a presence. What comes back knows those and everything the
        /// rules of JSON, of JSON Schema and of the declaration prove from them beside what the declaration states.
        /// Asked with nothing, it is the declaration's own context, which every query's lies beneath.
        /// </remarks>
        /// <param name="established">What the query proved, which may be empty.</param>
        /// <returns>What is known.</returns>
        public JsonFactSet Derive(IEnumerable<JsonFact>? established)
        {
            var facts = Distinct(established);
            if (facts.Length == 0 && _rules.Length == 0)
                return JsonFactSet.Empty;

            var compiled = Compile();
            return facts.Length == 0 ? compiled.Outright : compiled.Derive(facts);
        }

        /// <summary>
        /// Saturates a query's established facts on the engine, whatever they are, remembering nothing: what
        /// <see cref="Derive"/> answers is held to it.
        /// </summary>
        /// <param name="established">What the query proved.</param>
        /// <param name="mentioned">Paths for the context to know of beside those it established.</param>
        /// <returns>What is known.</returns>
        internal JsonFactSet DeriveOnEngine(IEnumerable<JsonFact> established, IEnumerable<JsonDocumentPath> mentioned) =>
            Compile().Open(Distinct(established), mentioned);

        /// <summary>
        /// Returns this declaration compiled into the current generation, for a reasoner that asks more of it than
        /// <see cref="Derive"/> does: the predicate oracle, which puts contexts of its own beneath it.
        /// </summary>
        /// <returns>The compiled declaration, or <c>null</c> where the engine could not compile it, which states nothing.</returns>
        internal JsonFactDeclaration? Declaration()
        {
            var compiled = Compile();
            return compiled.Schema is Scope schema ? new JsonFactDeclaration(compiled.Generation, schema, _vocabulary) : null;
        }

        /// <summary>
        /// Determines whether an exception is one the engine raises for a context it could not answer for, which loses
        /// facts rather than failing a plan.
        /// </summary>
        internal static bool IsEngineFailure(Exception e) =>
            e is RuleEvaluationException or FactConflictException or ScopeContradictedException
                or InvalidOperationException or ObjectDisposedException;

        static JsonFact[] Distinct(IEnumerable<JsonFact>? established)
        {
            if (established is null)
                return Array.Empty<JsonFact>();

            var seen = new HashSet<JsonFact>();
            var list = new List<JsonFact>();
            foreach (var fact in established)
                if (seen.Add(fact))
                    list.Add(fact);

            return list.ToArray();
        }

        /// <summary>
        /// Returns this declaration compiled into the current generation, compiling it where it is not.
        /// </summary>
        Compiled Compile()
        {
            var generation = Generations.Current;
            var compiled = Volatile.Read(ref _compiled);
            if (compiled is not null && ReferenceEquals(compiled.Generation, generation))
                return compiled;

            lock (_compiling)
            {
                compiled = _compiled;
                if (compiled is null || ReferenceEquals(compiled.Generation, generation) == false)
                {
                    compiled = new Compiled(this, generation);
                    Volatile.Write(ref _compiled, compiled);
                }

                return compiled;
            }
        }

        /// <summary>
        /// The declaration in one generation: its frozen context, and the answers it has given.
        /// </summary>
        sealed class Compiled
        {

            readonly JsonFactTheory _theory;
            readonly Scope? _schema;
            readonly Dictionary<Key, LinkedListNode<KeyValuePair<Key, JsonFactSet>>> _memo = new();
            readonly LinkedList<KeyValuePair<Key, JsonFactSet>> _recent = new();

            public Compiled(JsonFactTheory theory, JsonFactGenerations.Generation generation)
            {
                _theory = theory;
                Generation = generation;

                var contexts = generation.Contexts;

                try
                {
                    var schema = contexts.OpenSchema(theory._stated, theory._guarded, theory._paths);
                    schema.Freeze();
                    _schema = schema;
                    Outright = new JsonFactSet(contexts.Rules, theory._vocabulary, schema, owned: false);
                }
                catch (Exception e) when (IsEngineFailure(e))
                {
                    // A declaration the engine cannot compile states nothing; every question is answered no.
                    Outright = JsonFactSet.Empty;
                }
            }

            public JsonFactGenerations.Generation Generation { get; }

            /// <summary>
            /// Gets the declaration's frozen context, or <c>null</c> where the engine could not compile it.
            /// </summary>
            public Scope? Schema => _schema;

            /// <summary>
            /// Gets what holds of every document, whatever a query proved.
            /// </summary>
            public JsonFactSet Outright { get; }

            public JsonFactSet Derive(JsonFact[] established)
            {
                if (_schema is null)
                    return Outright;

                // Where nothing the engine derives could change an answer, it is not asked.
                if (_theory._conditional == false
                    && JsonFactOverlay.TryOpen(established, _theory._statedPaths, Outright.Tables!) is JsonFactOverlay overlay)
                    return new JsonFactSet(overlay, _theory._vocabulary);

                var key = new Key(established);

                lock (_memo)
                {
                    if (_memo.TryGetValue(key, out var node))
                    {
                        _recent.Remove(node);
                        _recent.AddFirst(node);
                        return node.Value.Value;
                    }
                }

                var set = Open(established);

                lock (_memo)
                {
                    // Another thread may have answered the same set meanwhile; one answer is kept, so that a statement
                    // asking twice reads one context.
                    if (_memo.TryGetValue(key, out var raced))
                        return raced.Value.Value;

                    _memo[key] = _recent.AddFirst(new KeyValuePair<Key, JsonFactSet>(key, set));

                    while (_recent.Count > Remembered)
                    {
                        var last = _recent.Last!;
                        _recent.RemoveLast();
                        _memo.Remove(last.Value.Key);
                    }
                }

                return set;
            }

            /// <summary>
            /// Makes a query's context beneath the declaration's, holding what it established.
            /// </summary>
            /// <param name="established">What the query established.</param>
            /// <param name="mentioned">Paths for the context to know of beside those, which a test asks about.</param>
            public JsonFactSet Open(JsonFact[] established, IEnumerable<JsonDocumentPath>? mentioned = null)
            {
                if (_schema is null)
                    return Outright;

                var contexts = Generation.Contexts;
                var facts = new List<Saturation.JsonFact>();
                var paths = new HashSet<JsonPathSpec>();

                foreach (var path in mentioned ?? Array.Empty<JsonDocumentPath>())
                    paths.Add(JsonFactEncoding.Path(path));

                foreach (var fact in established)
                {
                    paths.Add(JsonFactEncoding.Path(fact.Path));
                    facts.AddRange(JsonFactEncoding.Statement(fact, _theory._vocabulary, register: false));
                }

                try
                {
                    var scope = contexts.Open(_schema, facts, paths, rules: null, split: _theory._splits, options: QueryRun);
                    return new JsonFactSet(contexts.Rules, _theory._vocabulary, scope, owned: true);
                }
                catch (Exception e) when (IsEngineFailure(e))
                {
                    // What holds of every document holds of the ones the query keeps.
                    return Outright;
                }
            }

        }

        /// <summary>
        /// An established set as a memo key: the facts, compared as a set.
        /// </summary>
        readonly struct Key : IEquatable<Key>
        {

            readonly JsonFact[] _facts;
            readonly int _hash;

            public Key(JsonFact[] facts)
            {
                _facts = facts;

                var hash = facts.Length;
                foreach (var fact in facts)
                    hash ^= fact.GetHashCode();

                _hash = hash;
            }

            public bool Equals(Key other)
            {
                if (_facts.Length != other._facts.Length || _hash != other._hash)
                    return false;

                foreach (var fact in _facts)
                    if (Array.IndexOf(other._facts, fact) < 0)
                        return false;

                return true;
            }

            public override bool Equals(object? obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => _hash;

        }

    }

    /// <summary>
    /// A declaration compiled into one generation: its frozen context, and the vocabulary its forms and extensions are
    /// written in.
    /// </summary>
    /// <param name="Generation">The generation, whose graph the context belongs to.</param>
    /// <param name="Context">The declaration's context, frozen.</param>
    /// <param name="Vocabulary">The tokens of the declaration's forms and extensions.</param>
    internal sealed record JsonFactDeclaration(JsonFactGenerations.Generation Generation, Scope Context, JsonFactVocabulary Vocabulary);

}
