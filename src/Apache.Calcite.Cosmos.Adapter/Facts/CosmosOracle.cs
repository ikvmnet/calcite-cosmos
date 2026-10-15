using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What a question about a filter is for, which decides the facts it may be answered from.
    /// </summary>
    internal enum OracleQuestion
    {

        /// <summary>Whether a conjunct renders, and how: the stratum before the one that admitted it (L2).</summary>
        RenderConjunct,

        /// <summary>Whether a conjunct can be deleted: every sibling, never itself (L3).</summary>
        DeleteConjunct,

        /// <summary>Whether a conjunct says nothing its siblings do not: every sibling, never itself (L3).</summary>
        Redundant,

        /// <summary>What a point read may rely on, which applies no predicate: the pins alone.</summary>
        PointRead,

        /// <summary>A projection: the rows the pushed filter kept (L1).</summary>
        Select,

        /// <summary>A sort: the rows the pushed filter kept (L1).</summary>
        OrderBy,

        /// <summary>A grouping: the rows the pushed filter kept (L1).</summary>
        GroupBy,

        /// <summary>A residual filter, evaluated in process: what holds of every document (L0).</summary>
        Residual,

        /// <summary>Routing to partitions: what holds of every document (L0).</summary>
        Route,

    }

    /// <summary>
    /// A question's licence: what it is for, and the conjunct or the pins it is about. The oracle chooses the facts a
    /// question may be answered from by this, and nothing else, so no caller chooses facts.
    /// </summary>
    /// <param name="Question">What the question is for.</param>
    /// <param name="Conjunct">The conjunct a render, deletion or redundancy question is about, by its index in the statement.</param>
    /// <param name="Pins">The paths a point read is keyed on.</param>
    internal readonly record struct OracleLicence(OracleQuestion Question, int Conjunct, IReadOnlyList<JsonDocumentPath>? Pins)
    {

        /// <summary>Whether a conjunct renders: from the stratum before the one that admitted it.</summary>
        public static OracleLicence RenderConjunct(int conjunct) => new(OracleQuestion.RenderConjunct, conjunct, null);

        /// <summary>Whether a conjunct can be deleted: from every sibling, never from itself.</summary>
        public static OracleLicence DeleteConjunct(int conjunct) => new(OracleQuestion.DeleteConjunct, conjunct, null);

        /// <summary>Whether a conjunct is redundant: from every sibling, never from itself.</summary>
        public static OracleLicence Redundant(int conjunct) => new(OracleQuestion.Redundant, conjunct, null);

        /// <summary>What a point read keyed on these paths may rely on: the conjuncts that pin them, and no other.</summary>
        public static OracleLicence PointRead(IReadOnlyList<JsonDocumentPath> pins) => new(OracleQuestion.PointRead, -1, pins ?? throw new ArgumentNullException(nameof(pins)));

        /// <summary>A projection, over the rows the pushed filter kept.</summary>
        public static OracleLicence Select { get; } = new(OracleQuestion.Select, -1, null);

        /// <summary>A sort, over the rows the pushed filter kept.</summary>
        public static OracleLicence OrderBy { get; } = new(OracleQuestion.OrderBy, -1, null);

        /// <summary>A grouping, over the rows the pushed filter kept.</summary>
        public static OracleLicence GroupBy { get; } = new(OracleQuestion.GroupBy, -1, null);

        /// <summary>A residual filter: what holds of every document.</summary>
        public static OracleLicence Residual { get; } = new(OracleQuestion.Residual, -1, null);

        /// <summary>Routing: what holds of every document.</summary>
        public static OracleLicence Route { get; } = new(OracleQuestion.Route, -1, null);

    }

    /// <summary>
    /// What the facts a licence admits say of a predicate.
    /// </summary>
    /// <param name="Contradictory">The facts admit no document at all, so the predicate keeps nothing and anything may be said of it.</param>
    /// <param name="AlwaysTrue">The predicate is TRUE of every document.</param>
    /// <param name="AlwaysFalse">The predicate is FALSE, never UNKNOWN, of every document: its negation keeps them all.</param>
    /// <param name="NeverTrue">The predicate is FALSE or UNKNOWN of every document: it keeps none.</param>
    internal readonly record struct OracleVerdict(bool Contradictory, bool AlwaysTrue, bool AlwaysFalse, bool NeverTrue)
    {

        /// <summary>The verdict where nothing is known.</summary>
        public static OracleVerdict Unknown { get; } = default;

        /// <summary>The verdict of facts no document satisfies.</summary>
        public static OracleVerdict Contradicted { get; } = new(true, false, false, true);

        /// <summary>
        /// Gets the verdict as a planner reads it: a predicate that keeps every row, one that keeps none, or neither.
        /// </summary>
        public Truth Truth => Contradictory || NeverTrue ? Truth.Never : AlwaysTrue ? Truth.Always : Truth.Unknown;

    }

    /// <summary>
    /// The predicate oracle: a container's declaration, asked what a filter's conjuncts say of its documents and what its
    /// documents say of a predicate, each question answered from the facts its licence admits. In shadow: nothing the
    /// planner runs calls it.
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
    /// <b>Its own graphs, by default.</b> A theory is bound to the oracle's own generations, apart from those the planner
    /// reasons in, so that asking the oracle defines nothing in a graph a plan reads. A test may share the planner's.
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
        /// Returns the oracle of a declaration, the same one each time it is asked of the same theory.
        /// </summary>
        /// <param name="theory">The declaration: a container's <c>Facts</c>.</param>
        /// <returns>The oracle.</returns>
        public static CosmosOracle For(JsonFactTheory theory) =>
            Oracles.GetValue(theory ?? throw new ArgumentNullException(nameof(theory)), t => new CosmosOracle(t, null));

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

    /// <summary>
    /// One statement under one renderer: which conjuncts each stratum admitted, and the questions the licence table
    /// answers from them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The licence table</b>, argued once. Facts from siblings or from the declaration may justify any rewrite; a
    /// conjunct's own facts may justify only a strengthening, and the oracle never offers them; a deletion or a
    /// weakening needs facts from outside the conjunct; and a consumer that is not a filter needs facts that hold of
    /// every row it touches.
    /// </para>
    /// <list type="table">
    /// <item><term><see cref="OracleQuestion.RenderConjunct"/></term><description>the stratum before the one that admitted
    /// the conjunct; the last stratum for one never admitted, whose facts are its siblings' and never its own</description></item>
    /// <item><term><see cref="OracleQuestion.DeleteConjunct"/>, <see cref="OracleQuestion.Redundant"/></term><description>every
    /// conjunct but this one, admitted or not: the rows the rest of the filter keeps</description></item>
    /// <item><term><see cref="OracleQuestion.PointRead"/></term><description>the conjuncts that pin the read's keys, and no
    /// other: a point read applies no predicate</description></item>
    /// <item><term><see cref="OracleQuestion.Select"/>, <see cref="OracleQuestion.OrderBy"/>,
    /// <see cref="OracleQuestion.GroupBy"/></term><description>the last stratum: every admitted conjunct, applied before
    /// the clause reads a row</description></item>
    /// <item><term><see cref="OracleQuestion.Residual"/>, <see cref="OracleQuestion.Route"/></term><description>the
    /// declaration: what holds of every document</description></item>
    /// </list>
    /// <para>
    /// <b>Thread-safe.</b> The contexts are frozen and read freely; a context made for a question is made under the
    /// statement's lock.
    /// </para>
    /// </remarks>
    internal sealed class CosmosStatementScope
    {

        readonly StatementTerms _terms;
        readonly int[] _round;
        readonly StatementTerms.Node?[] _before;
        readonly StatementTerms.Node _final;
        readonly List<StatementTerms.Node> _chain = new();

        internal CosmosStatementScope(StatementTerms terms, PredicateStatement statement, Func<int, JsonFactSet, bool>? renders, Func<StatementTerms, StatementTerms.Node, SqlPredicate, StatementTerms.Node>? renderedFrom = null)
        {
            _terms = terms;
            Statement = statement;

            var count = statement.Conjuncts.Count;
            _round = new int[count];
            _before = new StatementTerms.Node?[count];

            var strata = new List<IReadOnlyList<int>>();
            var node = terms.Root;
            _chain.Add(node);

            if (renders is not null)
            {
                for (var round = 1; node.Contradictory == false; round++)
                {
                    var added = new List<int>();
                    for (var i = 0; i < count; i++)
                        if (_round[i] == 0 && renders(i, (renderedFrom?.Invoke(terms, node, statement.Conjuncts[i]) ?? node).View))
                            added.Add(i);

                    if (added.Count == 0)
                        break;

                    // Rendered from this stratum, every one of them; what they establish enters only the next.
                    foreach (var i in added)
                    {
                        _round[i] = round;
                        _before[i] = node;
                    }

                    strata.Add(added);
                    node = terms.Child(node, added.Select(i => statement.Conjuncts[i]));
                    _chain.Add(node);

                    // A stratum that knows nothing more than the one above it can admit nothing new.
                    if (node.Complete == false)
                        break;
                }
            }

            _final = node;
            Strata = strata;
            Residual = Enumerable.Range(0, count).Where(i => _round[i] == 0).ToArray();
        }

        /// <summary>
        /// Gets the statement.
        /// </summary>
        public PredicateStatement Statement { get; }

        /// <summary>
        /// Gets the conjuncts each round admitted, by index, in round order.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<int>> Strata { get; }

        /// <summary>
        /// Gets the conjuncts no round admitted.
        /// </summary>
        public IReadOnlyList<int> Residual { get; }

        /// <summary>
        /// Gets whether every context the admission opened ran to completion or to a contradiction.
        /// </summary>
        public bool Complete => _chain.All(node => node.Complete);

        /// <summary>
        /// Gets the reports of the runs that opened each stratum, for measurement: a gate reads their stop reasons.
        /// </summary>
        public IReadOnlyList<RunReport> Reports => _chain.Select(node => node.Report).OfType<RunReport>().ToArray();

        /// <summary>
        /// Returns the round that admitted a conjunct, counting from one, or zero where none did.
        /// </summary>
        public int StratumOf(int conjunct) => _round[Check(conjunct)];

        /// <summary>
        /// Returns the conjuncts whose facts a licence's scope holds, by index.
        /// </summary>
        /// <remarks>
        /// The licence table read structurally, for a checker: a render question's conjunct is never among them.
        /// </remarks>
        public IReadOnlyList<int> AssumedIn(OracleLicence licence)
        {
            var all = Enumerable.Range(0, Statement.Conjuncts.Count);
            return licence.Question switch
            {
                OracleQuestion.RenderConjunct when _round[Check(licence.Conjunct)] > 0 =>
                    all.Where(i => _round[i] > 0 && _round[i] < _round[licence.Conjunct]).ToArray(),
                OracleQuestion.RenderConjunct or OracleQuestion.Select or OracleQuestion.OrderBy or OracleQuestion.GroupBy =>
                    all.Where(i => _round[i] > 0 && _chain.Count > _round[i] && _chain[_round[i]].Complete).ToArray(),
                OracleQuestion.DeleteConjunct or OracleQuestion.Redundant =>
                    all.Where(i => i != Check(licence.Conjunct)).ToArray(),
                OracleQuestion.PointRead =>
                    all.Where(i => Pins(Statement.Conjuncts[i], licence.Pins!)).ToArray(),
                _ => Array.Empty<int>(),
            };
        }

        /// <summary>
        /// Returns what the facts a licence admits say of a predicate.
        /// </summary>
        /// <param name="predicate">The predicate: a conjunct, a part of one, or one the statement does not hold.</param>
        /// <param name="licence">What the question is for.</param>
        /// <returns>The verdicts.</returns>
        public OracleVerdict Judge(SqlPredicate predicate, OracleLicence licence) =>
            _terms.Judge(ScopeFor(licence), predicate ?? throw new ArgumentNullException(nameof(predicate)));

        /// <summary>
        /// Decides a predicate from the facts a licence admits: whether it keeps every row, none, or cannot be told.
        /// </summary>
        public Truth Decide(SqlPredicate predicate, OracleLicence licence) => Judge(predicate, licence).Truth;

        /// <summary>
        /// Decides one of the statement's conjuncts from the facts a licence admits.
        /// </summary>
        public Truth Decide(int conjunct, OracleLicence licence) => Decide(Statement.Conjuncts[Check(conjunct)], licence);

        /// <summary>
        /// Returns the facts a licence admits, to be asked as a planner asks any fact set: what the translator is handed.
        /// </summary>
        public JsonFactSet Facts(OracleLicence licence) => ScopeFor(licence).View;

        /// <summary>
        /// Determines whether a fact holds in the facts a licence admits.
        /// </summary>
        public bool Knows(JsonFact fact, OracleLicence licence) => Facts(licence).Knows(fact);

        /// <summary>
        /// Returns what the facts a licence admits say of a path, as claims.
        /// </summary>
        public IReadOnlyList<JsonClaim> FactsFor(JsonDocumentPath path, OracleLicence licence) => Facts(licence).ClaimsFor(path);

        /// <summary>
        /// Determines whether the facts a licence admits admit no document: whatever the question, nothing is kept.
        /// </summary>
        public bool IsContradictory(OracleLicence licence) => ScopeFor(licence).Contradictory;

        /// <summary>
        /// Returns the context a licence's questions are read from, and the rules it ran, for a checker that reads every row
        /// of it rather than asking: the context is <c>null</c> where nothing is known, or nothing is kept.
        /// </summary>
        internal (Scope? Context, PredicateRules? Rules) ContextFor(OracleLicence licence) => (ScopeFor(licence).Scope, _terms.Rules);

        /// <summary>
        /// Decides a predicate given others assumed beside the declaration: "if these hold, does this?"
        /// </summary>
        /// <remarks>
        /// The question redundancy asks of anything, conjunct or candidate: a predicate the given ones make always true
        /// adds nothing to them. The given predicates are the caller's, and the licence is its: a conjunct's own facts are
        /// among them only if the caller put the conjunct there.
        /// </remarks>
        public Truth DecideGiven(SqlPredicate predicate, IEnumerable<SqlPredicate> given) =>
            _terms.Judge(_terms.Child(_terms.Root, given ?? throw new ArgumentNullException(nameof(given))), predicate).Truth;

        /// <summary>
        /// Determines whether a predicate adds nothing to others: whether, where they hold, it always does.
        /// </summary>
        public bool IsRedundant(SqlPredicate predicate, IEnumerable<SqlPredicate> given) => DecideGiven(predicate, given) == Truth.Always;

        /// <summary>
        /// Returns the conjuncts that may be deleted, removing one at a time.
        /// </summary>
        /// <remarks>
        /// In digest order, ties by index, each asked against the conjuncts still kept: so two duplicates do not delete
        /// each other, the second being asked once the first is gone.
        /// </remarks>
        public IReadOnlyList<int> RedundantConjuncts()
        {
            var conjuncts = Statement.Conjuncts;
            var kept = Enumerable.Range(0, conjuncts.Count).ToList();
            var removed = new List<int>();

            foreach (var i in Enumerable.Range(0, conjuncts.Count).OrderBy(i => conjuncts[i].ToString(), StringComparer.Ordinal).ThenBy(i => i))
            {
                var siblings = kept.Where(j => j != i).Select(j => conjuncts[j]);
                if (_terms.Judge(_terms.Child(_terms.Root, siblings), conjuncts[i]).Truth == Truth.Always)
                {
                    kept.Remove(i);
                    removed.Add(i);
                }
            }

            return removed;
        }

        /// <summary>
        /// The licence table: the scope a question's licence admits facts from.
        /// </summary>
        StatementTerms.Node ScopeFor(OracleLicence licence)
        {
            switch (licence.Question)
            {
                case OracleQuestion.RenderConjunct:
                    return _before[Check(licence.Conjunct)] ?? _final;

                case OracleQuestion.DeleteConjunct or OracleQuestion.Redundant:
                {
                    var conjunct = Check(licence.Conjunct);
                    return _terms.Child(_terms.Root, Statement.Conjuncts.Where((_, i) => i != conjunct));
                }

                case OracleQuestion.PointRead:
                    return _terms.Child(_terms.Root, Statement.Conjuncts.Where(c => Pins(c, licence.Pins!)));

                case OracleQuestion.Select or OracleQuestion.OrderBy or OracleQuestion.GroupBy:
                    return _final;

                default:
                    return _terms.Root;
            }
        }

        /// <summary>
        /// Determines whether a conjunct pins one of a point read's keys: an equality of what the path holds, or reads as,
        /// with one literal.
        /// </summary>
        static bool Pins(SqlPredicate conjunct, IReadOnlyList<JsonDocumentPath> pins) => conjunct switch
        {
            SqlPredicate.Compare { Op: SqlPredicate.Compare.Equal, Left: var value, Right: SqlValue.Literal } =>
                value is not SqlValue.Cast && value.PathRead is JsonDocumentPath p && pins.Contains(p),
            SqlPredicate.In { Value: var value, Points.Count: 1 } =>
                value is not SqlValue.Cast && value.PathRead is JsonDocumentPath p && pins.Contains(p),
            _ => false,
        };

        int Check(int conjunct)
        {
            if (conjunct < 0 || conjunct >= Statement.Conjuncts.Count)
                throw new ArgumentOutOfRangeException(nameof(conjunct), conjunct, "No such conjunct in the statement.");

            return conjunct;
        }

    }

    /// <summary>
    /// The contexts of one statement beneath one declaration: the declaration's own, and the children made beneath it,
    /// each keyed by the predicates it assumes.
    /// </summary>
    /// <remarks>
    /// Keyed by digests rather than by a statement's indices, so that a tree serves every statement with the same
    /// conjuncts in whatever order. Released once nothing holds it: its contexts are disposed by a finalizer, the way a
    /// query's context is (<see cref="JsonScopeTables"/>).
    /// </remarks>
    internal sealed class StatementTerms
    {

        /// <summary>
        /// How a context runs: it stops once it is contradictory, there being nothing more to know of a filter that keeps
        /// no row.
        /// </summary>
        static readonly RunOptions Default = new() { StopOnContradiction = true };

        /// <summary>
        /// How many answers about predicates the statement does not hold are remembered.
        /// </summary>
        const int Answers = 1024;

        readonly PredicateStatement _statement;
        readonly JsonContexts? _contexts;
        readonly RunOptions _run = Default;
        readonly PredicateRules? _rules;
        readonly JsonFactVocabulary? _vocabulary;
        readonly HashSet<string> _held = new(StringComparer.Ordinal);
        readonly Dictionary<string, Node> _children = new(StringComparer.Ordinal);
        readonly Dictionary<string, OracleVerdict> _answers = new(StringComparer.Ordinal);
        readonly Lease _lease = new();
        readonly object _lock = new();

        /// <summary>
        /// Begins a statement's tree at a declaration's context.
        /// </summary>
        internal StatementTerms(JsonFactDeclaration declaration, PredicateStatement statement, RunOptions? options)
        {
            _run = options ?? Default;
            _contexts = declaration.Generation.Contexts;
            _vocabulary = declaration.Vocabulary;
            _rules = PredicateRules.Of(_contexts);
            _statement = statement;

            foreach (var conjunct in statement.Conjuncts)
                foreach (var term in conjunct.Subterms)
                    _held.Add(term.ToString());

            Root = new Node(this, declaration.Context, null, "", complete: true, holdsTerms: false, contradictory: false, report: null);
        }

        StatementTerms(PredicateStatement statement)
        {
            _statement = statement;
            Root = new Node(this, null, null, "", complete: false, holdsTerms: false, contradictory: false, report: null);
        }

        /// <summary>
        /// Returns the terms of a statement beneath a declaration the engine could not compile: they know nothing.
        /// </summary>
        public static StatementTerms Unknowing(PredicateStatement statement) => new(statement);

        /// <summary>
        /// Gets the declaration's own context, frozen: what holds of every document (L0). It holds none of the statement's
        /// terms; a question asked of it is answered in a child made for the question.
        /// </summary>
        public Node Root { get; }

        /// <summary>
        /// Gets the rules the contexts run, or <c>null</c> where the declaration could not be compiled.
        /// </summary>
        internal PredicateRules? Rules => _rules;

        /// <summary>
        /// Returns the child of a context that assumes these predicates beside what it holds, made the first time it is
        /// asked for. It holds the statement's terms and their verdicts, whatever it assumes.
        /// </summary>
        public Node Child(Node parent, IEnumerable<SqlPredicate> assumed)
        {
            var distinct = new Dictionary<string, SqlPredicate>(StringComparer.Ordinal);
            foreach (var predicate in assumed)
                distinct.TryAdd(predicate.ToString(), predicate);

            if (distinct.Count == 0 || parent.Scope is null || _rules is null)
                return parent;

            var key = parent.Key + "/" + string.Join(" & ", distinct.Keys.OrderBy(k => k, StringComparer.Ordinal));

            lock (_lock)
            {
                if (_children.TryGetValue(key, out var child) == false)
                {
                    var predicates = distinct.Values.ToArray();
                    var made = _statement.Conjuncts.Concat(predicates).ToArray();
                    var paths = _statement.Paths.Concat(predicates.SelectMany(p => p.Paths)).Distinct().ToArray();
                    child = Open(parent, key, assumed: predicates, made: made, paths: paths, holdsTerms: true);
                    _children[key] = child;
                }

                return child;
            }
        }

        /// <summary>
        /// Returns what a context says of a predicate: read off it where it holds the predicate's term, and otherwise off
        /// a child made to hold it, asked and released.
        /// </summary>
        public OracleVerdict Judge(Node node, SqlPredicate predicate)
        {
            if (node.Contradictory)
                return OracleVerdict.Contradicted;

            if (node.Scope is null || _rules is null)
                return OracleVerdict.Unknown;

            var digest = predicate.ToString();
            if (node.HoldsTerms && _held.Contains(digest))
                return Read(node.Scope, predicate);

            var key = node.Key + "?" + digest;
            lock (_lock)
            {
                if (_answers.TryGetValue(key, out var known))
                    return known;

                var transient = Open(node, key, assumed: Array.Empty<SqlPredicate>(), made: new[] { predicate }, paths: predicate.Paths.ToArray(), holdsTerms: false, keep: false);
                OracleVerdict verdict;
                try
                {
                    verdict = transient.Contradictory ? OracleVerdict.Contradicted
                        : transient.Complete && ReferenceEquals(transient.Scope, node.Scope) == false ? Read(transient.Scope!, predicate)
                        : OracleVerdict.Unknown;
                }
                finally
                {
                    if (ReferenceEquals(transient.Scope, node.Scope) == false)
                        transient.Scope?.Dispose();
                }

                if (_answers.Count < Answers)
                    _answers[key] = verdict;

                return verdict;
            }
        }

        /// <summary>
        /// Reads the verdicts a context holds on a predicate whose term it has.
        /// </summary>
        OracleVerdict Read(Scope scope, SqlPredicate predicate)
        {
            try
            {
                var term = Term(predicate, new List<Fact>());
                return new OracleVerdict(
                    scope.IsContradictory,
                    scope.Check(_rules!.AlwaysTrue.Of(term)),
                    scope.Check(_rules.AlwaysFalse.Of(term)),
                    scope.Check(_rules.NeverTrue.Of(term)));
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                return OracleVerdict.Unknown;
            }
        }

        /// <summary>
        /// Opens a context beneath another: mentions the paths it will be asked about, makes the predicates' terms and
        /// assumes those it is given, runs the rules to saturation and the verdicts after them, and freezes it.
        /// </summary>
        /// <remarks>
        /// A context that cannot be opened, or whose run stops on a limit, is released, and the node reads its parent's
        /// context instead: it knows nothing more than its parent did. One made to answer a single question is not kept:
        /// it is neither frozen nor leased, and the caller releases it.
        /// </remarks>
        Node Open(Node parent, string key, IReadOnlyList<SqlPredicate> assumed, IReadOnlyList<SqlPredicate> made, IReadOnlyList<JsonDocumentPath> paths, bool holdsTerms, bool keep = true)
        {
            var rules = _rules!;
            var json = rules.Json;
            Scope? scope = null;

            try
            {
                scope = parent.Scope!.CreateChild();

                var specs = paths.Select(JsonFactEncoding.Path).ToArray();
                _contexts!.Mention(scope, specs);

                // What a context assumes is what it mentions, as a query's context mentions what it established: a split
                // opens only on a discriminator something here narrows. A stratum splits on what its conjuncts read, not
                // on everything the statement does; a context that assumes nothing splits on nothing, its parent having
                // lifted what a split there proves.
                var facts = new List<Fact>();
                foreach (var spec in assumed.SelectMany(p => p.Paths).Distinct().Select(JsonFactEncoding.Path))
                    facts.Add(json.Mentioned.Of(spec.ToTerm(json)));

                foreach (var predicate in made)
                    scope.Make(Term(predicate, facts));

                foreach (var predicate in assumed)
                    facts.Add(rules.Holds.Of(Term(predicate, facts), true));

                if (facts.Count > 0)
                    scope.Assume(facts.ToArray());

                var report = scope.Run(assumed.Count == 0 ? rules.Answer : rules.Saturation, _run);
                var contradictory = report.StopReason == RunStopReason.Contradiction || scope.IsContradictory;

                if (contradictory == false && report.StopReason != RunStopReason.Completed)
                {
                    scope.Dispose();
                    return Unknowing(parent, key, report);
                }

                if (contradictory)
                {
                    // Nothing is read of a context that keeps nothing; it is released at once.
                    scope.Dispose();
                    return new Node(this, null, parent, key, complete: true, holdsTerms: false, contradictory: true, report: report);
                }

                if (keep)
                {
                    scope.Freeze();
                    _lease.Add(scope);
                }

                return new Node(this, scope, parent, key, complete: true, holdsTerms: holdsTerms, contradictory: false, report: report);
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                scope?.Dispose();
                return Unknowing(parent, key, null);
            }
        }

        Node Unknowing(Node parent, string key, RunReport? report) =>
            new(this, parent.Scope, parent, key, complete: false, holdsTerms: parent.HoldsTerms, contradictory: parent.Contradictory, report: report);

        /// <summary>
        /// Builds a predicate's term, and states beside it the arms of every junction in it.
        /// </summary>
        ConstructorTerm<Pred> Term(SqlPredicate predicate, List<Fact> bookkeeping)
        {
            var r = _rules!;
            switch (predicate)
            {
                case SqlPredicate.Compare c:
                {
                    var compare = r.Compare.Of(c.Op, Value(c.Left), Value(c.Right));
                    if (c.Right is SqlValue.Literal literal)
                        bookkeeping.Add(r.Point.Of(compare, literal.Value));

                    return compare;
                }
                case SqlPredicate.In i:
                {
                    var membership = r.In.Of(Value(i.Value), i.Points);
                    foreach (var point in i.Points)
                        bookkeeping.Add(r.Point.Of(membership, point));

                    return membership;
                }
                case SqlPredicate.IsNull n:
                    return r.IsNull.Of(Value(n.Value));
                case SqlPredicate.Not n:
                    return r.Not.Of(Term(n.Operand, bookkeeping));
                case SqlPredicate.Opaque o:
                    return r.Opaque.Of(o.Id);
                case SqlPredicate.And a:
                    return Junction(Flatten(a.Arms, x => (x as SqlPredicate.And)?.Arms), r.And, truth: false, bookkeeping);
                case SqlPredicate.Or o:
                    return Junction(Flatten(o.Arms, x => (x as SqlPredicate.Or)?.Arms), r.Or, truth: true, bookkeeping);
                default:
                    throw new NotSupportedException(predicate.GetType().Name);
            }
        }

        /// <summary>
        /// Builds a junction right-nested, and states the arms of it and of each junction it nests: a disjunction is
        /// decided by its arms where it is TRUE, a conjunction where it is FALSE.
        /// </summary>
        ConstructorTerm<Pred> Junction(IReadOnlyList<SqlPredicate> arms, Constructor<Pred, Pred, Pred> make, bool truth, List<Fact> bookkeeping)
        {
            var r = _rules!;
            var terms = arms.Select(arm => Term(arm, bookkeeping)).ToArray();
            var junction = terms[^1];

            for (var k = terms.Length - 2; k >= 0; k--)
            {
                junction = make.Of(terms[k], junction);

                var count = terms.Length - k;
                bookkeeping.Add(r.Arms.Of(junction, truth).Is(ValueSet.From(Enumerable.Range(0, count).Select(m => (long)m))));
                for (var m = 0; m < count; m++)
                    bookkeeping.Add(r.Arm.Of(junction, truth, (long)m, terms[k + m]));
            }

            return junction;
        }

        static IReadOnlyList<SqlPredicate> Flatten(IReadOnlyList<SqlPredicate> arms, Func<SqlPredicate, IReadOnlyList<SqlPredicate>?> nested)
        {
            var flat = new List<SqlPredicate>();
            foreach (var arm in arms)
            {
                if (nested(arm) is { } inner)
                    flat.AddRange(Flatten(inner, nested));
                else
                    flat.Add(arm);
            }

            return flat;
        }

        ConstructorTerm<Val> Value(SqlValue value)
        {
            var r = _rules!;
            return value switch
            {
                SqlValue.Text t => r.Text.Of(Path(t.Path)),
                SqlValue.Raw w => r.Raw.Of(Path(w.Path)),
                SqlValue.Typed t => r.Typed.Of(Path(t.Path), t.Type),
                SqlValue.Cast c => r.Cast.Of(c.Target, Value(c.Value)),
                SqlValue.Literal l => r.Literal.Of(l.Value),
                SqlValue.Parameter p => r.Parameter.Of(p.Ordinal, p.Type),
                _ => throw new NotSupportedException(value.GetType().Name),
            };
        }

        Term<JPath> Path(JsonDocumentPath path) => JsonFactEncoding.Path(path).ToTerm(_rules!.Json);

        /// <summary>
        /// One context of the tree, or where its run did not complete, the one above it read in its place.
        /// </summary>
        internal sealed class Node
        {

            readonly StatementTerms _owner;
            JsonFactSet? _view;

            public Node(StatementTerms owner, Scope? scope, Node? parent, string key, bool complete, bool holdsTerms, bool contradictory, RunReport? report)
            {
                _owner = owner;
                Scope = scope;
                Parent = parent;
                Key = key;
                Complete = complete;
                HoldsTerms = holdsTerms;
                Contradictory = contradictory;
                Report = report;
            }

            /// <summary>The context read, or <c>null</c> where nothing is known or nothing is kept.</summary>
            public Scope? Scope { get; }

            public Node? Parent { get; }

            public string Key { get; }

            /// <summary>Whether the run that opened it completed, or ended in a contradiction.</summary>
            public bool Complete { get; }

            /// <summary>Whether the context holds the statement's terms and their verdicts.</summary>
            public bool HoldsTerms { get; }

            /// <summary>Whether the context admits no document.</summary>
            public bool Contradictory { get; }

            public RunReport? Report { get; }

            /// <summary>
            /// Gets the context as a fact set, as the translator and every other consumer reads one.
            /// </summary>
            public JsonFactSet View => _view ??= _owner.ViewOf(this);

        }

        JsonFactSet ViewOf(Node node)
        {
            if (node.Contradictory)
                return new JsonFactSet(Contradiction.Tables, _vocabulary);

            if (node.Scope is null || _rules is null)
                return JsonFactSet.Empty;

            return new JsonFactSet(new Leased(new JsonScopeTables(_rules.Json, node.Scope, owned: false), _lease), _vocabulary);
        }

        /// <summary>
        /// Holds a statement's contexts and releases them, children first, once nothing holds the statement or a fact set
        /// read from it.
        /// </summary>
        sealed class Lease
        {

            readonly List<Scope> _scopes = new();

            public void Add(Scope scope)
            {
                lock (_scopes)
                    _scopes.Add(scope);
            }

            ~Lease()
            {
                for (var i = _scopes.Count - 1; i >= 0; i--)
                    _scopes[i].Dispose();
            }

        }

        /// <summary>
        /// A context's tables, holding its statement's lease for as long as anything reads them.
        /// </summary>
        sealed class Leased : JsonFactTables
        {

            readonly JsonFactTables _tables;
            readonly Lease _lease;

            public Leased(JsonFactTables tables, Lease lease)
            {
                _tables = tables;
                _lease = lease;
            }

            public override bool IsContradictory => Keep(_tables.IsContradictory);

            public override bool Present(JsonDocumentPath path) => Keep(_tables.Present(path));

            public override bool TryTypes(JsonDocumentPath path, out ValueSet<string> types) => Keep(_tables.TryTypes(path, out types));

            public override bool TryDom(JsonDocumentPath path, out ValueSet<string> dom) => Keep(_tables.TryDom(path, out dom));

            public override bool Ne(JsonDocumentPath path, string value) => Keep(_tables.Ne(path, value));

            public override bool TryLo(JsonDocumentPath path, out Bound bound) => Keep(_tables.TryLo(path, out bound));

            public override bool TryHi(JsonDocumentPath path, out Bound bound) => Keep(_tables.TryHi(path, out bound));

            public override bool Form(JsonDocumentPath path, string token) => Keep(_tables.Form(path, token));

            public override bool Extension(JsonDocumentPath path, string key) => Keep(_tables.Extension(path, key));

            T Keep<T>(T answer)
            {
                GC.KeepAlive(_lease);
                return answer;
            }

        }

        /// <summary>
        /// The tables of a context that admits no document: contradictory, and asked nothing else.
        /// </summary>
        sealed class Contradiction : JsonFactTables
        {

            public static readonly Contradiction Tables = new();

            public override bool IsContradictory => true;

            public override bool Present(JsonDocumentPath path) => false;

            public override bool TryTypes(JsonDocumentPath path, out ValueSet<string> types)
            {
                types = ValueSet<string>.Empty;
                return false;
            }

            public override bool TryDom(JsonDocumentPath path, out ValueSet<string> dom)
            {
                dom = ValueSet<string>.Empty;
                return false;
            }

            public override bool Ne(JsonDocumentPath path, string value) => false;

            public override bool TryLo(JsonDocumentPath path, out Bound bound)
            {
                bound = default;
                return false;
            }

            public override bool TryHi(JsonDocumentPath path, out Bound bound)
            {
                bound = default;
                return false;
            }

            public override bool Form(JsonDocumentPath path, string token) => false;

            public override bool Extension(JsonDocumentPath path, string key) => false;

        }

    }

}
