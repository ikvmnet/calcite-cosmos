using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <b>Three layers above it, and a statement's beneath.</b> The rules of JSON are the graph's own, frozen in its root;
    /// the rules of JSON Schema are local to a frozen child of the root; and this declaration is a frozen context beneath
    /// that, holding its unconditional facts as facts and its conditional ones as rows a fixed interpreter reads. What a
    /// filter establishes is the predicate oracle's to read: its strata are contexts beneath this one
    /// (<see cref="Declaration"/>), and <see cref="Declared"/> is this one read as a fact set.
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
    /// one that replaced it.
    /// </para>
    /// <para>
    /// <b>Bound to a schema's generations, or to none.</b> A theory made from rules alone is unbound: its rules and their
    /// encoding, a value a host may keep anywhere, which compiles nothing. Its container's schema binds it
    /// (<see cref="In"/>), and only the bound copy holds a compiled context and an oracle, so what a question teaches lives
    /// as long as the schema and no longer. Asked to reason unbound, it throws <see cref="ArgumentException"/>, which no
    /// caller reads as an engine's failure: a missed binding fails loudly, never as "unknown".
    /// </para>
    /// </remarks>
    public sealed class JsonFactTheory
    {

        /// <summary>
        /// The theory of a container that declares nothing, which proves nothing and costs nothing: unbound, a constant.
        /// </summary>
        public static readonly JsonFactTheory Empty = new(Array.Empty<JsonFactRule>());

        /// <summary>
        /// How a path the declaration's context is first asked about is told to it: stopping on a contradiction, which
        /// says only that the declaration admits nothing.
        /// </summary>
        static readonly RunOptions Told = new() { StopOnContradiction = true };

        readonly JsonFactRule[] _rules;
        readonly JsonFactVocabulary _vocabulary = new();
        readonly List<Saturation.JsonFact> _stated = new();
        readonly List<JsonGuardedRule> _guarded = new();
        readonly List<JsonPathSpec> _paths = new();
        readonly HashSet<JsonDocumentPath> _mentionedPaths = new();
        readonly object _compiling = new();
        Compiled? _compiled;
        CosmosOracle? _oracle;

        /// <summary>
        /// Initializes a new instance, bound to no schema's generations until its container is.
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
        /// <param name="generations">The generations to compile into, or <c>null</c> for none, which compiles nothing.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rules"/> is <c>null</c>.</exception>
        internal JsonFactTheory(IEnumerable<JsonFactRule> rules, JsonFactGenerations? generations)
        {
            if (rules is null)
                throw new ArgumentNullException(nameof(rules));

            _rules = new List<JsonFactRule>(rules).ToArray();
            Generations = generations;

            var paths = new HashSet<JsonPathSpec>();
            var discriminators = new HashSet<JsonPathSpec>();

            foreach (var rule in _rules)
            {
                var heads = new List<Saturation.JsonFact>(JsonFactEncoding.Statement(rule.Head, _vocabulary, register: true));
                if (heads.Count == 0)
                    continue;

                _mentionedPaths.Add(rule.Head.Path);
                foreach (var atom in rule.Body)
                    _mentionedPaths.Add(atom.Path);

                foreach (var head in heads)
                    paths.Add(head.Path);

                if (rule.Body.Count == 0)
                {
                    _stated.AddRange(heads);
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

            // What each form a rule names is written in, as the form states it: data about the form, which the theory
            // reads into what a path holding it holds.
            foreach (var (form, token) in _vocabulary.Forms)
                _stated.Add(new Saturation.JsonFact.FormCharacters(token, JsonFactEncoding.Members(form.Characters)));

            _paths.AddRange(paths);
        }

        /// <summary>
        /// Gets the generations this theory compiles into, or <c>null</c> where it is bound to none and compiles nothing.
        /// </summary>
        internal JsonFactGenerations? Generations { get; }

        /// <summary>
        /// Returns a theory stating what this one states, reasoning in the given generations: this one where it already
        /// does, a copy otherwise. A copy remembers nothing of this one, and nothing here remembers it, so no unbound
        /// theory — a static's included — reaches a schema's graphs.
        /// </summary>
        /// <param name="generations">The generations.</param>
        /// <returns>The theory, bound there.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="generations"/> is <c>null</c>.</exception>
        internal JsonFactTheory In(JsonFactGenerations generations)
        {
            if (generations is null)
                throw new ArgumentNullException(nameof(generations));

            return ReferenceEquals(Generations, generations) ? this : new JsonFactTheory(_rules, generations);
        }

        /// <summary>
        /// Gets the declaration's oracle, made when first asked and the same one after: on the theory, so that it lives as
        /// long as the schema that bound it.
        /// </summary>
        /// <exception cref="ArgumentException">The theory is bound to no schema's generations.</exception>
        internal CosmosOracle Oracle
        {
            get
            {
                if (Volatile.Read(ref _oracle) is CosmosOracle oracle)
                    return oracle;

                // A losing thread's oracle has asked nothing, so dropping it loses nothing.
                var made = new CosmosOracle(this);
                return Interlocked.CompareExchange(ref _oracle, made, null) ?? made;
            }
        }

        /// <summary>
        /// Gets the compiled rules.
        /// </summary>
        public IReadOnlyList<JsonFactRule> Rules => _rules;

        /// <summary>
        /// Gets every path a rule states something of or is conditional on: where nothing a filter reads lies within or
        /// above one of them, the declaration says nothing of what it reads.
        /// </summary>
        internal IReadOnlyCollection<JsonDocumentPath> MentionedPaths => _mentionedPaths;

        /// <summary>
        /// Gets whether a rule the theory reads is conditional, so that what it states of one path depends on another.
        /// </summary>
        internal bool IsConditional => _guarded.Count > 0;

        /// <summary>
        /// Gets whether the container declares nothing.
        /// </summary>
        public bool IsEmpty => _rules.Length == 0;

        /// <summary>
        /// Gets what the declaration states of every document: its own context, which every statement's lies beneath.
        /// </summary>
        /// <exception cref="ArgumentException">The theory states something and is bound to no schema's generations.</exception>
        internal JsonFactSet Declared => _rules.Length == 0 ? JsonFactSet.Empty : Compile().Outright;

        /// <summary>
        /// Returns this declaration compiled into the current generation, for the predicate oracle, which puts contexts of
        /// its own beneath it: what a filter establishes is a stratum of its statement, never a context made here.
        /// </summary>
        /// <returns>The compiled declaration, or <c>null</c> where the engine could not compile it, which states nothing.</returns>
        /// <exception cref="ArgumentException">The theory is bound to no schema's generations.</exception>
        internal JsonFactDeclaration? Declaration()
        {
            var compiled = Compile();
            return compiled.Schema is Scope schema ? new JsonFactDeclaration(compiled.Generation, schema, _vocabulary, compiled.Outright, compiled.DeclaredPossible) : null;
        }

        /// <summary>
        /// Determines whether an exception is one the engine raises for a context it could not answer for, which loses
        /// facts rather than failing a plan.
        /// </summary>
        /// <remarks>
        /// Running out of a graph's ids or epochs is one (<see cref="OverflowException"/>; Eggnog's architecture §2.12): it
        /// leaves the graph's <c>FractionUsed</c> at 1, so the next question retires the generation.
        /// </remarks>
        internal static bool IsEngineFailure(Exception e) =>
            e is RuleEvaluationException or FactConflictException or ScopeContradictedException
                or InvalidOperationException or ObjectDisposedException or OverflowException;

        /// <summary>
        /// The failure of a theory asked to reason in no schema's generations: an <see cref="ArgumentException"/>, which
        /// <see cref="IsEngineFailure"/> does not admit, so that a container the schema never bound fails its plan rather
        /// than planning as if its declaration knew nothing.
        /// </summary>
        internal static ArgumentException Unbound() =>
            new("The theory is bound to no schema's reasoning, and so compiles nothing: bind its container to its schema's before planning against it.");

        /// <summary>
        /// Returns this declaration compiled into the current generation, compiling it where it is not.
        /// </summary>
        /// <exception cref="ArgumentException">The theory is bound to no schema's generations.</exception>
        Compiled Compile()
        {
            var generation = (Generations ?? throw Unbound()).Current;
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
        /// The declaration in one generation: its frozen context, and <c>D0</c> beneath it once the first statement asks.
        /// </summary>
        sealed class Compiled
        {

            readonly Scope? _schema;

            public Compiled(JsonFactTheory theory, JsonFactGenerations.Generation generation)
            {
                Generation = generation;

                var contexts = generation.Contexts;

                try
                {
                    var schema = contexts.OpenSchema(theory._stated, theory._guarded, theory._paths);
                    schema.Freeze();
                    _schema = schema;

                    // A path the declaration says nothing of is told to it when first asked, beneath its frozen context,
                    // and the child is kept as long as the context is: what the rules derive of it holds of every
                    // document, as what the context holds does. Without a split, as a query's child: the declaration's own
                    // run lifted what its cases prove of the paths it states.
                    Outright = new JsonFactSet(
                        new JsonScopeTables(contexts.Rules, schema, owned: false, mention: path => contexts.Mentioning(schema, JsonFactEncoding.Path(path), contexts.Saturation, Told)),
                        theory._vocabulary);
                }
                catch (Exception e) when (IsEngineFailure(e))
                {
                    // A declaration the engine cannot compile states nothing; every question is answered no.
                    Outright = JsonFactSet.Empty;
                }

                var d0 = new Lazy<Scope?>(() => OpenDeclaredPossible(theory, contexts), LazyThreadSafetyMode.ExecutionAndPublication);
                DeclaredPossible = () => d0.Value;
            }

            /// <summary>
            /// Opens <c>D0</c> beneath the declaration's context, or returns <c>null</c> where it would state nothing or its
            /// run fails or stops short, and the root stays the context.
            /// </summary>
            Scope? OpenDeclaredPossible(JsonFactTheory theory, JsonContexts contexts)
            {
                if (_schema is not Scope schema)
                    return null;

                // Only paths the context holds a term of: a term D0 made would enter every stratum beneath it.
                var rules = PredicateRules.Of(contexts);
                var declared = theory._mentionedPaths.Select(path => JsonFactEncoding.Path(path).ToTerm(rules.Json))
                    .Where(term => schema.TryGet(term, out _)).Select(term => rules.Declared.Of(term)).ToArray();
                if (declared.Length == 0)
                    return null;

                Scope? d0 = null;
                try
                {
                    d0 = schema.CreateChild();
                    rules.DefineDeclaredPossible(d0);
                    d0.Assume(declared);
                    if (d0.Run(Schedule.Saturate(rules.Decide)).StopReason == RunStopReason.Completed)
                    {
                        d0.Freeze();
                        return d0;
                    }
                }
                catch (Exception e) when (IsEngineFailure(e))
                {
                    // Released below, as a run that stops short is, so the Lazy keeps no exception.
                }

                d0?.Dispose();
                return null;
            }

            public JsonFactGenerations.Generation Generation { get; }

            /// <summary>Gets <c>D0</c>, opened when first asked for in this generation.</summary>
            public Func<Scope?> DeclaredPossible { get; }

            /// <summary>
            /// Gets the declaration's frozen context, or <c>null</c> where the engine could not compile it.
            /// </summary>
            public Scope? Schema => _schema;

            /// <summary>
            /// Gets what holds of every document, whatever a query proved.
            /// </summary>
            public JsonFactSet Outright { get; }

        }

    }

}
