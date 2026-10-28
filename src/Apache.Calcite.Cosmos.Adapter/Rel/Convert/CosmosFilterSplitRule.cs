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
    /// The split terminates. It fires only when both parts are non-empty, and neither of the two filters
    /// it produces has that property — the inner is wholly renderable, the outer wholly not.
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
        /// either way, and <see cref="Split"/> already binds through them.
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

            var (pushable, residual) = Split((Filter)call.rel(0), _convention.Container);

            return pushable.Count > 0 && residual.Count > 0;
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);

            var (pushable, residual) = Split(filter, _convention.Container);
            if (pushable.Count == 0 || residual.Count == 0)
                return;

            var rexBuilder = filter.getCluster().getRexBuilder();

            var inner = filter.copy(filter.getTraitSet(), filter.getInput(), Compose(rexBuilder, pushable));
            var outer = filter.copy(filter.getTraitSet(), inner, Compose(rexBuilder, residual));

            call.transformTo(outer);
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
        internal static (List<RexNode> Pushable, List<RexNode> Residual) Split(Filter filter, Metadata.CosmosContainerMetadata container)
        {
            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out var readings, out _) == false)
                return (new List<RexNode>(), new List<RexNode>());

            var rexBuilder = filter.getCluster().getRexBuilder();

            // No weakening over a projection. FILTER_PROJECT_TRANSPOSE gives every such filter an
            // equivalent beneath the projection, where its weakenings are derived against the filters
            // already applied. Derived here they are blind to those: a filter left over a pushed
            // projection is a residual whose weakenings are already below it, and pushing them again
            // put a redundant definedness test in the statement, or one a declared presence had
            // deleted (#210).
            var weakens = OverProjection(filter.getInput()) == false;

            // Lowered before it is split, and that order is the point: a fact is usually conditional,
            // and the conjunct that proves the condition is a sibling of the one being lowered. Split
            // first and each conjunct would be weighed on its own, with nothing left to prove the
            // guard from.
            var condition = Metadata.CosmosFactRewriter.Rewrite(
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
            // filters beneath apply, and the weakenings pushed before it. A weakening they, the admitted
            // conjuncts and the declaration already imply restricts nothing -- one beneath, applied by a
            // filter the residual was split from, would be pushed under itself for ever -- and is not pushed.
            List<RexNode>? beneath = null;

            for (var i = 0; i < statement.Conjuncts.Count; i++)
            {
                var conjunct = statement.Conjuncts[i];

                if (statement.IsAdmitted(i))
                {
                    pushable.Add(conjunct);
                    continue;
                }

                // Kept whatever happens: a weakening is a restriction the service can apply, not a
                // replacement for the predicate, so the original is still rechecked above.
                residual.Add(conjunct);

                if (weakens == false)
                    continue;

                // Written from what every pushed conjunct establishes: the stratum a conjunct no round
                // admitted is read in, which is never its own facts.
                var parameters = new CosmosParameterList();
                var translator = statement.TranslatorFor(i, parameters);

                // S's weakening of it, as the Rex it builds: none where S gave none or the builder writes none.
                if (statement.WeakeningOf(i).Rex is not RexNode weakened)
                    continue;

                beneath ??= AlreadyApplied(filter.getInput()).ToList();

                if (statement.IsApplied(weakened, beneath) == false && translator.TryTranslate(weakened, out var text))
                {
                    pushable.Add(weakened);
                    beneath.Add(weakened);

                    // The rendering check's third site, inert unless a test arms it.
                    if (Metadata.CosmosRenderingCapture.Armed)
                        Metadata.CosmosRenderingCapture.Observer?.Weakened(statement, i, text!, parameters);
                }
            }

            return (pushable, residual);
        }

        /// <summary>
        /// Returns every conjunct any equivalent form of a subtree already applies, once each.
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
        /// </remarks>
        static IReadOnlyList<RexNode> AlreadyApplied(RelNode? node)
        {
            // Once each by Calcite's equality, which compares types where the digest prints none.
            var applied = new List<RexNode>();
            var once = new HashSet<RexNode>();
            var seen = Rel.CosmosPlanMembers.NewSeen();

            // Through a stack of filters as well as across a set's members: a filter keeps its input's
            // row type, so a conjunct applied anywhere down the stack is applied to this relation in
            // the same ordinals. Read from the top filter alone, a weakening pushed under a filter the
            // residual was later split from was pushed again above it — and a redundant filter, which
            // halves the estimated rows, was then the plan chosen (#210).
            void Collect(RelNode? candidate)
            {
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

            return applied;
        }

        /// <summary>
        /// Determines whether any equivalent form of a subtree is a projection.
        /// </summary>
        static bool OverProjection(RelNode? node)
        {
            foreach (var member in Rel.CosmosPlanMembers.Of(node))
                if (member is Project)
                    return true;

            return false;
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
