using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Splits a filter whose condition is partly renderable into the part Cosmos can evaluate and the
    /// part it cannot, so that the service does the work it is able to instead of none of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WHERE a AND b</c>, where <c>a</c> renders and <c>b</c> does not, becomes <c>WHERE b</c> over
    /// <c>WHERE a</c>. The inner filter converts to a <see cref="CosmosFilter"/> and reaches the service;
    /// the outer stays in Calcite and rechecks what is left. Without this the whole predicate is
    /// declined and every document in the container crosses the wire.
    /// </para>
    /// <para>
    /// <b>Why it is sound.</b> The pushed condition is a conjunction of a subset of the original's
    /// conjuncts, so the original <em>implies</em> it. A weaker filter can only discard rows the original
    /// would have discarded too, and the outer filter — the remaining conjuncts, applied to what
    /// survives — restores exactly the original result. The rule never needs to reason about what a
    /// predicate <em>means</em>, only that dropping conjuncts weakens.
    /// </para>
    /// <para>
    /// <b>Why only conjunctions.</b> Weakening is sound in a positive position and nowhere else.
    /// Dropping a disjunct from <c>a OR b</c> <em>strengthens</em> the predicate and loses every row that
    /// satisfied only the dropped side, so an <c>OR</c> is pushable only when every branch is, which is
    /// what the existing all-or-nothing rule already decides. Under a <c>NOT</c> the polarity flips and
    /// a weaker inner predicate makes a stronger outer one. Top-level conjuncts are the case where
    /// polarity needs no analysis: each one is positive by construction.
    /// </para>
    /// <para>
    /// The split terminates. It fires only where both parts are non-empty and something it would push is not yet
    /// applied beneath, compared as Calcite compares expressions. The inner is wholly renderable; the outer holds no
    /// term of a conjunct sent, or is the condition kept whole above the half sent, so split again it pushes only
    /// weakenings not yet applied, or nothing new, and stops. What is applied beneath is read through a projection
    /// too (<see cref="AlreadyApplied"/>): a weakening the transpose or the residual projection has already pushed
    /// beneath it is found applied over the rows the filter there reads, or over the projection's own columns, and is
    /// pushed once. Reading beneath a projection only adds what is assumed, so it can take a push away and never add
    /// one.
    /// </para>
    /// <para>
    /// <b>The outer is the conjuncts the inner does not decide, as written</b>, or what Calcite's simplification of
    /// the whole condition leaves beside the conjuncts sent, where it reads the two halves together (<see
    /// cref="Residual"/>). It holds nothing of the service's form, which Calcite has no body for
    /// (<see cref="CosmosOperators.HoldsServiceForm"/>): legality.
    /// </para>
    /// <para>
    /// <b>What the container declares is lowered first.</b> A comparison with no Cosmos form may have
    /// one once the declared stored form is taken into account, and the fact that says so is usually
    /// conditional on a sibling conjunct — so the whole condition is rewritten before it is taken apart,
    /// while the conjunct that proves the condition is still beside the one it licenses. See
    /// <see cref="Metadata.CosmosFactRewriter"/>.
    /// </para>
    /// <para>
    /// <b>Which conjuncts push is the oracle's strata</b> (<see cref="Metadata.CosmosFilterStatement"/>):
    /// a conjunct is pushed where it renders from what the conjuncts already pushed establish, round
    /// after round, and its own facts enter only the round after it. So a guard pushed beside what it
    /// licenses is the only kind of help a conjunct gets, to any depth, and none is its own. A
    /// weakening of a conjunct left above is rendered from everything pushed, and is pushed beside it.
    /// </para>
    /// <para>
    /// <b>Why a weakening is sound.</b> It is S's (<see cref="Metadata.CosmosFilterStatement.WeakeningOf"/>):
    /// a form S proved the conjunct implies, under the statement's set and the stratum it is read in, so
    /// on the service it discards only documents the conjunct discards, and the conjunct itself is kept
    /// in the residual and rechecked above.
    /// </para>
    /// </remarks>
    public class CosmosFilterSplitRule : RelOptRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits filters over.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosFilterSplitRule Create(CosmosConvention convention)
        {
            return new CosmosFilterSplitRule(convention);
        }

        readonly CosmosConvention _convention;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits filters over.</param>
        // RelOptRule's operand builders are deprecated in favour of RelRule.Config, whose operand
        // supplier is a chain of Calcite functional interfaces that costs more ceremony from C# than it
        // buys. The deprecation is "to be removed before 2.0" and the API is unchanged; this is not a
        // rule shape worth expressing twice.
#pragma warning disable CS0612
        public CosmosFilterSplitRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(Filter), any()),
                "CosmosFilterSplitRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <summary>
        /// Returns the container a subtree reads, or <c>null</c> where it does not read one.
        /// </summary>
        /// <remarks>
        /// Walked rather than read off a fixed operand position, because the filter may sit above a
        /// projection or a traversal as easily as above the scan — the split is the same argument
        /// either way, and <see cref="Split(Filter, Metadata.CosmosContainerMetadata, bool)"/> already binds through them.
        /// </remarks>
        internal static CosmosTable? FindTable(RelNode? node)
        {
            return FindTable(node, CosmosPlanMembers.NewSeen());
        }

        /// <inheritdoc cref="FindTable(RelNode?)" />
        /// <remarks>
        /// Every member is asked, not one representative: which container a subtree reads is a
        /// question about its <em>shape</em>, and a member whose type none of the cases mention
        /// answers "none" for a plan that reads one. See <see cref="CosmosPlanMembers"/>.
        /// </remarks>
        /// <param name="node">The subtree.</param>
        /// <param name="seen">The expressions already asked, which keeps a graph finite.</param>
        static CosmosTable? FindTable(RelNode? node, System.Collections.Generic.HashSet<RelNode> seen)
        {
            foreach (var member in CosmosPlanMembers.Of(node))
            {
                if (seen.Add(member) == false)
                    continue;

                var found = member switch
                {
                    TableScan scan => scan.getTable()?.unwrap(typeof(CosmosTable)) as CosmosTable,
                    Filter filter => FindTable(filter.getInput(), seen),
                    Project project => FindTable(project.getInput(), seen),
                    Correlate correlate => FindTable(correlate.getLeft(), seen),
                    _ => null,
                };

                if (found is not null)
                    return found;
            }

            return null;
        }

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            // Scoped to this convention's own container. A rule set is registered per container, and a
            // filter over somebody else's table is not this rule's business.
            if (FindTable(((Filter)call.rel(0)).getInput()) is not CosmosTable table || ReferenceEquals(table.Convention, _convention) == false)
                return false;

            // Only where the filter split off can reach the service, which is a logical filter's copy over an input that can:
            // a copy of a filter Calcite implements is Calcite's, as is one over a filter Calcite keeps or a correlate no rule
            // makes part of a statement, and a weakening is the service's form, with no in-process body for a Cosmos type test.
            if (((Filter)call.rel(0)).getConvention() != Convention.NONE || Reaches(_convention, ((Filter)call.rel(0)).getInput(), CosmosPlanMembers.NewSeen()) == false)
                return false;

            // Each weakening taken whole: what the filter beneath admits of it is onMatch's to ask, at a statement's cost.
            var (pushable, residual) = Split((Filter)call.rel(0), _convention.Container, admit: false);

            return pushable.Count > 0 && residual.Count > 0;
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);

            var (pushable, residual) = Split(filter, _convention.Container, admit: true, out var admitted, out var sent);
            if (pushable.Count == 0 || residual.Count == 0)
                return;

            var rexBuilder = filter.getCluster().getRexBuilder();

            // Legality: a weakening is the service's form, which Calcite has no body for, so the half split off holds one only
            // where it reaches the service: through a member already part of a statement, or over a projection through
            // Calcite's FILTER_PROJECT_TRANSPOSE, asked its own conditions (Transposed). Where nothing carries it, the conjuncts
            // admitted go alone, Calcite's own forms, and the weakenings of what is left above are not pushed by this split.
            var half = Compose(rexBuilder, pushable);
            if (CosmosOperators.HoldsServiceForm(half) && Reaches(_convention, filter.getInput(), CosmosPlanMembers.NewSeen(), half) == false)
            {
                if (admitted.Count == 0)
                    return;

                pushable = admitted;
            }

            // Split no further where everything it would push is applied beneath already, compared as Calcite compares
            // expressions: the filter is a split's outer, and split again it would stack the same half under itself for ever.
            // What its own rows hold alone: Calcite's equality compares nodes over one row type, and what a filter beneath a
            // projection applies is over another.
            var (applied, _, _) = AlreadyApplied(filter.getInput());
            if (pushable.All(applied.Contains))
                return;

            // What Calcite evaluates of the condition over the rows the half sent returns; true, nothing is left above, and the
            // conjuncts admitted are the filter, the weakenings of what is left out with it.
            var left = Residual(rexBuilder, filter.getCondition(), sent, ProjectOf(filter.getInput()));

            // Legality: what is left above is a filter Calcite implements, and the service's form has no body there. A host's
            // filter merge carries a split's weakenings up beside the conjuncts they weaken, and split again such a condition
            // left them above it, which failed with "cannot translate call IS_NUMBER".
            if (CosmosOperators.HoldsServiceForm(left))
                return;

            // Legality: the pair leaves the filter's input beneath what Calcite evaluates, and a projection there the service
            // selects is read for every row the half returns, where Calcite computes a column only for the rows that reach it.
            // A computed column whose reading can refuse a value then failed the statement over a row the condition drops, where
            // Calcite answers, whatever the condition reads: a CASE branch, COALESCE's later operands among them, is computed only
            // for the rows that reach it (ReadsWhatCanRefuse). The transpose still carries the filter beneath such a projection,
            // where it is split over the rows the projection reads.
            if (left.isAlwaysTrue() == false && ReadsWhatCanRefuse(filter.getInput()))
                return;

            if (left.isAlwaysTrue())
            {
                call.transformTo(filter.copy(filter.getTraitSet(), filter.getInput(), Compose(rexBuilder, admitted)));
                return;
            }

            var inner = filter.copy(filter.getTraitSet(), filter.getInput(), Compose(rexBuilder, pushable));
            call.transformTo(filter.copy(filter.getTraitSet(), inner, left));
        }

        /// <summary>
        /// Returns what Calcite evaluates of a filter's condition over the rows the half sent returns: the conjuncts that half
        /// does not decide, as the query wrote them; where Calcite's simplification of the whole condition reads the two
        /// halves together, its terms but those of the conjuncts sent, true where none is left; and where it reads the whole
        /// as false, the condition as it stands.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Calcite simplifies the condition it is given whole (unknown read as false), so a conjunct its siblings make go
        /// unread is not evaluated: <c>kind = 'A' AND (kind = 'A' OR CAST(body AS INTEGER) = 1)</c> is <c>kind = 'A'</c>,
        /// and keeps nothing above it. Each conjunct sent is read alone as well as with the rest sent, so a half sent that
        /// reads as false, beside a cast the whole does not fold, leaves no conjunct sent above to be pushed again.
        /// </para>
        /// <para>
        /// As written, not the planning form, whose lowerings and flattened <c>CASE</c>s Calcite does not read. A conjunct
        /// sent is one each conjunct of whose planning form the half sent admits.
        /// </para>
        /// <para>
        /// <b>Over a projection, through it</b>, as Calcite merges the two before it simplifies; what is left is written
        /// back over the columns, and where a term reads what no column stands for, the conjuncts are kept as written.
        /// </para>
        /// </remarks>
        internal static RexNode Residual(RexBuilder rexBuilder, RexNode condition, IReadOnlyList<bool> sent, Project? project)
        {
            var own = Metadata.CosmosFilterStatement.ConjunctsOf(condition);

            // The conjuncts the half sent does not decide, as the query wrote them.
            var written = Compose(rexBuilder, own.Where((_, i) => sent[i] == false).ToList());

            // Beneath a second projection the condition would be read through both, as Calcite merges them.
            if (project is not null && ProjectOf(project.getInput()) is not null)
                return written;

            var plain = new RexSimplify(rexBuilder, RelOptPredicateList.EMPTY, RexUtil.EXECUTOR);
            RexNode Read(IEnumerable<RexNode> conjuncts)
            {
                var composed = Compose(rexBuilder, conjuncts.ToList());
                return plain.simplifyUnknownAsFalse(project is null ? composed : RelOptUtil.pushPastProject(composed, project));
            }

            var whole = Terms(Read(own));
            var decided = Terms(Read(own.Where((_, i) => sent[i])));
            var rest = Terms(Read(own.Where((_, i) => sent[i] == false)));

            // Calcite evaluates the residual alone wherever the whole is its terms beside terms of the conjuncts sent, which
            // are true on every row the request returns: none of its terms read away, none read into another.
            var apart = new HashSet<RexNode>(decided.Concat(rest));
            var kept = new HashSet<RexNode>(whole);
            if (whole.All(apart.Contains) && rest.All(kept.Contains))
                return written;

            // A whole Calcite reads as false is left above the half sent as it stands, for Calcite to read as it does in
            // process: written into a node, the FALSE is one Calcite's rules read, an empty relation beside a join, where
            // Calcite in process holds the condition.
            if (whole.Any(term => term.isAlwaysFalse()))
                return condition;

            // Each conjunct sent read alone as well: where the half sent reads as false and the whole beside a cast does not,
            // no term of the whole is the half's, and a conjunct sent left above would be pushed beneath it again.
            var sentTerms = new HashSet<RexNode>(decided);
            foreach (var conjunct in own.Where((_, i) => sent[i]))
                sentTerms.UnionWith(Terms(Read([conjunct])));

            var left = whole.Where(term => sentTerms.Contains(term) == false).ToList();
            if (project is null)
                return Compose(rexBuilder, left);

            var columns = new Columns(project, rexBuilder);
            var over = left.Select(term => (RexNode)term.accept(columns)).ToList();
            return columns.Unread ? written : Compose(rexBuilder, over);
        }

        /// <summary>
        /// Returns a projection some equivalent form of a subtree is, a logical one first, or <c>null</c>.
        /// </summary>
        static Project? ProjectOf(RelNode? node)
        {
            Project? found = null;
            foreach (var member in Rel.CosmosPlanMembers.Of(node))
                if (member is Project project && (found is null || project.getConvention() == Convention.NONE))
                    found = project;

            return found;
        }

        /// <summary>
        /// Determines whether a projection the service may select, among a subtree's equivalent forms or beneath a filter
        /// there, holds a computed column whose reading can refuse a value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A filter and a projection commute in what they answer, not in what they raise: the service's projection is read
        /// for every row the statement returns, and Calcite computes a column only for the rows that reach it. A reading that
        /// can refuse a value (<see cref="Client.CosmosJson.CanRefuse(org.apache.calcite.rel.type.RelDataType)"/>: 1.5 as an
        /// <c>INTEGER</c>, text that is no date) then fails the statement over a row the condition above drops, where Calcite
        /// answers. Measured: <c>SELECT t."n" FROM (SELECT c."DOC", JSON_VALUE(c."DOC", '$.n' RETURNING INTEGER) AS "n" FROM
        /// products AS c) AS t WHERE INITCAP(JSON_VALUE(t."DOC", '$.name')) = 'Steel'</c>, split over the view's projection,
        /// selected the column under <c>WHERE IS_DEFINED(c.name)</c> and failed over <c>{"n":1.5,"name":true}</c>, which
        /// Calcite drops on <c>INITCAP</c> before it computes the column
        /// (<c>CosmosToClrCursorConverterTests.ShouldNotSelectAViewsReadingThatCanRefuseUnderAConditionASplitLeavesInProcess</c>).
        /// </para>
        /// <para>
        /// <b>Whatever the condition reads.</b> Calcite computes a <c>CASE</c> branch, and so a later operand of
        /// <c>COALESCE</c>, only for the rows that reach it, so a column the condition reads there is not computed for every
        /// row it tests: <c>… AND COALESCE(JSON_VALUE(t."DOC", '$.s'), CAST(t."n" AS VARCHAR)) = 'a'</c> over the same view
        /// selected the column and failed over <c>{"n":1.5,"name":"x","s":"q"}</c>, which Calcite drops without reading it.
        /// </para>
        /// <para>
        /// <b>Asked of the column's type alone</b>, as <see cref="CosmosProjectResidualRule"/> asks it of the projection it
        /// moves (<c>TODO.md</c>, section 7). Implementation reads a column of every type that can refuse as that type, its
        /// other readings being of text and of the document, which refuse nothing; the walk's reading is not implementation's,
        /// since it reads a <c>JSON_QUERY</c> returning an <c>INTEGER ARRAY</c> as JSON text where implementation reads the
        /// array and refuses <c>[1.5]</c>. A bare reference is read by the scan either way. A projection Calcite implements is
        /// its own evaluation, not the service's.
        /// </para>
        /// </remarks>
        /// <param name="node">The filter's input.</param>
        static bool ReadsWhatCanRefuse(RelNode? node)
        {
            var seen = Rel.CosmosPlanMembers.NewSeen();

            bool Holds(RelNode? candidate)
            {
                foreach (var member in Rel.CosmosPlanMembers.Of(candidate))
                {
                    if (seen.Add(member) == false || (member.getConvention() != Convention.NONE && member.getConvention() is not CosmosConvention))
                        continue;

                    if (member is Project project && Refuses(project))
                        return true;

                    // A filter keeps its row type, so a projection beneath it holds this relation's columns in the same ordinals.
                    if (member is Filter filter && Holds(filter.getInput()))
                        return true;
                }

                return false;
            }

            bool Refuses(Project project)
            {
                var projects = project.getProjects();
                for (var i = 0; i < projects.size(); i++)
                {
                    var expression = (RexNode)projects.get(i);
                    if (expression is not RexInputRef && Client.CosmosJson.CanRefuse(expression.getType()))
                        return true;
                }

                return false;
            }

            return Holds(node);
        }

        /// <summary>
        /// Writes an expression read through a projection back over its columns: each expression a column stands for as
        /// that column, and <see cref="Unread"/> where an input of the projection is left that no column stands for.
        /// </summary>
        sealed class Columns : RexShuttle
        {

            readonly Project _project;
            readonly RexBuilder _rexBuilder;

            public Columns(Project project, RexBuilder rexBuilder)
            {
                _project = project;
                _rexBuilder = rexBuilder;
            }

            /// <summary>Gets whether an input of the projection was left that no column stands for.</summary>
            public bool Unread { get; private set; }

            public override RexNode visitCall(RexCall call) => Column(call) ?? base.visitCall(call);

            public override RexNode visitFieldAccess(RexFieldAccess fieldAccess) => Column(fieldAccess) ?? base.visitFieldAccess(fieldAccess);

            public override RexNode visitInputRef(RexInputRef inputRef)
            {
                if (Column(inputRef) is RexNode column)
                    return column;

                Unread = true;
                return inputRef;
            }

            RexNode? Column(RexNode node)
            {
                var projects = _project.getProjects();
                for (var i = 0; i < projects.size(); i++)
                    if (node.Equals(projects.get(i)))
                        return _rexBuilder.makeInputRef(_project, i);

                return null;
            }

        }

        /// <summary>
        /// Returns the terms of a simplified condition, a literal <c>TRUE</c> among none.
        /// </summary>
        static List<RexNode> Terms(RexNode simplified)
        {
            return Metadata.CosmosFilterStatement.ConjunctsOf(simplified).Where(term => term.isAlwaysTrue() == false).ToList();
        }

        /// <summary>
        /// Partitions a filter's top-level conjuncts by whether each renders as Cosmos SQL.
        /// </summary>
        /// <remarks>
        /// Each conjunct is admitted by the statement's rounds, against the same binding
        /// <see cref="CosmosFilterRule"/> uses, so a conjunct this puts in the pushable half is one that
        /// rule would accept whole. An input whose binding cannot be derived splits into nothing, and the
        /// rule does not fire.
        /// </remarks>
        /// <param name="filter">The filter.</param>
        /// <param name="container">The container it reads.</param>
        /// <param name="admit">Whether a weakening is pushed only as far as the filter beneath admits it (<see cref="Admitted"/>),
        /// or whole, as <see cref="matches"/> asks it.</param>
        internal static (List<RexNode> Pushable, List<RexNode> Residual) Split(Filter filter, Metadata.CosmosContainerMetadata container, bool admit = true)
        {
            return Split(filter, container, admit, out _, out _);
        }

        /// <inheritdoc cref="Split(Filter, Metadata.CosmosContainerMetadata, bool)" />
        /// <param name="filter">The filter.</param>
        /// <param name="container">The container it reads.</param>
        /// <param name="admit">Whether a weakening is pushed only as far as the filter beneath admits it.</param>
        /// <param name="admitted">The conjuncts of the half pushed that are the condition's own, its weakenings left out.</param>
        /// <param name="sent">For each conjunct of the filter's condition as written, whether the half pushed decides it whole:
        /// each conjunct of its planning form admitted. Read only where <paramref name="admit"/>.</param>
        static (List<RexNode> Pushable, List<RexNode> Residual) Split(Filter filter, Metadata.CosmosContainerMetadata container, bool admit, out List<RexNode> admitted, out List<bool> sent)
        {
            admitted = new List<RexNode>();
            sent = new List<bool>();

            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out _) == false)
                return (new List<RexNode>(), new List<RexNode>());

            var rexBuilder = filter.getCluster().getRexBuilder();

            // Lowered before it is split, and that order is the point: a fact is usually conditional,
            // and the conjunct that proves the condition is a sibling of the one being lowered. Split
            // first and each conjunct would be weighed on its own, with nothing left to prove the
            // guard from. The planning form: the half pushed is a node Calcite reads, so it holds no
            // deletion, arm never read or FALSE; what is kept above is the condition as written (Residual).
            var condition = Metadata.CosmosFactRewriter.Planned(
                filter.getCondition(), fields, container, CosmosImplementor.DefaultRootAlias, rexBuilder, readings);

            // Admitted stratum by stratum: a conjunct pushes where it renders from what the conjuncts
            // pushed before it establish. A fact licenses a comparison only where the conjunct that
            // proved it is applied beside it -- a guard left above in the residual would leave the
            // service deciding rows the guard was meant to have excluded -- and only a conjunct pushed
            // beside it is in a stratum it is rendered from.
            var statement = Metadata.CosmosFilterStatement.Of(condition, fields, readings, container, rexBuilder);

            var pushable = new List<RexNode>();
            var residual = new List<RexNode>();

            // What lies beneath a weakening, beside the conjuncts the statement admits: every conjunct the
            // filters beneath apply, what a filter beneath a projection there applies written over its columns,
            // and the weakenings pushed before it, all over these rows; and beside them what a filter beneath
            // a projection applies, each conjunct with the relation it reads. A weakening they, the admitted
            // conjuncts and the declaration already imply restricts nothing -- one beneath, applied by a
            // filter the residual was split from, would be pushed under itself for ever -- and is not pushed.
            // Nor is one equal to a conjunct of this filter, which a filter not admitting it would push under
            // itself for ever (h15). Equal, not implied: every weakening is implied by its own conjunct, which
            // is not applied.
            List<RexNode>? beneath = null;
            IReadOnlyList<Metadata.CosmosFilterStatement.BoundConjunct>? across = null;
            var own = Metadata.CosmosFilterStatement.ConjunctsOf(filter.getCondition());

            // What the filter split off holds beside a weakening: every conjunct admitted, and the weakenings pushed before it.
            var held = Enumerable.Range(0, statement.Conjuncts.Count).Where(statement.IsAdmitted).Select(i => statement.Conjuncts[i]).ToList();

            for (var i = 0; i < statement.Conjuncts.Count; i++)
            {
                var conjunct = statement.Conjuncts[i];

                if (statement.IsAdmitted(i))
                {
                    pushable.Add(conjunct);
                    admitted.Add(conjunct);
                    continue;
                }

                // Kept whatever happens: a weakening is a restriction the service can apply, not a
                // replacement for the predicate, so the original is still rechecked above.
                residual.Add(conjunct);

                // S's weakening of it, read in the stratum a conjunct no round admitted is read in, which is never
                // its own facts, as the Rex the builder writes: none where S gave none or the builder writes none.
                if (statement.WeakeningOf(i).Rex is not RexNode weakened)
                    continue;

                if (beneath is null)
                {
                    var applied = AlreadyApplied(filter.getInput());
                    beneath = [.. applied.Own, .. applied.Over];
                    across = applied.Across;
                }

                if (own.Contains(weakened) || statement.IsOwn(weakened) || statement.IsApplied(weakened, beneath, across))
                    continue;

                // Of it, what the filter it is pushed in admits through S: a member that filter declined would stay in
                // process beneath the conjunct it weakens, where it restricts nothing that conjunct does not, and where
                // Calcite has no body for a Cosmos type test nor an order between a raw value's kinds (h15).
                var kept = admit ? Admitted(weakened, held, beneath, across, fields, readings, container, rexBuilder) : new List<RexNode> { weakened };

                // Nor where what is admitted of it is applied beneath, all of it: the filter left above is matched next and
                // asks the same weakening, which is not applied where only what was admitted of it was pushed, and pushed
                // again that would be under itself for ever (h15, measured as TryBindOutput recursing through the filters).
                if (kept.Count == 0 || (admit && kept.All(k => statement.IsApplied(k, beneath, across))))
                    continue;

                pushable.AddRange(kept);
                held.AddRange(kept);
                beneath.AddRange(kept);

                // The rendering check's third site, inert unless a test arms it: S's weakening as the printer writes it.
                if (Metadata.CosmosRenderingCapture.Armed)
                {
                    var parameters = new CosmosParameterList();
                    if (statement.PrintWeakening(i, parameters) is { } text)
                        Metadata.CosmosRenderingCapture.Observer?.Weakened(statement, i, text, parameters);
                }
            }

            // Which conjuncts as written the half pushed decides whole, read off the planning form conjunct by conjunct.
            if (admit)
            {
                var planned = Metadata.CosmosFactRewriter.PlannedConjuncts(filter.getCondition(), fields, container, CosmosImplementor.DefaultRootAlias, rexBuilder, readings);
                var decided = new HashSet<RexNode>(admitted);
                foreach (var form in planned)
                    sent.Add(Metadata.CosmosFilterStatement.ConjunctsOf(form).All(c => c.isAlwaysTrue() || decided.Contains(c)));
            }

            return (pushable, residual);
        }

        /// <summary>
        /// Returns the conjuncts of a weakening the filter it is pushed in admits through S, beside what that filter holds;
        /// of each it declines, S's weakening there in its turn, to a fixed point; and nothing else of it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The filter is the one <see cref="CosmosFilterRule"/> converts, read as that rule reads it: its condition lowered
        /// (<see cref="Metadata.CosmosFactRewriter"/>) and admitted stratum by stratum. So what is returned is what that
        /// rule accepts whole, and the filter split off reaches the service entire (h15). A weakening is S's form, but the
        /// Rex the builder writes of it is read back by the loader and proved again beneath, where S may decline a member:
        /// a <c>CASE</c> it proves no exact form of, under one set or one image.
        /// </para>
        /// <para>
        /// <b>What it declines is dropped, not kept in process.</b> Kept, it would sit beneath the conjunct it weakens, which
        /// implies it, and restrict no row that conjunct does not; and it is the service's form written as a Rex, which
        /// Calcite may not implement: a Cosmos type test has an in-process body only where a schema declares it, and an
        /// ordering of a raw value raises between two kinds. Measured on the partition gate over EN20260907, seven
        /// predicates failed so where Calcite answered. Dropped, the filter beneath restricts what S admits of it, which the
        /// residual implies, and the residual is rechecked above as before. S's weakening of a member declined there is
        /// asked in its stead, and so on, each asked once.
        /// </para>
        /// </remarks>
        static List<RexNode> Admitted(RexNode weakening, IReadOnlyList<RexNode> held, IReadOnlyList<RexNode> beneath, IReadOnlyList<Metadata.CosmosFilterStatement.BoundConjunct>? across,
            IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, Metadata.CosmosContainerMetadata container, RexBuilder rexBuilder)
        {
            var kept = new List<RexNode>();
            var asked = new HashSet<RexNode> { weakening };
            for (var pending = new List<RexNode> { weakening }; pending.Count > 0;)
            {
                var condition = Metadata.CosmosFactRewriter.Planned(
                    Compose(rexBuilder, [.. held, .. kept, .. pending]), fields, container, CosmosImplementor.DefaultRootAlias, rexBuilder, readings);
                var statement = Metadata.CosmosFilterStatement.Of(condition, fields, readings, container, rexBuilder);

                var next = new List<RexNode>();
                for (var k = 0; k < statement.Conjuncts.Count; k++)
                {
                    var conjunct = statement.Conjuncts[k];
                    if (held.Contains(conjunct) || kept.Contains(conjunct))
                        continue;

                    if (statement.IsAdmitted(k))
                        kept.Add(conjunct);
                    else if (statement.WeakeningOf(k).Rex is RexNode weaker && asked.Add(weaker) && statement.IsOwn(weaker) == false && statement.IsApplied(weaker, beneath, across) == false)
                        next.Add(weaker);
                }

                pending = next;
            }

            return kept;
        }

        /// <summary>
        /// Returns every conjunct any equivalent form of a subtree already applies, once each (<c>Own</c>); what a filter
        /// beneath a projection there applies, written over that projection's columns, over the subtree's own rows
        /// (<c>Over</c>); and what a filter beneath a projection applies, each conjunct beside the relation it reads and that
        /// relation's binding (<c>Across</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A weakening is derived from a conjunct that stays in the residual, and the residual is what
        /// the rule matches on next time round — so the weakening is asked whether these already apply
        /// it (<see cref="Metadata.CosmosFilterStatement.IsApplied"/>), or the same weakening would be
        /// pushed under itself for ever: measured, as a stack overflow with TryBindOutput recursing
        /// through the filters it had built. A weakening of more than one term lands as several
        /// conjuncts of the filter beneath, and is applied where all of them are.
        /// </para>
        /// <para>
        /// Every equivalent form, not the one the set happens to name: a Volcano input is a set of
        /// equivalent expressions, and which of them <c>getOriginal</c> or <c>getBest</c> returns
        /// depends on how far the planner has got. Reading the whole set is stable, because once a
        /// weakening has been pushed the filter carrying it stays in the set.
        /// </para>
        /// <para>
        /// <b>And beneath a projection.</b> Every row a projection returns is a row its input returned, so what a filter
        /// beneath it applies holds of the document behind each, read through the binding of the rows that filter reads.
        /// It is written over the input's ordinals, not this relation's, so it is kept apart from <c>Own</c>, which is
        /// compared by Calcite's equality over one row type: each conjunct beside the relation it reads, the projection's
        /// input, and that relation's binding, which the statement reads it through. Each projection met at the subtree or
        /// beneath a filter there is crossed once, and one met beneath it the same way with its own input; one set of
        /// what was seen keeps a graph finite, and reads a filter once, in <c>Own</c> where the first walk met it.
        /// </para>
        /// <para>
        /// <b>And over the projection's own columns.</b> Where the projection's columns carry everything a conjunct reads, it
        /// is also written over them, as Calcite writes a condition back over a projection, and holds of every row the
        /// projection returns, which the relation the projection is met in reads: it reads a column as a weakening over that
        /// relation's binding reads it. Read only over the projection's input, a typed or text column is its accessor there
        /// and a field here, two terms, and a weakening the transpose had pushed beneath was found not applied and pushed
        /// again: <c>NOT (t."n" &gt; 10) OR INITCAP(…) = 'Steel'</c> over a view's <c>RETURNING DOUBLE</c> column sent its
        /// weakening in two filters (<c>CosmosToClrCursorConverterTests.ShouldPushAWeakeningOverAViewsTypedColumnOnce</c>).
        /// Over this subtree's own rows it is <c>Over</c>, which the statement reads as it reads <c>Own</c>, through its own
        /// loading, so that one node the loader cannot read is one term above the projection and beneath it
        /// (<c>CosmosPlannerTests.AConjunctBeneathAViewWrittenOverItsColumnsIsReadAsTheStatementsOwn</c>); over the rows of a
        /// projection met beneath another, it is beside that relation in <c>Across</c>.
        /// </para>
        /// </remarks>
        internal static (IReadOnlyList<RexNode> Own, IReadOnlyList<RexNode> Over, IReadOnlyList<Metadata.CosmosFilterStatement.BoundConjunct> Across) AlreadyApplied(RelNode? node)
        {
            // Once each by Calcite's equality, which compares types where the digest prints none.
            var applied = new List<RexNode>();
            var once = new HashSet<RexNode>();
            var over = new List<RexNode>();
            var overOnce = new HashSet<RexNode>();
            var across = new List<Metadata.CosmosFilterStatement.BoundConjunct>();
            var projections = new List<Project>();
            var seen = Rel.CosmosPlanMembers.NewSeen();

            // Through a stack of filters as well as across a set's members: a filter keeps its input's
            // row type, so a conjunct applied anywhere down the stack is applied to this relation in
            // the same ordinals. Read from the top filter alone, a weakening pushed under a filter the
            // residual was later split from was pushed again above it — and a redundant filter, which
            // halves the estimated rows, was then the plan chosen (#210).
            void Collect(RelNode? candidate)
            {
                if (candidate is Project project)
                    projections.Add(project);

                if (candidate is not Filter filter || seen.Add(filter) == false)
                    return;

                foreach (var conjunct in Metadata.CosmosFilterStatement.ConjunctsOf(filter.getCondition()))
                    if (once.Add(conjunct))
                        applied.Add(conjunct);

                foreach (var member in Rel.CosmosPlanMembers.Of(filter.getInput()))
                    Collect(member);
            }

            // Every expression the node stands for, which this did by hand before CosmosPlanMembers
            // existed and is the pattern that class was named after: a conjunct applied in any member
            // is applied to the relation, and missing one re-pushes a weakening under itself.
            foreach (var member in Rel.CosmosPlanMembers.Of(node))
                Collect(member);

            // Beneath a projection, every conjunct the filters there apply, over the rows the projection reads.
            void Beneath(RelNode? candidate, RelNode relation, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings, List<RexNode> conjuncts)
            {
                if (candidate is Project project)
                    Cross(project, relation, fields, readings);

                if (candidate is not Filter filter || seen.Add(filter) == false)
                    return;

                conjuncts.AddRange(Metadata.CosmosFilterStatement.ConjunctsOf(filter.getCondition()));

                foreach (var member in Rel.CosmosPlanMembers.Of(filter.getInput()))
                    Beneath(member, relation, fields, readings, conjuncts);
            }

            // Each projection once, over its input's binding, one whose input binds nothing giving nothing: each conjunct beside
            // the relation it reads, and where the projection's columns carry everything it reads, written over them over the
            // rows the projection returns, which the relation it is met in reads: this subtree's own, read as Own is, or beside
            // that relation and its binding.
            void Cross(Project project, RelNode? outer, IReadOnlyList<CosmosPath?> outerFields, IReadOnlyList<CosmosReading> outerReadings)
            {
                var input = project.getInput();
                if (seen.Add(project) == false || CosmosImplementor.TryBindOutput(input, out var fields, out var readings, out _) == false)
                    return;

                var conjuncts = new List<RexNode>();
                foreach (var member in Rel.CosmosPlanMembers.Of(input))
                    Beneath(member, input, fields, readings, conjuncts);

                foreach (var conjunct in conjuncts)
                {
                    across.Add(new(conjunct, input, fields, readings));

                    var columns = new Columns(project, project.getCluster().getRexBuilder());
                    var carried = (RexNode)conjunct.accept(columns);
                    if (outer is null || columns.Unread)
                        continue;

                    if (ReferenceEquals(outer, node))
                    {
                        if (overOnce.Add(carried))
                            over.Add(carried);
                    }
                    else
                    {
                        across.Add(new(carried, outer, outerFields, outerReadings));
                    }
                }
            }

            if (projections.Count > 0)
            {
                var bound = CosmosImplementor.TryBindOutput(node, out var fields, out var readings, out _);
                foreach (var project in projections)
                    Cross(project, bound ? node : null, fields, readings);
            }

            return (applied, over, across);
        }

        /// <summary>
        /// Determines whether a filter over a subtree can be a <see cref="CosmosFilter"/>: some equivalent form of it is part
        /// of a statement already, or one a rule converts over a subtree that can be: this container's scan, a projection,
        /// a filter <see cref="CosmosFilterRule"/> converts whole, or a traversal <see cref="CosmosUnnestRule"/> converts.
        /// Given the half a split pushes, a projection carries it only where Calcite's transpose writes it beneath
        /// (<see cref="Transposed"/>), and the projection's input is asked of the half as written there; a filter's input is
        /// asked of the half as it stands.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The filter rule converts a filter only over an input that converts, so a filter it declines leaves every filter
        /// above it in Calcite; and a filter Calcite implements copies as one. Split in either, the pushed half was a filter
        /// Calcite evaluates, and a weakening in it the service's form written as a Rex:
        /// <c>WHERE JSON_VALUE(x."DOC", '$.n') = '1'</c> over a subquery's <c>CAST(JSON_VALUE(c."DOC", '$.body') AS INTEGER)
        /// = 1</c> failed with "cannot translate call IS_NUMBER" where Calcite answers, declared or not, where no host merges
        /// the two filters. So did one over a lateral, an <c>UNNEST</c> or a scalar sub-query beside such a cast, while any
        /// member that was no filter counted: the correlate moved the half pushed into its left side, beside the cast. Which
        /// rule converts a member is legality, the question each asks of it.
        /// </para>
        /// <para>
        /// <b>A half holding the service's form, over a projection no statement holds.</b> Such a projection reaches the
        /// service only by the transpose moving the half beneath it, and the transpose declines a projection holding a
        /// window, a condition holding a correlation, and one that read through the projection's expressions grows past its
        /// bloat, as a half holding an 80-way <c>LIKE</c> of a view's column beside a weakening does over a view that also
        /// computes <c>INITCAP</c>. A filter keeps its row type, so the half sits over its input as it sits over the filter,
        /// and a projection beneath a filter the filter rule converts is asked of the half too. Asked without the half, the
        /// projection counts wherever its input reaches, the transpose taken for granted, which is the cheap question
        /// <see cref="matches"/> asks before the half is built.
        /// </para>
        /// </remarks>
        /// <param name="convention">The convention whose statements the filter would be part of.</param>
        /// <param name="node">The subtree the filter sits over.</param>
        /// <param name="seen">The expressions already asked, which keeps a graph finite.</param>
        /// <param name="half">The condition of the filter split off, where it holds the service's form; <c>null</c> asks
        /// whether any filter over the subtree can reach the service.</param>
        internal static bool Reaches(CosmosConvention convention, RelNode? node, HashSet<RelNode> seen, RexNode? half = null)
        {
            foreach (var member in Rel.CosmosPlanMembers.Of(node))
            {
                if (seen.Add(member) == false)
                    continue;

                if (member.getConvention() is CosmosConvention)
                    return true;

                // A form Calcite implements says nothing of what the service can be handed: a set holds one beside its
                // logical forms once Volcano has converted any of them.
                if (member.getConvention() != Convention.NONE)
                    continue;

                var reaches = member switch
                {
                    TableScan scan => scan.getTable()?.unwrap(typeof(CosmosTable)) is CosmosTable table && ReferenceEquals(table.Convention, convention),
                    Filter filter => CosmosFilterRule.IsTranslatable(convention, filter) && Reaches(convention, filter.getInput(), seen, half),
                    Project project => half is null
                        ? Reaches(convention, project.getInput(), seen)
                        : Transposed(half, project) is RexNode beneath && Reaches(convention, project.getInput(), seen, beneath),
                    Correlate correlate => CosmosUnnestRule.IsTranslatable(convention, correlate) && Reaches(convention, correlate.getLeft(), seen),
                    _ => false,
                };

                if (reaches)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns a condition read through a projection's expressions as Calcite's <c>FILTER_PROJECT_TRANSPOSE</c> reads it,
        /// asked that rule's own conditions, or <c>null</c> where it declines: the projection holds a window, the condition a
        /// correlation, or the condition so read grows past the rule's bloat.
        /// </summary>
        /// <remarks>
        /// The rule the adapter registers (<see cref="CosmosRules"/>), its bloat read from its own configuration, so the
        /// answer is the one it gives itself when it matches the filter over the projection.
        /// </remarks>
        internal static RexNode? Transposed(RexNode condition, Project project)
        {
            if (project.containsOver() || RexUtil.containsCorrelation(condition))
                return null;

            var transpose = (org.apache.calcite.rel.rules.FilterProjectTransposeRule.Config)org.apache.calcite.rel.rules.CoreRules.FILTER_PROJECT_TRANSPOSE.config;
            return RelOptUtil.pushPastProjectUnlessBloat(condition, project, transpose.bloat());
        }

        static RexNode Compose(RexBuilder rexBuilder, List<RexNode> conjuncts)
        {
            var list = new java.util.ArrayList();
            foreach (var conjunct in conjuncts)
                list.add(conjunct);

            return RexUtil.composeConjunction(rexBuilder, list);
        }

    }

}
