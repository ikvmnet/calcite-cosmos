using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

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
    /// <item><term>a conjunct deleted as redundant (<see cref="Redundant"/>)</term><description>the siblings still kept,
    /// never the conjunct itself, one deletion at a time; and for one that reaches the service, only siblings that reach it
    /// too</description></item>
    /// <item><term>a comparison lowered onto its stored form (<see cref="Siblings"/>)</term><description>every sibling the
    /// deletions keep, never the conjunct itself</description></item>
    /// <item><term>the arm of a <c>CASE</c> inside a conjunct (<see cref="Within"/>)</term><description>every sibling the
    /// deletions keep, and the conditions the arm is read under</description></item>
    /// <item><term>a weakening already applied (<see cref="IsApplied"/>)</term><description>every admitted conjunct, what
    /// the filters beneath apply and the weakenings pushed before it, and the declaration</description></item>
    /// <item><term>a clause after the filter: a projection, a sort, a grouping, a lookup's key (<see cref="After"/>,
    /// <see cref="CosmosRowFacts"/>)</term><description>the last stratum: every admitted conjunct, which the service applies
    /// before the clause reads a row, or beside the key's restriction</description></item>
    /// <item><term>what the rows the filter keeps satisfy, for a reader that rewrites nothing: a constraint's scope, a
    /// self-join's key (<see cref="Proves"/>, <see cref="Kept"/>)</term><description>every conjunct</description></item>
    /// <item><term>what a side with no filter proves, and a reader that names no filter (<see cref="Declares"/>,
    /// <see cref="CosmosOracle.Declared"/>)</term><description>the declaration</description></item>
    /// </list>
    /// <para>
    /// <b>Remembered by the condition itself.</b> A rule is asked the same filter many times — each time Volcano matches it,
    /// and again when it is implemented — and a condition is an immutable node the planner hands back unchanged, so a
    /// statement is remembered against the node, with the binding it was read through, and released with it. What the
    /// oracle remembers beneath it is keyed by the statement's digest, so two nodes spelling one filter share contexts.
    /// </para>
    /// <para>
    /// <b>Its strata are opened only where asked.</b> A contradiction, a rewrite's siblings and a key's pins need no
    /// stratum; a render, a deletion beside what reaches the service and a clause after the filter do. The statement has one
    /// scope, whose rounds run the first time something asks of them, and a second only where the rewrite deletes a
    /// conjunct: the statement the deletions leave, whose siblings a lowering reads.
    /// </para>
    /// </remarks>
    internal sealed class CosmosFilterStatement
    {

        /// <summary>
        /// The statements read off each condition, by container and then binding: a condition stands in more than one
        /// filter only where a rule copies one over another input.
        /// </summary>
        /// <remarks>
        /// Weak on the container as well as the condition. A statement reaches its container's reasoning, and a public
        /// caller, or <see cref="Declares"/> over a builder that outlives its plan, may pass a condition that lives for the
        /// process, such as a builder's cached <c>TRUE</c>; so a condition that outlives a table keeps none of it.
        /// </remarks>
        static readonly ConditionalWeakTable<RexNode, ConditionalWeakTable<CosmosContainerMetadata, List<CosmosFilterStatement>>> Read = new();

        readonly IReadOnlyList<CosmosPath?> _fields;
        readonly IReadOnlyList<CosmosReading> _readings;
        readonly CosmosContainerMetadata _container;
        readonly RexBuilder _rexBuilder;
        readonly string _rootAlias;
        readonly CosmosPredicateLoader.Loading _loading;
        readonly ServiceForm?[] _forms, _weakenings;
        readonly object _lock = new();
        bool[]? _owned;

        /// <summary>S's side of the admission under each set the shadow asked, the one it is proven under first; none unarmed.</summary>
        List<CosmosFilterRendering>? _renderings;

        /// <summary>Whether <see cref="_forms"/> and <see cref="_weakenings"/> hold what S's admission came to.</summary>
        bool _resolved;
        CosmosStatementScope? _scope;
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
            _forms = new ServiceForm?[Conjuncts.Count];
            _weakenings = new ServiceForm?[Conjuncts.Count];
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

            var read = Read.GetOrCreateValue(condition).GetOrCreateValue(container);
            lock (read)
            {
                // The builder too: the strata render through it, and a statement a reader made over a builder of its own
                // is no statement for a rule's translator to render.
                foreach (var statement in read)
                    if (ReferenceEquals(statement._rexBuilder, rexBuilder) && statement._rootAlias == rootAlias
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

        CosmosOracle Oracle => CosmosOracle.For(_container.Facts);

        /// <summary>
        /// Gets S as the container's schema asks it: its reasoning's oracle, bound wherever F was asked of the statement.
        /// </summary>
        /// <exception cref="ArgumentException">The container is bound to no schema's reasoning.</exception>
        internal RenderingOracle Rendering => (_container.Reasoning ?? throw JsonFactTheory.Unbound()).Rendering;

        /// <summary>
        /// The statement, admitted stratum by stratum the first time a question asks of a stratum.
        /// </summary>
        CosmosStatementScope Scope
        {
            get
            {
                lock (_lock)
                    return _scope ??= Oracle.Admit(Statement, Render);
            }
        }

        /// <summary>
        /// One round of admission: the translator asked of each pending conjunct, in order, from the node the round hands it.
        /// Site A: where a flow armed the shadow, S is asked once a round beside it (<see cref="CosmosFilterRendering"/>);
        /// writing, S answers alone for the conjuncts it owns, its "no" included (O2, O8), and after the last round says
        /// whether its admissions stand.
        /// </summary>
        AdmissionRound.Answer Render(AdmissionRound round)
        {
            // The shadow's: the rounds ran, which a statement none of whose conjuncts rendered says here alone.
            if (CosmosRenderingCapture.Armed)
                CosmosRenderingCapture.Observer?.Read(this, round);

            var arm = CosmosRenderingShadow.Armed ? CosmosRenderingShadow.Current : null;
            var ours = arm is null ? [] : Rendered(round, arm);
            if (round.Closing)
                return new([], ours);

            var translated = new List<int>();
            foreach (var i in round.Pending)
            {
                if (arm is { Writes: true } && Owned(i))
                    continue;

                var parameters = new CosmosParameterList();
                if (Translator(round.NodeFor(i).View, parameters).TryTranslateConjunct(Conjuncts[i], out var text) == false)
                    continue;

                if (CosmosRenderingCapture.Armed)
                    CosmosRenderingCapture.Observer?.Admitted(this, i, text!, parameters);

                translated.Add(i);
            }

            return new(translated, arm is { Writes: true } ? ours : []);
        }

        /// <summary>
        /// S asked of a round under each set the flow shadows, the statement's own first, whose admissions it hands back; the
        /// capture told of each as S's print, as it is told of the translator's text.
        /// </summary>
        IReadOnlyList<int> Rendered(AdmissionRound round, CosmosRenderingShadow.Arming arm)
        {
            if (_renderings is null)
            {
                // A statement's admission is asked of S from its first round, in the flow that ran it.
                if (round is not { Number: 1, Again: false, Closing: false })
                    return [];

                var owned = Enumerable.Range(0, Conjuncts.Count).Select(Owned).ToArray();
                _renderings = [.. new[] { arm.Set ?? CosmosRendering.SetFor(_container.ServiceTarget) }.Concat(arm.Observer?.Sets ?? []).Distinct()
                    .Select(set => new CosmosFilterRendering(this, set, arm, owned))];
            }

            var ours = _renderings.Select(r => r.Answer(round, _scope!)).ToList()[0];
            if (arm.Writes && round.Closing == false && CosmosRenderingCapture.Armed)
                foreach (var i in ours)
                {
                    var parameters = new CosmosParameterList();
                    CosmosRenderingCapture.Observer?.Admitted(this, i, Print(i, _renderings[0].Rounds[^1].Answer.Of(Statement.Conjuncts[i]).Best!, parameters), parameters);
                }

            return ours;
        }

        /// <summary>Whether S owns a conjunct: syntactic on the IR, never S's answer (<see cref="RenderingFamily.Owns"/>).</summary>
        bool Owned(int conjunct) => (_owned ??= [.. Statement.Conjuncts.Select(RenderingFamily.Owns)])[conjunct];

        /// <summary>Prints a form S chose for a conjunct into a list, over the paths the conjunct reads.</summary>
        string Print(int conjunct, ServiceForm form, CosmosParameterList parameters) =>
            RenderingPrinter.Print(form, Statement.Conjuncts[conjunct].Paths.Distinct().ToDictionary(SqlValue.Spell), _rootAlias, parameters);

        /// <summary>The form S chose for a conjunct it admitted, which implementation prints; none for the translator's, or unarmed.</summary>
        internal ServiceForm? Form(int conjunct) => Resolved()._forms[conjunct];

        /// <summary>S's weakening of a conjunct left residual, read from the bundle that answered it; none where S gave none, or unarmed.</summary>
        internal ServiceForm? Weakening(int conjunct) => Resolved()._weakenings[conjunct];

        /// <summary>
        /// What S's admission came to under the set the statement is proven under, held for implementation and the split
        /// rule; read once the rounds are done, the closing bundle opened then where one answers the weakenings.
        /// </summary>
        CosmosFilterStatement Resolved()
        {
            lock (_lock)
            {
                // The rounds run first where nothing has asked of them yet, as at implementation: then S's side exists.
                _ = Scope.Residual;
                if (_resolved || _renderings is null)
                    return this;

                var resolution = _renderings[0].Resolve(Scope);
                foreach (var (i, form) in resolution.Forms)
                    _forms[i] = Scope.StratumOf(i) > 0 ? form : null;
                foreach (var (i, weakening) in resolution.Weakenings)
                    _weakenings[i] = weakening;

                _resolved = true;
                return this;
            }
        }

        /// <summary>S's side of the admission under a set, resolved, for the shadow; none where no flow armed it when the rounds ran.</summary>
        internal (CosmosFilterRendering Rendering, CosmosFilterRendering.Resolution Resolution)? RenderingFor(RenderingSet set)
        {
            lock (_lock)
            {
                _ = Scope.Residual;
                return _renderings?.FirstOrDefault(r => r.Set == set) is { } rendering ? (rendering, rendering.Resolve(Scope)) : null;
            }
        }

        /// <summary>The sets S's admission was asked under, the statement's own first; none unarmed.</summary>
        internal IReadOnlyList<RenderingSet> RenderedSets
        {
            get
            {
                lock (_lock)
                    return [.. _renderings?.Select(r => r.Set) ?? []];
            }
        }

        /// <summary>
        /// S's weakening of a residual conjunct under a set, the statement's own where none is named, and the Rex the split
        /// rule pushes for it (<see cref="RenderingRexBuilder"/>): none where S gave none, or the builder writes none.
        /// </summary>
        internal (ServiceForm? Form, RexNode? Rex) WeakeningOf(int conjunct, RenderingSet? set = null)
        {
            var form = set is null ? Weakening(conjunct) : RenderingFor(set.Value) is { } r && r.Resolution.Weakenings.TryGetValue(conjunct, out var w) ? w : null;
            return (form, form is null ? null : RenderingRexBuilder.Build(form, _rexBuilder, AccessorsOf(conjunct)));
        }

        /// <summary>Each path a conjunct reads, by its spelling, and its accessor: what the Rex builder writes its weakening over.</summary>
        internal IReadOnlyDictionary<string, RenderingRexBuilder.Accessor> AccessorsOf(int conjunct) => _loading.AccessorsOf(Conjuncts[conjunct], _rexBuilder);

        /// <summary>The statement's scope, for the rendering check's importer.</summary>
        internal CosmosStatementScope ScopeForCheck => Scope;

        /// <summary>The declaration the statement reasons from, for the rendering check's importer.</summary>
        internal JsonFactTheory DeclarationForCheck => Oracle.Theory;

        /// <summary>The builder the strata render through, for the rendering check's Rex builder round trip.</summary>
        internal RexBuilder RexBuilderForCheck => _rexBuilder;

        /// <summary>The binding the conjuncts read their fields through, for the rendering check's in-process judge of a weakening.</summary>
        internal (IReadOnlyList<CosmosPath?> Fields, IReadOnlyList<CosmosReading> Readings) BindingForCheck => (_fields, _readings);

        /// <summary>
        /// The statement the declared-fact rewrite leaves of this one, through the same binding, or this one where nothing
        /// was rewritten: for the rendering check's rewrite test. Nothing in planning calls it.
        /// </summary>
        internal CosmosFilterStatement RewrittenForCheck => CosmosFactRewriter.Rewrite(Condition, _fields, _container, _rootAlias, _rexBuilder, _readings) is var rewritten
            && ReferenceEquals(rewritten, Condition) == false ? Of(rewritten, _fields, _readings, _container, _rexBuilder, _rootAlias) : this;

        CosmosRexTranslator Translator(JsonFactSet facts, CosmosParameterList parameters) =>
            new(_rexBuilder, _fields, parameters, facts, null, _container, _readings);

        // ── Rendering (L2) ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets whether every conjunct renders, each from the stratum before the one that admitted it: whether the filter
        /// pushes whole.
        /// </summary>
        public bool Renders => Scope.Residual.Count == 0;

        /// <summary>
        /// Determines whether a stratum admitted a conjunct: whether it reaches the service.
        /// </summary>
        public bool IsAdmitted(int conjunct) => StratumOf(conjunct) > 0;

        /// <summary>
        /// Returns the round that admitted a conjunct, counting from one, or zero where none did: one renders from the
        /// declaration alone, and each round after from what the rounds before it admitted.
        /// </summary>
        public int StratumOf(int conjunct) => Scope.StratumOf(conjunct);

        /// <summary>
        /// Returns the facts a conjunct is rendered from: the stratum before the one that admitted it, or the last for one
        /// no stratum admitted. A weakening of a conjunct left above is rendered from them too, beside every admitted one.
        /// </summary>
        public JsonFactSet FactsFor(int conjunct) => Scope.Facts(OracleLicence.RenderConjunct(conjunct));

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

            // Site P: writing, a conjunct S admitted prints the form it chose, no bundle and no question; beside the
            // translator, S's form under each set is printed into a list of its own and told the shadow.
            var arm = CosmosRenderingShadow.Armed ? CosmosRenderingShadow.Current : null;

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

                // Decompose reads a nested AND depth first, left to right: the order written here, which the node at
                // each index says.
                var conjunct = next++;
                System.Diagnostics.Debug.Assert(ReferenceEquals(Conjuncts[conjunct], node), "Translate walks the conjuncts Decompose read, in its order.");
                var text = arm is { Writes: true } && Form(conjunct) is { } form ? Print(conjunct, form, parameters) : TranslatorFor(conjunct, parameters).TranslateConjunct(node);
                builder.Append(text);
                if (CosmosRenderingCapture.Armed)
                    CosmosRenderingCapture.Observer?.Written(this, conjunct, text, parameters);
                if (arm is { Writes: false, Observer: { } observer })
                    Printed(conjunct, text, observer);
            }

            Write(Condition);
            return builder.ToString();
        }

        /// <summary>The shadow told, under each set, of the form S admitted for a conjunct, printed into a list of its own beside the translator's text.</summary>
        void Printed(int conjunct, string written, ICosmosRenderingShadowObserver observer)
        {
            foreach (var set in RenderedSets)
                if (RenderingFor(set) is { } r && r.Resolution.Forms.TryGetValue(conjunct, out var form))
                {
                    string? text;
                    try
                    {
                        text = Print(conjunct, form, new CosmosParameterList());
                    }
                    catch (Exception e) when (e is CosmosTranslationException or InvalidOperationException or ArgumentException)
                    {
                        text = null;
                    }

                    observer.Printed(this, conjunct, set, form, text, written);
                }
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
            licence.ReadsKeptRows
                ? Scope.Facts(licence)
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
                    return _contradictory ??= Scope.IsContradictory(OracleLicence.Kept);
            }
        }

        /// <summary>
        /// Gets what holds of every row the filter keeps, every conjunct assumed: what a strengthening may read, and a
        /// reader of those rows that rewrites nothing.
        /// </summary>
        public JsonFactSet Kept => Scope.Facts(OracleLicence.Kept);

        // ── A reader that rewrites nothing: routing, a point read's pins ─────────────────────────────────────────────

        /// <summary>
        /// Returns the facts a licence admits, for a reader that rewrites nothing and names its licence: routing, every
        /// conjunct; a point read, the pins alone.
        /// </summary>
        internal JsonFactSet FactsUnder(OracleLicence licence) => Scope.Facts(licence);

        /// <summary>
        /// Determines whether the facts a licence admits admit no document.
        /// </summary>
        internal bool KeepsNothingUnder(OracleLicence licence) => Scope.IsContradictory(licence);

        /// <summary>
        /// Decides one of the conjuncts from the facts a licence admits: whether a point read's pins make it true.
        /// </summary>
        internal Apache.Calcite.Cosmos.Facts.Saturation.Truth Decide(int conjunct, OracleLicence licence) => Scope.Decide(conjunct, licence);

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
            Scope.Judge(_loading.Read(predicate ?? throw new ArgumentNullException(nameof(predicate))), OracleLicence.Kept).AlwaysTrue;

        /// <summary>
        /// Determines whether every document the container keeps satisfies a predicate: what a side with no filter
        /// proves, which is what the declaration does (L0).
        /// </summary>
        /// <remarks>
        /// Asked of the filter a side with none has, <c>TRUE</c>, under the declaration's licence: read through a loading of
        /// the binding as any statement is, and remembered as one.
        /// </remarks>
        /// <param name="predicate">The predicate, over the binding.</param>
        /// <param name="fields">The binding.</param>
        /// <param name="readings">How each field is read.</param>
        /// <param name="container">The container.</param>
        /// <param name="rexBuilder">A builder over the predicate's types.</param>
        /// <returns><c>true</c> where every document satisfies it.</returns>
        public static bool Declares(RexNode predicate, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, RexBuilder rexBuilder)
        {
            var everything = Of(rexBuilder.makeLiteral(true), fields, readings, container, rexBuilder);
            return everything.Scope.Judge(everything._loading.Read(predicate ?? throw new ArgumentNullException(nameof(predicate))), OracleLicence.Declaration).AlwaysTrue;
        }

        // ── Deletion, every sibling, and lowering, every sibling kept (L3) ───────────────────────────────────────────

        /// <summary>
        /// Gets the conjuncts that may be deleted, removed one at a time so that two duplicates do not delete each other.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The oracle's own answer, which asks only where something could decide a conjunct
        /// (<see cref="CosmosStatementScope.RedundantConjuncts"/>).
        /// </para>
        /// <para>
        /// <b>What reaches the service is no weaker for a deletion.</b> A conjunct the strata admit is deleted only where the
        /// admitted siblings imply it (<see cref="OracleLicence.DeleteConjunct"/>): deleted for a sibling left above, whose
        /// weakening is all that goes down, an exact <c>label IS NOT NULL</c> beside <c>label &lt;&gt; 'zzz'</c> stopped
        /// reaching the service, and the null, the array and the object it excludes crossed the wire for the recheck to
        /// drop. The strata are the condition's as written: a sibling admitted only once the rewrite lowers it counts as one
        /// left above, and the conjunct is kept, which is never wrong.
        /// </para>
        /// </remarks>
        public IReadOnlyList<int> Redundant
        {
            get
            {
                lock (_lock)
                    return _redundant ??= Scope.RedundantConjuncts();
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
                return _siblings[conjunct] ??= scope.Facts(OracleLicence.Siblings(RemainingIndex(index, conjunct)));
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
                    var scope = redundant.Count == 0 ? Scope : Oracle.Admit(_loading.Statement(remaining));
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

        // ── A weakening beside what is applied ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Determines whether a weakening pushed beside this statement adds nothing to what is applied there: its admitted
        /// conjuncts, what the filters beneath apply and the weakenings pushed before it, and the declaration.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A weakening is derived from a conjunct left above, and the filter above is what the split rule matches next;
        /// answered no where it is applied, the same weakening is pushed under itself for ever. And one the declaration
        /// implies — a definedness test of a path every document holds — restricts nothing, and pushed it only makes a
        /// filter a host may plan in process, where the service's test has no body.
        /// </para>
        /// <para>
        /// Which of this statement's conjuncts count is the licence's (<see cref="OracleLicence.Applied"/>): the admitted
        /// ones. What lies beneath is the plan's, which the rule walks; it is read through this statement's loading, so a
        /// weakening spelled as a conjunct beneath is that conjunct's term. So it rests on nothing the engine might not
        /// finish: a predicate a scope assumes is always true there, read off what the scope assumes before any run
        /// (<see cref="StatementTerms"/>), and so is anything a scope that keeps no row is asked. What a run adds is what
        /// the conjuncts beneath imply beyond spelling the weakening themselves.
        /// </para>
        /// </remarks>
        /// <param name="weakening">The weakening.</param>
        /// <param name="beneath">What the filters beneath apply, and the weakenings pushed before this one, over the same
        /// binding.</param>
        /// <returns><c>true</c> where the weakening adds nothing to what is applied.</returns>
        public bool IsApplied(RexNode weakening, IReadOnlyList<RexNode> beneath)
        {
            var verdict = Scope.Judge(_loading.Read(weakening), OracleLicence.Applied(beneath.Select(_loading.Read).ToArray()));
            return verdict.AlwaysTrue || verdict.Contradictory;
        }

    }

}
