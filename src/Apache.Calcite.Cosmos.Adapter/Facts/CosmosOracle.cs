using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The predicate oracle: a container's declaration, asked what a filter's conjuncts say of its documents and what its
    /// documents say of a predicate, each question answered from the facts its licence admits. Every fact the planner
    /// reads off a filter is read here, through <c>CosmosFilterStatement</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Eggnog decides what is true; the host decides what is written and what is legal.</b> The oracle loads a filter's
    /// conjuncts into the theory of JSON as terms (<see cref="PredicateRules"/>) and answers with values — a verdict, a
    /// fact about a path, whether anything is left — and never with a term. Whether a conjunct renders is asked of the
    /// translator, given a <see cref="JsonFactSet"/> of the facts it may use; which plan is the planner's.
    /// </para>
    /// <para>
    /// <b>One tree of contexts per statement</b>, memoised by the statement's digest:
    /// </para>
    /// <list type="bullet">
    /// <item>the declaration's frozen context, which is what holds of every document (L0);</item>
    /// <item>beneath it, one stratum per round of admission: the conjuncts that first rendered in the stratum above,
    /// assumed, and what they establish (L2, and the last of them L1);</item>
    /// <item>and beside them, children of the declaration holding every conjunct but one (L3), or the pins of a point
    /// read.</item>
    /// </list>
    /// <para>
    /// Each holds the statement's terms and their verdicts, and mentions, for a split, only what it assumes. A question
    /// about a predicate a context does not hold is answered in a child made for it, which assumes nothing and so splits
    /// on nothing: the context it was made beneath has lifted what a split there proves.
    /// </para>
    /// <para>
    /// <b>No conjunct is rendered from its own facts.</b> A conjunct is admitted in the first stratum that renders it, and
    /// its facts enter only the stratum below, so nothing it establishes is ever in the facts it was rendered from — to any
    /// depth, not only one. That is #117's shape made unrepresentable rather than guarded against.
    /// </para>
    /// <para>
    /// <b>A run that does not complete knows nothing it did not know before.</b> A context whose run stops on a limit, or
    /// that the engine cannot open, is discarded, and the stratum it would have been reads the one above it: whatever held
    /// there holds of the fewer documents below. A contradiction is no failure: it is the answer that nothing is kept.
    /// </para>
    /// <para>
    /// <b>In the planner's graphs.</b> <see cref="For"/> reasons in the generations the declaration compiles into, so a
    /// declaration compiles once whoever asks: the oracle, and the clauses that still read the declaration directly. A
    /// test makes an oracle in graphs of its own, so that no test reads another's memo.
    /// </para>
    /// <para>
    /// <b>Not every stratum needs the engine.</b> Over a declaration with no conditional rule, a stratum whose
    /// conjuncts touch nothing it states is read off <see cref="JsonFactOverlay"/>, as <see cref="JsonFactTheory.Derive"/>
    /// has always read such a query; the engine's context is opened only if a verdict is asked of it.
    /// </para>
    /// <para>
    /// How to ask it: load a filter into a <see cref="PredicateStatement"/> (<c>CosmosPredicateLoader</c> reads one off a
    /// <c>RexNode</c>, or build one from <see cref="SqlPredicate"/>s), then <see cref="Admit"/> it with a renderer — the
    /// translator's <c>TryTranslate</c> over the facts it is handed, which <c>CosmosPredicateLoader.Renderer</c> builds —
    /// and ask the <see cref="CosmosStatementScope"/> that comes back.
    /// </para>
    /// </remarks>
    internal sealed class CosmosOracle
    {

        /// <summary>
        /// The generations every oracle reasons in unless it is given others: graphs apart from the planner's.
        /// </summary>
        static readonly JsonFactGenerations Own = new();

        static readonly ConditionalWeakTable<JsonFactTheory, CosmosOracle> Oracles = new();

        /// <summary>
        /// How many statements' trees an oracle remembers.
        /// </summary>
        const int Remembered = 256;

        readonly JsonFactTheory _theory;
        readonly RunOptions? _options;
        readonly object _memoLock = new();
        readonly Dictionary<string, LinkedListNode<KeyValuePair<string, StatementTerms>>> _memo = new(StringComparer.Ordinal);
        readonly LinkedList<KeyValuePair<string, StatementTerms>> _recent = new();
        JsonFactGenerations.Generation? _generation;

        /// <summary>
        /// Returns the oracle of a declaration, the same one each time it is asked of the same theory, reasoning in the
        /// generations the theory compiles into.
        /// </summary>
        /// <param name="theory">The declaration: a container's <c>Facts</c>.</param>
        /// <returns>The oracle.</returns>
        public static CosmosOracle For(JsonFactTheory theory) =>
            Oracles.GetValue(theory ?? throw new ArgumentNullException(nameof(theory)), t => new CosmosOracle(t, t.Generations));

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="theory">The declaration.</param>
        /// <param name="generations">The generations to reason in, or <c>null</c> for the oracle's own.</param>
        /// <param name="options">The limits a context's run is held to, or <c>null</c> for the engine's, stopping on a contradiction.</param>
        internal CosmosOracle(JsonFactTheory theory, JsonFactGenerations? generations, RunOptions? options = null)
        {
            _theory = (generations ?? Own).Bind(theory ?? throw new ArgumentNullException(nameof(theory)));
            _options = options;
        }

        /// <summary>
        /// Gets the theory the oracle reasons with, bound to its generations.
        /// </summary>
        internal JsonFactTheory Theory => _theory;

        /// <summary>
        /// Gets what the declaration states of every document (L0): what routing reads, and what a consumer that names no
        /// statement may lean on. It holds whatever a query keeps, so no licence is needed to read it.
        /// </summary>
        public JsonFactSet Declared => _theory.Derive(null);

        /// <summary>
        /// Gets or sets, for a checker, the stratum a conjunct is handed to the renderer from in a round, given the round's:
        /// the round's own where it is <c>null</c>, which is every oracle but one a test plants a fault in to be caught.
        /// </summary>
        internal Func<StatementTerms, StatementTerms.Node, SqlPredicate, StatementTerms.Node>? RenderedFrom { get; init; }

        /// <summary>
        /// Admits a statement's conjuncts stratum by stratum, and returns the scope its questions are asked of.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each round asks the renderer, of every conjunct not yet admitted, whether it renders given the facts of the
        /// stratum so far; those that do are admitted together, and their facts enter a new stratum beneath. The rounds
        /// end when one admits nothing. A conjunct never admitted is residual.
        /// </para>
        /// <para>
        /// With no renderer nothing is admitted: every question about the rows a filter kept is answered from the
        /// declaration.
        /// </para>
        /// </remarks>
        /// <param name="statement">The statement.</param>
        /// <param name="renders">Whether conjunct <c>i</c> renders given these facts; <c>null</c> to admit nothing.</param>
        /// <returns>The statement's scope.</returns>
        public CosmosStatementScope Admit(PredicateStatement statement, Func<int, JsonFactSet, bool>? renders = null)
        {
            if (statement is null)
                throw new ArgumentNullException(nameof(statement));

            return new CosmosStatementScope(Terms(statement), statement, renders, RenderedFrom);
        }

        /// <summary>
        /// Returns the tree of contexts for a statement, remembered by its digest in the current generation.
        /// </summary>
        StatementTerms Terms(PredicateStatement statement)
        {
            JsonFactDeclaration? declaration;
            try
            {
                declaration = _theory.Declaration();
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                declaration = null;
            }

            if (declaration is null)
                return StatementTerms.Unknowing(statement);

            lock (_memoLock)
            {
                if (ReferenceEquals(_generation, declaration.Generation) == false)
                {
                    _memo.Clear();
                    _recent.Clear();
                    _generation = declaration.Generation;
                }

                if (_memo.TryGetValue(statement.Digest, out var hit))
                {
                    _recent.Remove(hit);
                    _recent.AddFirst(hit);
                    return hit.Value.Value;
                }
            }

            var terms = new StatementTerms(declaration, statement, _options);

            lock (_memoLock)
            {
                // Another thread may have opened the same statement meanwhile; one tree is kept, so that the questions of
                // a plan read one set of contexts.
                if (ReferenceEquals(_generation, declaration.Generation) && _memo.TryGetValue(statement.Digest, out var raced))
                    return raced.Value.Value;

                if (ReferenceEquals(_generation, declaration.Generation))
                {
                    _memo[statement.Digest] = _recent.AddFirst(new KeyValuePair<string, StatementTerms>(statement.Digest, terms));
                    while (_recent.Count > Remembered)
                    {
                        var last = _recent.Last!;
                        _recent.RemoveLast();
                        _memo.Remove(last.Value.Key);
                    }
                }
            }

            return terms;
        }

    }

}
