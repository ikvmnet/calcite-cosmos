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
    /// </remarks>
    public sealed class JsonFactTheory
    {

        /// <summary>
        /// The theory of a container that declares nothing, which proves nothing and costs nothing.
        /// </summary>
        public static readonly JsonFactTheory Empty = new(Array.Empty<JsonFactRule>());



        readonly JsonFactRule[] _rules;
        readonly bool _conditional;
        readonly JsonFactVocabulary _vocabulary = new();
        readonly List<Saturation.JsonFact> _stated = new();
        readonly List<JsonGuardedRule> _guarded = new();
        readonly List<JsonPathSpec> _paths = new();
        readonly List<JsonDocumentPath> _statedPaths = new();
        readonly HashSet<JsonDocumentPath> _mentionedPaths = new();
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
                if (rule.Body.Count > 0)
                    _conditional = true;

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

            // What each form a rule names is written in, as the form states it: data about the form, which the theory
            // reads into what a path holding it holds.
            foreach (var (form, token) in _vocabulary.Forms)
                _stated.Add(new Saturation.JsonFact.FormCharacters(token, JsonFactEncoding.Members(form.Characters)));

            _paths.AddRange(paths);
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
        /// Gets the compiled rules.
        /// </summary>
        public IReadOnlyList<JsonFactRule> Rules => _rules;

        /// <summary>
        /// Gets every path a rule states something of or is conditional on: where nothing a filter reads lies within or
        /// above one of them, the declaration says nothing of what it reads.
        /// </summary>
        internal IReadOnlyCollection<JsonDocumentPath> MentionedPaths => _mentionedPaths;

        /// <summary>
        /// Gets whether the container declares nothing.
        /// </summary>
        public bool IsEmpty => _rules.Length == 0;

        /// <summary>
        /// Gets what the declaration states of every document: its own context, which every statement's lies beneath.
        /// </summary>
        internal JsonFactSet Declared => _rules.Length == 0 ? JsonFactSet.Empty : Compile().Outright;

        /// <summary>
        /// Returns this declaration compiled into the current generation, for the predicate oracle, which puts contexts of
        /// its own beneath it: what a filter establishes is a stratum of its statement, never a context made here.
        /// </summary>
        /// <returns>The compiled declaration, or <c>null</c> where the engine could not compile it, which states nothing.</returns>
        internal JsonFactDeclaration? Declaration()
        {
            var compiled = Compile();
            return compiled.Schema is Scope schema && compiled.Outright.Tables is JsonFactTables outright
                ? new JsonFactDeclaration(compiled.Generation, schema, _vocabulary, _conditional == false ? new JsonFactUnconditional(_statedPaths, outright) : null)
                : null;
        }

        /// <summary>
        /// Determines whether an exception is one the engine raises for a context it could not answer for, which loses
        /// facts rather than failing a plan.
        /// </summary>
        internal static bool IsEngineFailure(Exception e) =>
            e is RuleEvaluationException or FactConflictException or ScopeContradictedException
                or InvalidOperationException or ObjectDisposedException;

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
        /// The declaration in one generation: its frozen context.
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

        }

    }

}
