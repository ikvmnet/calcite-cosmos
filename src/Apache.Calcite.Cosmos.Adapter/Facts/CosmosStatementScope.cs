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
    /// <item><term><see cref="OracleQuestion.DeleteConjunct"/>, <see cref="OracleQuestion.Redundant"/></term><description>every
    /// conjunct but this one, admitted or not: the rows the rest of the filter keeps</description></item>
    /// <item><term><see cref="OracleQuestion.Kept"/></term><description>every conjunct, admitted or not: the rows the whole
    /// filter keeps, which only a strengthening may read</description></item>
    /// <item><term><see cref="OracleQuestion.Arm"/></term><description>every conjunct but this one, and the conditions the
    /// arm of a <c>CASE</c> inside it is read under</description></item>
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
        readonly List<StatementTerms.Node> _chain = new();
        readonly object _lock = new();
        StatementTerms.Node _final;

        /// <summary>
        /// The last round's stratum where every conjunct was admitted before it was opened: what it assumes beneath what,
        /// opened only where a question asks of every admitted conjunct.
        /// </summary>
        (StatementTerms.Node Parent, SqlPredicate[] Assumed)? _pending;

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

                    // Once every conjunct is admitted no round reads another stratum, and a filter pushed whole asks
                    // nothing of the last: it is opened when something asks of every admitted conjunct.
                    if (strata.Sum(s => s.Count) == count)
                    {
                        _pending = (node, added.Select(i => statement.Conjuncts[i]).ToArray());
                        break;
                    }

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
        public bool Complete => Chain.All(node => node.Complete);

        /// <summary>
        /// Gets the reports of the runs that opened each stratum, for measurement: a gate reads their stop reasons.
        /// </summary>
        public IReadOnlyList<RunReport> Reports => Chain.Select(node => node.Report).OfType<RunReport>().ToArray();

        /// <summary>
        /// The last stratum: every admitted conjunct's facts, opened now if it was left until asked for.
        /// </summary>
        StatementTerms.Node Final
        {
            get
            {
                lock (_lock)
                {
                    if (_pending is { } pending)
                    {
                        _final = _terms.Child(pending.Parent, pending.Assumed);
                        _chain.Add(_final);
                        _pending = null;
                    }

                    return _final;
                }
            }
        }

        /// <summary>
        /// The strata, the last opened.
        /// </summary>
        List<StatementTerms.Node> Chain
        {
            get
            {
                _ = Final;
                return _chain;
            }
        }

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
                    all.Where(i => _round[i] > 0 && Chain.Count > _round[i] && Chain[_round[i]].Complete).ToArray(),
                OracleQuestion.DeleteConjunct or OracleQuestion.Redundant or OracleQuestion.Arm =>
                    all.Where(i => i != Check(licence.Conjunct)).ToArray(),
                OracleQuestion.Kept =>
                    all.ToArray(),
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
        internal (Scope? Context, PredicateRules? Rules) ContextFor(OracleLicence licence) => (ScopeFor(licence).Engine.Scope, _terms.Rules);

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
        public IReadOnlyList<int> RedundantConjuncts() => RedundantConjuncts(Enumerable.Range(0, Statement.Conjuncts.Count));

        /// <summary>
        /// Returns those of some conjuncts that may be deleted, removing one at a time; the rest are kept unasked.
        /// </summary>
        /// <param name="candidates">The conjuncts to ask about, which a caller knows to be the only ones anything could decide.</param>
        public IReadOnlyList<int> RedundantConjuncts(IEnumerable<int> candidates)
        {
            var conjuncts = Statement.Conjuncts;
            var kept = Enumerable.Range(0, conjuncts.Count).ToList();
            var removed = new List<int>();

            foreach (var i in candidates.Select(Check).Distinct().OrderBy(i => conjuncts[i].Digest, StringComparer.Ordinal).ThenBy(i => i))
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
                    return _before[Check(licence.Conjunct)] ?? Final;

                case OracleQuestion.DeleteConjunct or OracleQuestion.Redundant:
                {
                    var conjunct = Check(licence.Conjunct);
                    return _terms.Child(_terms.Root, Statement.Conjuncts.Where((_, i) => i != conjunct));
                }

                case OracleQuestion.Arm:
                {
                    var conjunct = Check(licence.Conjunct);
                    return _terms.Child(_terms.Root, Statement.Conjuncts.Where((_, i) => i != conjunct).Concat(licence.Given ?? Array.Empty<SqlPredicate>()));
                }

                case OracleQuestion.Kept:
                    return _terms.Child(_terms.Root, Statement.Conjuncts);

                case OracleQuestion.PointRead:
                    return _terms.Child(_terms.Root, Statement.Conjuncts.Where(c => Pins(c, licence.Pins!)));

                case OracleQuestion.Select or OracleQuestion.OrderBy or OracleQuestion.GroupBy:
                    return Final;

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

}
