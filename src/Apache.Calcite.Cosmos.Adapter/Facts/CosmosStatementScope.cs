using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

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
    /// <item><term><see cref="OracleQuestion.DeleteConjunct"/></term><description>the conjuncts kept beside it; where it is
    /// admitted, only the admitted ones, so that what the service applies is no weaker without it</description></item>
    /// <item><term><see cref="OracleQuestion.Siblings"/></term><description>every conjunct but this one, admitted or not:
    /// the rows the rest of the filter keeps, which a rewrite inside it may lean on</description></item>
    /// <item><term><see cref="OracleQuestion.Kept"/></term><description>every conjunct, admitted or not: the rows the whole
    /// filter keeps, which only a strengthening may read, and routing, which skips only partitions holding none of them</description></item>
    /// <item><term><see cref="OracleQuestion.Arm"/></term><description>every conjunct but this one, and the conditions the
    /// arm of a <c>CASE</c> inside it is read under</description></item>
    /// <item><term><see cref="OracleQuestion.PointRead"/></term><description>the conjuncts that pin the read's keys, and no
    /// other: a point read applies no predicate</description></item>
    /// <item><term><see cref="OracleQuestion.Applied"/></term><description>every admitted conjunct, and what lies beneath
    /// and beside them: what the statement a weakening joins already applies</description></item>
    /// <item><term><see cref="OracleQuestion.Select"/>, <see cref="OracleQuestion.OrderBy"/>,
    /// <see cref="OracleQuestion.GroupBy"/>, <see cref="OracleQuestion.Key"/></term><description>the last stratum: every
    /// admitted conjunct, applied before the clause reads a row or beside the restriction a key writes</description></item>
    /// <item><term><see cref="OracleQuestion.Declaration"/></term><description>the declaration: what holds of every
    /// document</description></item>
    /// </list>
    /// <para>
    /// <b>An order the service computes</b> — a sort, an extreme, an ordering comparison, a weakening's string branch, a
    /// rank — is read through <see cref="Apache.Calcite.Cosmos.Adapter.Metadata.CosmosServiceOrder"/> under the consumer's
    /// own row above, and nowhere else: the sort and the extreme under the last stratum, the comparison under the
    /// translator's, the weakening under <see cref="OracleQuestion.Applied"/>.
    /// </para>
    /// <para>
    /// <b>Admitted when first asked.</b> The strata are the renderer's answers, and a question that names no stratum —
    /// a contradiction, a rewrite's siblings, a point read's pins — needs none of them; so the rounds run the first time
    /// something asks of a stratum or of what was admitted, and once.
    /// </para>
    /// <para>
    /// <b>Thread-safe.</b> The contexts are frozen and read freely; the admission, and a context made for a question, are
    /// made under the statement's lock.
    /// </para>
    /// </remarks>
    internal sealed class CosmosStatementScope
    {

        readonly StatementTerms _terms;
        readonly Func<int, JsonFactSet, bool>? _renders;
        readonly Func<StatementTerms, StatementTerms.Node, SqlPredicate, StatementTerms.Node>? _renderedFrom;
        readonly IReadOnlyCollection<JsonDocumentPath> _declared;
        readonly object _lock = new();
        Admission? _admission;

        internal CosmosStatementScope(StatementTerms terms, PredicateStatement statement, Func<int, JsonFactSet, bool>? renders, IReadOnlyCollection<JsonDocumentPath>? declared = null, Func<StatementTerms, StatementTerms.Node, SqlPredicate, StatementTerms.Node>? renderedFrom = null)
        {
            _terms = terms;
            _renders = renders;
            _renderedFrom = renderedFrom;
            _declared = declared ?? Array.Empty<JsonDocumentPath>();
            Statement = statement;
        }

        /// <summary>
        /// Gets the statement.
        /// </summary>
        public PredicateStatement Statement { get; }

        /// <summary>
        /// Gets the conjuncts each round admitted, by index, in round order.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<int>> Strata => Admitted.Strata;

        /// <summary>
        /// Gets the conjuncts no round admitted.
        /// </summary>
        public IReadOnlyList<int> Residual => Admitted.Residual;

        /// <summary>
        /// Gets whether every context the admission opened ran to completion or to a contradiction.
        /// </summary>
        public bool Complete => Admitted.Chain.All(node => node.Complete);

        /// <summary>
        /// Gets the reports of the runs that opened each stratum, for measurement: a gate reads their stop reasons.
        /// </summary>
        public IReadOnlyList<RunReport> Reports => Admitted.Chain.Select(node => node.Report).OfType<RunReport>().ToArray();

        /// <summary>
        /// Returns the round that admitted a conjunct, counting from one, or zero where none did.
        /// </summary>
        public int StratumOf(int conjunct) => Admitted.Round[Check(conjunct)];

        /// <summary>
        /// The strata, admitted the first time something asks of them.
        /// </summary>
        Admission Admitted
        {
            get
            {
                lock (_lock)
                    return _admission ??= new Admission(this);
            }
        }

        /// <summary>
        /// The rounds of admission: each asks the renderer, of every conjunct not yet admitted, whether it renders given
        /// the facts of the stratum so far, and those that do enter a stratum beneath together.
        /// </summary>
        sealed class Admission
        {

            readonly CosmosStatementScope _scope;
            readonly List<StatementTerms.Node> _chain = new();

            /// <summary>
            /// The last round's stratum where every conjunct was admitted before it was opened: what it assumes beneath
            /// what, opened only where a question asks of every admitted conjunct.
            /// </summary>
            (StatementTerms.Node Parent, SqlPredicate[] Assumed)? _pending;

            StatementTerms.Node _final;

            public Admission(CosmosStatementScope scope)
            {
                _scope = scope;

                var terms = scope._terms;
                var conjuncts = scope.Statement.Conjuncts;
                var count = conjuncts.Count;
                Round = new int[count];
                Before = new StatementTerms.Node?[count];

                var strata = new List<IReadOnlyList<int>>();
                var node = terms.Root;
                _chain.Add(node);

                if (scope._renders is { } renders)
                {
                    for (var round = 1; node.Contradictory == false; round++)
                    {
                        var added = new List<int>();
                        for (var i = 0; i < count; i++)
                            if (Round[i] == 0 && renders(i, (scope._renderedFrom?.Invoke(terms, node, conjuncts[i]) ?? node).View))
                                added.Add(i);

                        if (added.Count == 0)
                            break;

                        // Rendered from this stratum, every one of them; what they establish enters only the next.
                        foreach (var i in added)
                        {
                            Round[i] = round;
                            Before[i] = node;
                        }

                        strata.Add(added);

                        // Once every conjunct is admitted no round reads another stratum, and a filter pushed whole asks
                        // nothing of the last: it is opened when something asks of every admitted conjunct.
                        if (strata.Sum(s => s.Count) == count)
                        {
                            _pending = (node, added.Select(i => conjuncts[i]).ToArray());
                            break;
                        }

                        node = terms.Child(node, added.Select(i => conjuncts[i]));
                        _chain.Add(node);

                        // A stratum that knows nothing more than the one above it can admit nothing new.
                        if (node.Complete == false)
                            break;
                    }
                }

                _final = node;
                Strata = strata;
                Residual = Enumerable.Range(0, count).Where(i => Round[i] == 0).ToArray();
            }

            public int[] Round { get; }

            public StatementTerms.Node?[] Before { get; }

            public IReadOnlyList<IReadOnlyList<int>> Strata { get; }

            public IReadOnlyList<int> Residual { get; }


            /// <summary>
            /// The strata, the last opened.
            /// </summary>
            public List<StatementTerms.Node> Chain
            {
                get
                {
                    _ = Final;
                    return _chain;
                }
            }

            /// <summary>
            /// The last stratum: every admitted conjunct's facts, opened now if it was left until asked for.
            /// </summary>
            public StatementTerms.Node Final
            {
                get
                {
                    lock (_scope._lock)
                    {
                        if (_pending is { } pending)
                        {
                            _final = _scope._terms.Child(pending.Parent, pending.Assumed);
                            _chain.Add(_final);
                            _pending = null;
                        }

                        return _final;
                    }
                }
            }

        }

        /// <summary>
        /// Returns the conjuncts whose facts a licence's scope holds, by index.
        /// </summary>
        /// <remarks>
        /// The licence table read structurally, for a checker: a render question's conjunct is never among them.
        /// </remarks>
        public IReadOnlyList<int> AssumedIn(OracleLicence licence)
        {
            var all = Enumerable.Range(0, Statement.Conjuncts.Count);
            var admitted = all.Where(i => StratumOf(i) > 0);

            if (licence.ReadsKeptRows)
                return admitted.Where(i => Admitted.Chain.Count > StratumOf(i) && Admitted.Chain[StratumOf(i)].Complete).ToArray();

            return licence.Question switch
            {
                OracleQuestion.RenderConjunct when StratumOf(licence.Conjunct) > 0 =>
                    admitted.Where(i => StratumOf(i) < StratumOf(licence.Conjunct)).ToArray(),
                OracleQuestion.RenderConjunct =>
                    admitted.Where(i => Admitted.Chain.Count > StratumOf(i) && Admitted.Chain[StratumOf(i)].Complete).ToArray(),
                OracleQuestion.DeleteConjunct =>
                    Beside(licence).ToArray(),
                OracleQuestion.Siblings or OracleQuestion.Arm =>
                    all.Where(i => i != Check(licence.Conjunct)).ToArray(),
                OracleQuestion.Kept =>
                    all.ToArray(),
                OracleQuestion.PointRead =>
                    all.Where(i => IsPin(Statement.Conjuncts[i], licence.Pins!)).ToArray(),
                OracleQuestion.Applied =>
                    admitted.ToArray(),
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
        /// Determines whether the facts a licence admits admit no document: whatever the question, nothing is kept.
        /// </summary>
        public bool IsContradictory(OracleLicence licence) => ScopeFor(licence).Contradictory;

        /// <summary>
        /// Returns the context a licence's questions are read from, and the rules it ran, for a checker that reads every row
        /// of it rather than asking: the context is <c>null</c> where nothing is known, or nothing is kept.
        /// </summary>
        /// <remarks>
        /// The context is this scope's and lives only as long as it: once the scope is unreachable its lease's finalizer
        /// disposes the context, which then answers less rather than throwing. A caller reading the context past its last use
        /// of the scope keeps the scope alive (<see cref="GC.KeepAlive(object)"/>); Release's JIT lets it go at this call.
        /// </remarks>
        internal (Scope? Context, PredicateRules? Rules) ContextFor(OracleLicence licence) => (ScopeFor(licence).Engine.Scope, _terms.Rules);

        /// <summary>
        /// Returns the conjuncts that may be deleted, removing one at a time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In digest order, ties by index, each asked against the conjuncts still kept
        /// (<see cref="OracleLicence.DeleteConjunct"/>): so two duplicates do not delete each other, the second being asked
        /// once the first is gone.
        /// </para>
        /// <para>
        /// <b>Asked only where something could decide it</b>: a sibling that reads a path the conjunct reads, or one above
        /// or beneath it, or that shares a term with it; or a path the declaration speaks of. Elsewhere its siblings and the
        /// declaration say nothing of what it reads, and asking would open a context to learn that. It is the oracle's bound
        /// on what it looks for, and costs what it bounds: a conjunct true of every document on its own, such as
        /// <c>IS_DEFINED(x) OR NOT IS_DEFINED(x)</c> over a path nothing speaks of, is not deleted.
        /// </para>
        /// </remarks>
        public IReadOnlyList<int> RedundantConjuncts()
        {
            var conjuncts = Statement.Conjuncts;
            var candidates = Decidable().ToArray();
            if (candidates.Length == 0)
                return Array.Empty<int>();

            var kept = Enumerable.Range(0, conjuncts.Count).ToList();
            var removed = new List<int>();

            foreach (var i in candidates.OrderBy(i => conjuncts[i].Digest, StringComparer.Ordinal).ThenBy(i => i))
            {
                if (Decide(i, OracleLicence.DeleteConjunct(i, kept.ToArray())) == Truth.Always)
                {
                    kept.Remove(i);
                    removed.Add(i);
                }
            }

            return removed;
        }

        IEnumerable<int> Decidable()
        {
            var conjuncts = Statement.Conjuncts;

            static bool Meet(JsonDocumentPath a, JsonDocumentPath b) => a.IsWithin(b) || b.IsWithin(a);

            for (var i = 0; i < conjuncts.Count; i++)
            {
                var paths = conjuncts[i].Paths.ToArray();
                var terms = conjuncts[i].Subterms.Select(t => t.Digest).ToHashSet(StringComparer.Ordinal);

                var decidable = paths.Any(p => _declared.Any(d => Meet(p, d)))
                    || Enumerable.Range(0, conjuncts.Count).Any(j => j != i
                        && (conjuncts[j].Paths.Any(q => paths.Any(p => Meet(p, q))) || conjuncts[j].Subterms.Any(t => terms.Contains(t.Digest))));

                if (decidable)
                    yield return i;
            }
        }

        /// <summary>
        /// The conjuncts a deletion is asked beside: those it names, never the conjunct itself, and where it is admitted,
        /// only the admitted.
        /// </summary>
        IEnumerable<int> Beside(OracleLicence licence)
        {
            var conjunct = Check(licence.Conjunct);
            var beside = (licence.Beside ?? Enumerable.Range(0, Statement.Conjuncts.Count).ToArray()).Where(j => j != conjunct);

            return StratumOf(conjunct) > 0 ? beside.Where(j => StratumOf(Check(j)) > 0) : beside;
        }

        /// <summary>
        /// The licence table: the scope a question's licence admits facts from.
        /// </summary>
        StatementTerms.Node ScopeFor(OracleLicence licence)
        {
            var conjuncts = Statement.Conjuncts;

            if (licence.ReadsKeptRows)
                return Admitted.Final;

            switch (licence.Question)
            {
                case OracleQuestion.RenderConjunct:
                    return Admitted.Before[Check(licence.Conjunct)] ?? Admitted.Final;

                case OracleQuestion.DeleteConjunct:
                    return _terms.Child(_terms.Root, Beside(licence).Select(j => conjuncts[j]));

                case OracleQuestion.Siblings:
                {
                    var conjunct = Check(licence.Conjunct);
                    return _terms.Child(_terms.Root, conjuncts.Where((_, i) => i != conjunct));
                }

                case OracleQuestion.Arm:
                {
                    var conjunct = Check(licence.Conjunct);
                    return _terms.Child(_terms.Root, conjuncts.Where((_, i) => i != conjunct).Concat(licence.Given ?? Array.Empty<SqlPredicate>()));
                }

                case OracleQuestion.Kept:
                    return _terms.Child(_terms.Root, conjuncts);

                case OracleQuestion.PointRead:
                    return _terms.Child(_terms.Root, conjuncts.Where(c => IsPin(c, licence.Pins!)));

                case OracleQuestion.Applied:
                    return _terms.Child(_terms.Root, conjuncts.Where((_, i) => StratumOf(i) > 0).Concat(licence.Given ?? Array.Empty<SqlPredicate>()));

                case OracleQuestion.Declaration:
                    return _terms.Root;

                default:
                    throw new ArgumentOutOfRangeException(nameof(licence), licence.Question, "No such question.");
            }
        }

        /// <summary>
        /// Determines whether a conjunct pins one of a point read's keys: an equality of what the path holds, or reads as,
        /// with one literal.
        /// </summary>
        /// <remarks>
        /// Which conjuncts a pins-only scope assumes, and what <c>CosmosPartitionKeyExtractor</c> splits a filter by: one
        /// recogniser, so a conjunct the split rule calls a pin is one the point read's scope assumes. What value it pins,
        /// if any, is the scope's to say — an equality over text a number renders as pins nothing.
        /// </remarks>
        internal static bool IsPin(SqlPredicate conjunct, IReadOnlyList<JsonDocumentPath> pins) => conjunct switch
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

}
