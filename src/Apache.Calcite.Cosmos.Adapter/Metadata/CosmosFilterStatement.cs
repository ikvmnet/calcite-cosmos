using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// A filter as the predicate oracle reads it: its conjuncts loaded, admitted stratum by stratum by the translator, and
    /// every question a rule asks of it answered from the facts the question's licence admits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No rule chooses the facts it reads.</b> Each question a filter's rules ask is one of a few kinds, and the kind
    /// names the facts (<see cref="CosmosStatementScope"/>, the licence table):
    /// </para>
    /// <list type="table">
    /// <item><term>whether a conjunct renders, and how (<see cref="Renders"/>, <see cref="IsAdmitted"/>,
    /// <see cref="FactsFor"/>, <see cref="Translate"/>)</term><description>the stratum before the one that admitted it, or
    /// for one no stratum admitted, the last: what its siblings applied beside it establish, never what it does</description></item>
    /// <item><term>a contradiction, the filter made <c>FALSE</c> (<see cref="IsContradictory"/>)</term><description>every
    /// conjunct: a strengthening, which the conjuncts' own facts may justify</description></item>
    /// <item><term>a conjunct deleted as redundant (<see cref="Redundant"/>)</term><description>every sibling, never the
    /// conjunct itself, one deletion at a time</description></item>
    /// <item><term>a comparison lowered onto its stored form (<see cref="Siblings"/>)</term><description>every sibling the
    /// deletions keep, never the conjunct itself</description></item>
    /// <item><term>the arm of a <c>CASE</c> inside a conjunct (<see cref="Within"/>)</term><description>every sibling the
    /// deletions keep, and the conditions the arm is read under</description></item>
    /// <item><term>a weakening already applied (<see cref="IsApplied"/>)</term><description>the conjuncts beneath and
    /// those pushed beside it, and the declaration</description></item>
    /// <item><term>a clause after the filter: a projection, a sort, a grouping, a lookup's key (<see cref="After"/>,
    /// <see cref="CosmosRowFacts"/>)</term><description>the last stratum: every admitted conjunct, which the service applies
    /// before the clause reads a row, or beside the key's restriction</description></item>
    /// <item><term>what the rows the filter keeps satisfy, for a reader that rewrites nothing: a constraint's scope, a
    /// self-join's key (<see cref="Proves"/>, <see cref="Kept"/>)</term><description>every conjunct</description></item>
    /// <item><term>a reader that names no filter: routing, a key (<see cref="CosmosOracle.Declared"/>)</term><description>the
    /// declaration</description></item>
    /// </list>
    /// <para>
    /// <b>Remembered by the condition itself.</b> A rule is asked the same filter many times — each time Volcano matches it,
    /// and again when it is implemented — and a condition is an immutable node the planner hands back unchanged, so a
    /// statement is remembered against the node, with the binding it was read through, and released with it. What the
    /// oracle remembers beneath it is keyed by the statement's digest, so two nodes spelling one filter share contexts.
    /// </para>
    /// <para>
    /// <b>Its strata are opened only where asked.</b> A rewrite asks deletions and contradictions and never renders; a
    /// filter rule renders and asks nothing else. Each is computed the first time it is asked, and once.
    /// </para>
    /// </remarks>
    internal sealed class CosmosFilterStatement
    {

        /// <summary>
        /// The statements read off each condition, by binding: a condition stands in more than one filter only where a
        /// rule copies one over another input.
        /// </summary>
        static readonly ConditionalWeakTable<RexNode, List<CosmosFilterStatement>> Read = new();

        readonly IReadOnlyList<CosmosPath?> _fields;
        readonly IReadOnlyList<CosmosReading> _readings;
        readonly CosmosContainerMetadata _container;
        readonly RexBuilder _rexBuilder;
        readonly string _rootAlias;
        readonly CosmosPredicateLoader.Loading _loading;
        readonly object _lock = new();
        CosmosStatementScope? _plain;
        CosmosStatementScope? _strata;
        IReadOnlyList<int>? _redundant;
        bool? _contradictory;
        JsonFactSet?[]? _siblings;
        (CosmosStatementScope Scope, int[] Index)? _remaining;

        CosmosFilterStatement(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings, CosmosContainerMetadata container, RexBuilder rexBuilder, string rootAlias)
        {
            Condition = condition;
            _fields = fields;
            _readings = readings;
            _container = container;
            _rexBuilder = rexBuilder;
            _rootAlias = rootAlias;
            _loading = new CosmosPredicateLoader.Loading(fields, readings, rootAlias);

            Conjuncts = ConjunctsOf(condition);
            Statement = _loading.Statement(Conjuncts);
        }

        /// <summary>
        /// Reads a condition's conjuncts as the translator writes a conjunction: every <c>AND</c> taken apart, depth first
        /// and left to right, and everything else a conjunct.
        /// </summary>
        /// <remarks>
        /// Not <c>RelOptUtil.conjunctions</c>, which drops what Calcite calls always true — a literal <c>TRUE</c>, and an
        /// <c>IS NOT NULL</c> over a column it types as never null — which the translator still writes, and must still
        /// render, and the rules must still be told renders.
        /// </remarks>
        internal static IReadOnlyList<RexNode> ConjunctsOf(RexNode condition)
        {
            var conjuncts = new List<RexNode>();
            Decompose(condition, conjuncts);
            return conjuncts;
        }

        static void Decompose(RexNode node, List<RexNode> conjuncts)
        {
            if (node is RexCall call && call.getKind() == SqlKind.AND)
            {
                for (var i = 0; i < call.getOperands().size(); i++)
                    Decompose((RexNode)call.getOperands().get(i), conjuncts);

                return;
            }

            conjuncts.Add(node);
        }

        /// <summary>
        /// Returns the statement a filter's condition is, read through its input's binding: the same one each time the
        /// same condition is asked about through the same binding.
        /// </summary>
        /// <param name="condition">The condition.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container, whose declaration the oracle reasons from.</param>
        /// <param name="rexBuilder">A builder over the condition's types, for the translator.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The statement.</returns>
        public static CosmosFilterStatement Of(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, RexBuilder rexBuilder, string rootAlias = CosmosImplementor.DefaultRootAlias)
        {
            if (condition is null)
                throw new ArgumentNullException(nameof(condition));
            if (fields is null)
                throw new ArgumentNullException(nameof(fields));
            if (container is null)
                throw new ArgumentNullException(nameof(container));

            readings ??= Array.Empty<CosmosReading>();

            var read = Read.GetValue(condition, _ => new List<CosmosFilterStatement>());
            lock (read)
            {
                foreach (var statement in read)
                    if (ReferenceEquals(statement._container, container) && statement._rootAlias == rootAlias
                        && statement._fields.SequenceEqual(fields) && statement._readings.SequenceEqual(readings))
                        return statement;

                var made = new CosmosFilterStatement(condition, fields, readings, container, rexBuilder, rootAlias);
                read.Add(made);
                return made;
            }
        }

        /// <summary>
        /// Gets the condition.
        /// </summary>
        public RexNode Condition { get; }

        /// <summary>
        /// Gets the condition's top-level conjuncts: every <c>AND</c> taken apart, depth first.
        /// </summary>
        public IReadOnlyList<RexNode> Conjuncts { get; }

        /// <summary>
        /// Gets the conjuncts in the IR, parallel to <see cref="Conjuncts"/>.
        /// </summary>
        public PredicateStatement Statement { get; }

        /// <summary>
        /// Gets a rewrite's answer for this condition, remembered here so that it is one node each time it is asked: the
        /// node the rules then render is the statement remembered against it.
        /// </summary>
        internal RexNode? Rewritten { get; set; }

        CosmosOracle Oracle => CosmosOracle.For(_container.Facts);

        /// <summary>
        /// The statement with nothing admitted: the questions that do not render.
        /// </summary>
        CosmosStatementScope Plain
        {
            get
            {
                lock (_lock)
                    return _plain ??= Oracle.Admit(Statement);
            }
        }

        /// <summary>
        /// The statement admitted by the translator, stratum by stratum.
        /// </summary>
        CosmosStatementScope Strata
        {
            get
            {
                lock (_lock)
                    return _strata ??= Oracle.Admit(Statement, (i, facts) => Translator(facts, new CosmosParameterList()).TryTranslate(Conjuncts[i], out _));
            }
        }

        CosmosRexTranslator Translator(JsonFactSet facts, CosmosParameterList parameters) =>
            new(_rexBuilder, _fields, parameters, null, _container, _readings, facts);

        // ── Rendering (L2) ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets whether every conjunct renders, each from the stratum before the one that admitted it: whether the filter
        /// pushes whole.
        /// </summary>
        public bool Renders => Strata.Residual.Count == 0;

        /// <summary>
        /// Determines whether a stratum admitted a conjunct: whether it reaches the service.
        /// </summary>
        public bool IsAdmitted(int conjunct) => StratumOf(conjunct) > 0;

        /// <summary>
        /// Returns the round that admitted a conjunct, counting from one, or zero where none did: one renders from the
        /// declaration alone, and each round after from what the rounds before it admitted.
        /// </summary>
        public int StratumOf(int conjunct) => Strata.StratumOf(conjunct);

        /// <summary>
        /// Returns the facts a conjunct is rendered from: the stratum before the one that admitted it, or the last for one
        /// no stratum admitted. A weakening of a conjunct left above is rendered from them too, beside every admitted one.
        /// </summary>
        public JsonFactSet FactsFor(int conjunct) => Strata.Facts(OracleLicence.RenderConjunct(conjunct));

        /// <summary>
        /// Returns a translator that renders a conjunct from the facts its licence admits.
        /// </summary>
        public CosmosRexTranslator TranslatorFor(int conjunct, CosmosParameterList parameters) => Translator(FactsFor(conjunct), parameters);

        /// <summary>
        /// Renders the condition, each conjunct from its own stratum, as the translator renders a conjunction: a chain of
        /// <c>AND</c> as written, and each conjunct within it on its own.
        /// </summary>
        /// <param name="parameters">Receives the bound values, in the order the conjuncts are written.</param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="CosmosTranslationException">A conjunct has no rendering from the facts its licence admits.</exception>
        public string Translate(CosmosParameterList parameters)
        {
            var builder = new StringBuilder();
            var next = 0;

            void Write(RexNode node)
            {
                if (node is RexCall call && call.getKind() == SqlKind.AND)
                {
                    builder.Append('(');
                    for (var i = 0; i < call.getOperands().size(); i++)
                    {
                        if (i > 0)
                            builder.Append(" AND ");

                        Write((RexNode)call.getOperands().get(i));
                    }

                    builder.Append(')');
                    return;
                }

                // Decompose reads a nested AND depth first, left to right: the order written here.
                var conjunct = next++;
                builder.Append(TranslatorFor(conjunct, parameters).Translate(node));
            }

            Write(Condition);
            return builder.ToString();
        }

        // ── The clauses after it: every admitted conjunct (L1) ───────────────────────────────────────────────────────

        /// <summary>
        /// Gets the conjuncts a stratum admitted, in the IR: those the service applies.
        /// </summary>
        public IEnumerable<SqlPredicate> Admitted => Statement.Conjuncts.Where((_, i) => IsAdmitted(i));

        /// <summary>
        /// Returns what holds of every row the filter's admitted conjuncts keep, for a clause the service applies after
        /// them: the last stratum.
        /// </summary>
        /// <param name="licence">The clause asking: a projection, a sort, a grouping or a key.</param>
        /// <returns>The facts.</returns>
        public JsonFactSet After(OracleLicence licence) =>
            licence.Question is OracleQuestion.Select or OracleQuestion.OrderBy or OracleQuestion.GroupBy or OracleQuestion.Key
                ? Strata.Facts(licence)
                : throw new ArgumentException("Only a projection, a sort, a grouping or a key reads the rows a filter kept.", nameof(licence));

        // ── Strengthening: every conjunct ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets whether the conjuncts and the declaration cannot all hold of one document, so that the filter keeps nothing.
        /// </summary>
        public bool IsContradictory
        {
            get
            {
                lock (_lock)
                    return _contradictory ??= Plain.IsContradictory(OracleLicence.Kept);
            }
        }

        /// <summary>
        /// Gets what holds of every row the filter keeps, every conjunct assumed: what a strengthening may read, and a
        /// reader of those rows that rewrites nothing.
        /// </summary>
        public JsonFactSet Kept => Plain.Facts(OracleLicence.Kept);

        /// <summary>
        /// Determines whether every row the filter keeps satisfies a predicate: whether, where every conjunct and the
        /// declaration hold, it always does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What a self-join asks of a constraint scoped by a predicate: the constraint holds among the documents its
        /// predicate admits, and a pair of documents is inside it only where each side's filter proves the predicate of
        /// the document it reads. Nothing is rewritten, so the conjuncts' own facts are among those asked: the question
        /// is about the rows the filter keeps (<see cref="OracleLicence.Kept"/>).
        /// </para>
        /// <para>
        /// Read through the filter's own loading, so that a term the two share is one term, whether the oracle can take
        /// it apart or not: a filter proves a conjunct it holds before any run, <c>UPPER(x) = 'A'</c> among them. The
        /// implication checker this replaced reached that by standing a column for each term, and decided
        /// <c>type = 'A'</c> proves <c>type IN ('A', 'B')</c> by Calcite's reading of a comparison of columns; the
        /// oracle decides it by what <c>type</c> may hold, which reaches the declaration too.
        /// </para>
        /// </remarks>
        /// <param name="predicate">The predicate, over the filter's binding.</param>
        /// <returns><c>true</c> where every row the filter keeps satisfies it.</returns>
        public bool Proves(RexNode predicate) =>
            Plain.Judge(_loading.Read(predicate ?? throw new ArgumentNullException(nameof(predicate))), OracleLicence.Kept).AlwaysTrue;

        /// <summary>
        /// Determines whether every document the container keeps satisfies a predicate: what a side with no filter
        /// proves, which is what the declaration does (L0).
        /// </summary>
        /// <param name="predicate">The predicate, over the binding.</param>
        /// <param name="fields">The binding.</param>
        /// <param name="readings">How each field is read.</param>
        /// <param name="container">The container.</param>
        /// <returns><c>true</c> where every document satisfies it.</returns>
        public static bool Declares(RexNode predicate, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container)
        {
            var loading = new CosmosPredicateLoader.Loading(fields, readings ?? Array.Empty<CosmosReading>(), CosmosImplementor.DefaultRootAlias);
            var nothing = loading.Statement(Array.Empty<RexNode>());
            return CosmosOracle.For(container.Facts).Admit(nothing).Judge(loading.Read(predicate), OracleLicence.Residual).AlwaysTrue;
        }

        // ── Deletion, every sibling, and lowering, every sibling kept (L3) ───────────────────────────────────────────

        /// <summary>
        /// Gets the conjuncts that may be deleted, removed one at a time so that two duplicates do not delete each other.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A conjunct is asked only where something could decide it: a sibling that reads a path it reads, or one above or
        /// beneath it, or that shares a term with it; or a path the declaration speaks of. Elsewhere its siblings and the
        /// declaration say nothing of what it reads, and nothing is always true of a path nothing is known of: asking would
        /// open a context to learn that.
        /// </para>
        /// </remarks>
        public IReadOnlyList<int> Redundant
        {
            get
            {
                lock (_lock)
                    return _redundant ??= Decidable().Any() ? Plain.RedundantConjuncts(Decidable()) : Array.Empty<int>();
            }
        }

        IEnumerable<int> Decidable()
        {
            var conjuncts = Statement.Conjuncts;
            var declared = _container.Facts.MentionedPaths;

            bool Meet(JsonDocumentPath a, JsonDocumentPath b) => a.IsWithin(b) || b.IsWithin(a);

            for (var i = 0; i < conjuncts.Count; i++)
            {
                var paths = conjuncts[i].Paths.ToArray();
                var terms = conjuncts[i].Subterms.Select(t => t.Digest).ToHashSet(StringComparer.Ordinal);

                var decidable = paths.Any(p => declared.Any(d => Meet(p, d)))
                    || Enumerable.Range(0, conjuncts.Count).Any(j => j != i
                        && (conjuncts[j].Paths.Any(q => paths.Any(p => Meet(p, q))) || conjuncts[j].Subterms.Any(t => terms.Contains(t.Digest))));

                if (decidable)
                    yield return i;
            }
        }

        /// <summary>
        /// Returns what every conjunct but one establishes, beside the declaration, of those the rewrite keeps: the facts
        /// a comparison inside that conjunct is lowered onto its stored form by.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A lowering is an equivalence where its fact holds, and the facts hold wherever the siblings do — which is every
        /// row the filter keeps, since the conjunct is decided only where they hold. The conjunct's own facts are not
        /// among them: a comparison never certifies its own stored form.
        /// </para>
        /// <para>
        /// <b>The siblings the rewrite keeps, not every sibling.</b> A deletion and a lowering are each sound on the
        /// statement they are asked of, and the rewrite does both: the second is asked of the statement the first leaves.
        /// Asked of every sibling, a conjunct deleted because this one implies it would lend this one
        /// its facts — the conjunct certifying itself through a sibling it had made redundant, the shape #117 forbids one
        /// step removed — and the lowering would be an equivalence only where a conjunct no longer applied holds.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The rewrite deletes the conjunct, and lowers nothing in it.</exception>
        public JsonFactSet Siblings(int conjunct)
        {
            lock (_lock)
            {
                var (scope, index) = Remaining;
                _siblings ??= new JsonFactSet?[Conjuncts.Count];
                return _siblings[conjunct] ??= scope.Facts(OracleLicence.DeleteConjunct(RemainingIndex(index, conjunct)));
            }
        }

        /// <summary>
        /// The statement the rewrite leaves: every conjunct <see cref="Redundant"/> does not delete, and where each of the
        /// condition's conjuncts stands in it.
        /// </summary>
        (CosmosStatementScope Scope, int[] Index) Remaining
        {
            get
            {
                lock (_lock)
                {
                    if (_remaining is { } remains)
                        return remains;

                    var redundant = Redundant;
                    var index = new int[Conjuncts.Count];
                    var remaining = new List<RexNode>();

                    for (var i = 0; i < Conjuncts.Count; i++)
                    {
                        index[i] = redundant.Contains(i) ? -1 : remaining.Count;
                        if (index[i] >= 0)
                            remaining.Add(Conjuncts[i]);
                    }

                    // Nothing deleted, nothing to read again: the statement is the one every question so far was asked of.
                    var scope = redundant.Count == 0 ? Plain : Oracle.Admit(_loading.Statement(remaining));
                    return (_remaining = (scope, index)).Value;
                }
            }
        }

        static int RemainingIndex(int[] index, int conjunct) =>
            index[conjunct] >= 0 ? index[conjunct] : throw new ArgumentOutOfRangeException(nameof(conjunct), conjunct, "The rewrite deletes the conjunct, and lowers nothing in it.");

        // ── Inside a CASE: the kept siblings and the arm's conditions ────────────────────────────────────────────────

        /// <summary>
        /// Returns what holds where an arm of a <c>CASE</c> inside a conjunct is read: every sibling the rewrite keeps, and
        /// the conditions the arm is read under.
        /// </summary>
        /// <param name="conjunct">The conjunct the <c>CASE</c> is part of.</param>
        /// <param name="conditions">The conditions of the arms it is nested in, outermost first.</param>
        public JsonFactSet Within(int conjunct, IReadOnlyList<RexNode> conditions)
        {
            var (scope, index) = Remaining;
            return scope.Facts(OracleLicence.Arm(RemainingIndex(index, conjunct), conditions.Select(_loading.Read).ToArray()));
        }

        /// <summary>
        /// Determines whether an arm of a <c>CASE</c> is read under conditions its kept siblings contradict, so that it is
        /// never read and the <c>CASE</c> answers its <c>ELSE</c>.
        /// </summary>
        public bool NeverWithin(int conjunct, IReadOnlyList<RexNode> conditions)
        {
            var (scope, index) = Remaining;
            return scope.IsContradictory(OracleLicence.Arm(RemainingIndex(index, conjunct), conditions.Select(_loading.Read).ToArray()));
        }

        // ── A weakening beneath ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Determines whether conjuncts a statement applies, beneath a filter or beside a weakening, already apply it:
        /// whether, where they and the declaration hold, it always does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The question redundancy asks, of a statement of the conjuncts applied with the weakening beside them, read in one
        /// reading so that a weakening spelled as a conjunct beneath is that conjunct's term. A weakening is derived from a
        /// conjunct left above, and the filter above is what the split rule matches next; answered no where it is applied,
        /// the same weakening is pushed under itself for ever. And one the declaration implies — a definedness test of a
        /// path every document holds — restricts nothing, and pushed it only makes a filter a host may plan in process,
        /// where the service's test has no body.
        /// </para>
        /// <para>
        /// So it rests on nothing the engine might not finish: a predicate a scope assumes is always true there, read off
        /// what the scope assumes before any run (<see cref="StatementTerms"/>), and so is anything a scope that keeps no row
        /// is asked. What a run adds is what the conjuncts beneath imply beyond spelling the weakening themselves.
        /// </para>
        /// </remarks>
        /// <param name="beneath">The conjuncts applied beside the weakening: those every member of the input applies, and
        /// those pushed with it, over the same binding.</param>
        /// <param name="weakening">The weakening.</param>
        /// <param name="fields">The binding.</param>
        /// <param name="readings">How each field is read.</param>
        /// <param name="container">The container.</param>
        /// <returns><c>true</c> where the weakening adds nothing to what is applied.</returns>
        public static bool IsApplied(IReadOnlyList<RexNode> beneath, RexNode weakening, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container)
        {
            var loading = new CosmosPredicateLoader.Loading(fields, readings ?? Array.Empty<CosmosReading>(), CosmosImplementor.DefaultRootAlias);
            var statement = loading.Statement(beneath.Append(weakening).ToArray());
            var verdict = CosmosOracle.For(container.Facts).Admit(statement).Judge(statement.Conjuncts[^1], OracleLicence.Redundant(statement.Conjuncts.Count - 1));
            return verdict.AlwaysTrue || verdict.Contradictory;
        }

    }

}
