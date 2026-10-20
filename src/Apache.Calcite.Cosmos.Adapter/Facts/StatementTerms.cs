using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

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
        readonly JsonFactSet _declared = JsonFactSet.Empty;
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
            _declared = declaration.Declared;
            _rules = PredicateRules.Of(_contexts);
            _statement = statement;

            foreach (var conjunct in statement.Conjuncts)
                foreach (var term in conjunct.Subterms)
                    _held.Add(term.Digest);

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
        /// <remarks>
        /// Every child is the engine's context, over a declaration with no conditional rule as over any other: what a
        /// stratum knows is what the rules derive beneath the declaration, and no reading of its predicates in C# stands
        /// in for them. A container that declares no schema opens one too, at a millisecond or a few a context
        /// (<c>DESIGN.md</c>, <em>Generations, and what a question costs</em>).
        /// </remarks>
        public Node Child(Node parent, IEnumerable<SqlPredicate> assumed)
        {
            var distinct = new Dictionary<string, SqlPredicate>(StringComparer.Ordinal);
            foreach (var predicate in assumed)
                distinct.TryAdd(predicate.Digest, predicate);

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

            // What a context assumes holds of every row it admits, and so does a conjunction of what it assumes: read off
            // the assumptions themselves, so that the answer rests on no run the engine might not finish. It is the
            // question a weakening beneath asks, whose answer no must never be for want of a run.
            if (node.Assumes(predicate))
                return OracleVerdict.Always;

            if (node.Scope is null || _rules is null)
                return OracleVerdict.Unknown;

            var digest = predicate.Digest;
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
                    return Unknowing(parent, key, report, assumed);
                }

                if (contradictory)
                {
                    // Nothing is read of a context that keeps nothing; it is released at once.
                    scope.Dispose();
                    return new Node(this, null, parent, key, complete: true, holdsTerms: false, contradictory: true, report: report, assumed);
                }

                if (keep)
                {
                    scope.Freeze();
                    _lease.Add(scope);
                }

                return new Node(this, scope, parent, key, complete: true, holdsTerms: holdsTerms, contradictory: false, report: report, assumed);
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                scope?.Dispose();
                return Unknowing(parent, key, null, assumed);
            }
        }

        Node Unknowing(Node parent, string key, RunReport? report, IReadOnlyList<SqlPredicate> assumed) =>
            new(this, parent.Scope, parent, key, complete: false, holdsTerms: parent.HoldsTerms, contradictory: parent.Contradictory, report: report, assumed);

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
                case SqlPredicate.IsDefined d:
                    return r.IsDefined.Of(Value(d.Value));
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

            public Node(StatementTerms owner, Scope? scope, Node? parent, string key, bool complete, bool holdsTerms, bool contradictory, RunReport? report, IReadOnlyList<SqlPredicate>? own = null)
            {
                _owner = owner;
                Scope = scope;
                Key = key;
                Complete = complete;
                HoldsTerms = holdsTerms;
                Contradictory = contradictory;
                Report = report;
                Holding = Hold(parent, own);
            }

            /// <summary>The digests of every predicate the node assumes, down to the root.</summary>
            public IReadOnlySet<string> Holding { get; }

            /// <summary>
            /// Determines whether the node assumes a predicate, or a conjunction of predicates it assumes.
            /// </summary>
            public bool Assumes(SqlPredicate predicate) =>
                Holding.Contains(predicate.Digest) || predicate is SqlPredicate.And and && Holding.Count > 0 && and.Arms.All(Assumes);

            static IReadOnlySet<string> Hold(Node? parent, IReadOnlyList<SqlPredicate>? own)
            {
                if (own is null || own.Count == 0)
                    return parent?.Holding ?? Nothing;

                var holding = new HashSet<string>(parent?.Holding ?? Nothing, StringComparer.Ordinal);
                foreach (var predicate in own)
                    holding.Add(predicate.Digest);

                return holding;
            }

            static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

            /// <summary>The context read, or <c>null</c> where nothing is known or nothing is kept.</summary>
            public Scope? Scope { get; }

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

        /// <summary>
        /// Reads a context as a fact set: the declaration's own view where the node reads the declaration's context, and
        /// otherwise the context's tables, told of each path they are asked about that the context has no term for, and
        /// holding the statement's lease for as long as anything reads them.
        /// </summary>
        /// <remarks>
        /// A stratum mentions what its statement reads (<see cref="Open"/>), and a clause after the filter reads paths no
        /// conjunct does: <c>ORDER BY name.first</c> over <c>WHERE name = 'x'</c> asks of a path the stratum was never told
        /// of, whose absence the rules derive the moment it is. So the tables tell the context of it then, in a child that
        /// assumes nothing (<see cref="Mentioning"/>), and every reader of a stratum knows what the rules derive of every
        /// path it asks about.
        /// </remarks>
        JsonFactSet ViewOf(Node node)
        {
            if (node.Contradictory)
                return new JsonFactSet(Contradiction.Tables, _vocabulary);

            if (node.Scope is null || _rules is null)
                return JsonFactSet.Empty;

            if (ReferenceEquals(node.Scope, Root.Scope))
                return _declared;

            var scope = node.Scope;
            return new JsonFactSet(new JsonScopeTables(_rules.Json, scope, owned: false, mention: path => Mentioning(scope, path), keep: _lease), _vocabulary);
        }

        /// <summary>
        /// Opens a child of a context that mentions a path it has no term for, run as a child made for a question is, and
        /// leases it with the statement's contexts; or returns <c>null</c> where its run does not complete.
        /// </summary>
        Scope? Mentioning(Scope scope, JsonDocumentPath path)
        {
            var told = _contexts!.Mentioning(scope, JsonFactEncoding.Path(path), _rules!.Answer, _run);
            if (told is not null)
                _lease.Add(told);

            return told;
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

            public override bool TryMinLength(JsonDocumentPath path, out long length)
            {
                length = 0;
                return false;
            }

            public override bool TryMaxLength(JsonDocumentPath path, out long length)
            {
                length = 0;
                return false;
            }

            public override bool Form(JsonDocumentPath path, string token) => false;

            public override bool TryChars(JsonDocumentPath path, out ValueSet<string> bands)
            {
                bands = ValueSet<string>.Empty;
                return false;
            }

            public override bool Extension(JsonDocumentPath path, string key) => false;

            public override bool Constant(JsonDocumentPath path, string value) => false;

        }

    }

}
