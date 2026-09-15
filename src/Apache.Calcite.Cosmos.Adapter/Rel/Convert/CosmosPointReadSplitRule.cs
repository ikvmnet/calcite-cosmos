using System.Collections.Generic;

using org.apache.calcite.plan;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Offers, as an alternative, the plan in which a by-id lookup carrying a residual is answered by a
    /// point read with the residual applied above the projection, instead of by a query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WHERE id = 'X' AND pk = 'Y' AND deleteUtcTime IS NULL</c> — every by-id lookup through a
    /// soft-delete view — pins a complete point read and then says one thing more.
    /// <see cref="Metadata.CosmosPartitionKeyExtractor.TryExtractPointRead"/> declines it, because a
    /// point read applies no predicate of its own and would return a document the query excludes. That
    /// is right, and this rule does not weaken it. It rewrites the plan so that the standard is met:
    /// </para>
    /// <code>
    /// Filter(deleteUtcTime IS NULL)          ← lifted above the projection
    ///   Project(…)
    ///     Filter(id = 'X' AND pk = 'Y')      ← says nothing else, so the point read is recovered
    /// </code>
    /// <para>
    /// The inner filter is exactly the pinned equalities, so <c>TryExtractPointRead</c> accepts it
    /// unchanged; the residual becomes an ordinary Calcite filter, evaluated in process. That last point
    /// is what makes this safe rather than merely cheaper: issue #92 notes that a residual applied after
    /// a read must keep the semantics the query path would give it — three-valued logic, absent against
    /// null on a document path — and here it is Calcite applying its own predicate to rows, which is the
    /// semantics by construction. No evaluator is written, so none can disagree.
    /// </para>
    /// <para>
    /// <b>Why it matches the projection and not the filter alone.</b> A residual left directly above the
    /// pushed filter is still underneath the projection, and a Cosmos projection requires a Cosmos
    /// input, so the only convention the residual can be implemented in is the one it was split out of —
    /// whereupon it merges straight back and the split buys nothing. Lifted above the projection it sits
    /// where the plan is free to leave the Cosmos convention, which is the whole of what makes it a
    /// partial pushdown. The lift is why the rule can decline: it fires only where every field the
    /// residual reads is projected, so the predicate can be rewritten against the projection's output
    /// without inventing a column.
    /// </para>
    /// <para>
    /// <b>What it will not lift.</b> A residual naming a Cosmos function — <c>IS_DEFINED</c> and the rest
    /// of <see cref="Sql.CosmosOperators"/> — stays with the service. Those are declared so a query can
    /// name them and rendered into Cosmos SQL; none has a CLR implementation, so a plan holding one above
    /// the converter is cheaper on paper and impossible in fact, and Calcite says so only at code
    /// generation with <c>Unable to implement</c>. The sibling rule never meets this because everything
    /// it lifts is, by construction, something Cosmos could not render in the first place.
    /// </para>
    /// <para>
    /// <b>Why it is sound.</b> The same argument as <see cref="CosmosFilterSplitRule"/>: the pushed
    /// condition is a subset of the original's top-level conjuncts, so the original implies it, and a
    /// weaker filter discards only rows the original would have discarded too. The lifted filter — the
    /// remaining conjuncts, rewritten to read the same values through the projection — restores exactly
    /// the original result. Top-level conjuncts are the case where polarity needs no analysis.
    /// </para>
    /// <para>
    /// <b>Why it is only an offer.</b> <c>transformTo</c> registers an alternative; the planner keeps
    /// both and takes the cheaper. Which is cheaper is a real question rather than a rhetorical one, and
    /// not the same answer twice: measured against a real account, a point read beats the query it
    /// replaces threefold for a small document and loses to it threefold for one of 100 KB, because a
    /// read is charged for the body it returns at roughly six times a query's rate and has no floor to
    /// save once that body dominates. <see cref="Metadata.CosmosRequestUnitModel"/> carries those
    /// coefficients and <see cref="CosmosFilter"/> prices the plans with them, so the decision is made
    /// per container on what the service actually charges.
    /// </para>
    /// <para>
    /// <b>It does not offer the batch.</b> <c>TryExtractPointReadSet</c>'s shape stays strict. The same
    /// measurement found the batch read losing to a single query at every set size above one — a batch
    /// pays per document where a query does not, and by 128 ids the query is over fourfold cheaper — so
    /// splitting a residual out of an id set would buy a worse plan. A set of one is this rule's case
    /// already.
    /// </para>
    /// <para>
    /// The split terminates. It fires only where both halves are non-empty, and the projection it
    /// produces sits over a filter that is wholly pinned equalities, which splits into an empty residual.
    /// </para>
    /// </remarks>
    public class CosmosPointReadSplitRule : RelOptRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits filters over.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosPointReadSplitRule Create(CosmosConvention convention)
        {
            return new CosmosPointReadSplitRule(convention);
        }

        readonly CosmosConvention _convention;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits filters over.</param>
        // As CosmosFilterSplitRule: RelOptRule's operand builders are deprecated in favour of
        // RelRule.Config, whose operand supplier costs more ceremony from C# than it buys.
#pragma warning disable CS0612
        public CosmosPointReadSplitRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(Project), operand((java.lang.Class)typeof(Filter), any())),
                "CosmosPointReadSplitRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <summary>
        /// Partitions a filter's top-level conjuncts into those a point read accounts for and the rest.
        /// </summary>
        /// <remarks>
        /// The classification is the extractor's own — see
        /// <see cref="Metadata.CosmosPartitionKeyExtractor.IsPointReadConjunct"/> — so a conjunct this
        /// puts in the pinned half is one the recovery would accept, and the two cannot drift apart.
        /// </remarks>
        static (List<RexNode> Pinned, List<RexNode> Residual) Split(Filter filter, Metadata.CosmosContainerMetadata container)
        {
            var pinned = new List<RexNode>();
            var residual = new List<RexNode>();

            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out _) == false)
                return (pinned, residual);

            var conjuncts = RelOptUtil.conjunctions(filter.getCondition());

            for (var i = 0; i < conjuncts.size(); i++)
            {
                var conjunct = (RexNode)conjuncts.get(i);

                if (Metadata.CosmosPartitionKeyExtractor.IsPointReadConjunct(conjunct, fields, container, CosmosImplementor.DefaultRootAlias))
                    pinned.Add(conjunct);
                else
                    residual.Add(conjunct);
            }

            return (pinned, residual);
        }

        /// <summary>
        /// Determines whether the pinned half is a complete point read on its own.
        /// </summary>
        /// <remarks>
        /// Conjuncts that pin <em>some</em> of the partition key, or the key without an <c>id</c>, are
        /// not a point read and splitting them out buys a weaker pushed predicate for nothing. The
        /// question is asked of the composed half rather than inferred from the count, because
        /// completeness is the extractor's to judge.
        /// </remarks>
        static bool IsCompletePointRead(Filter filter, List<RexNode> pinned, Metadata.CosmosContainerMetadata container)
        {
            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out _) == false)
                return false;

            var composed = Compose(filter.getCluster().getRexBuilder(), pinned);

            return Metadata.CosmosPartitionKeyExtractor.TryExtractPointRead(
                composed, fields, container, CosmosImplementor.DefaultRootAlias, out _, out _);
        }

        /// <summary>
        /// Maps each of the projection's input ordinals to the output ordinal that carries it unchanged.
        /// </summary>
        /// <remarks>
        /// Only bare column references are mapped. A projection computing something from a column does
        /// not let a predicate over that column be rewritten against its output, and guessing otherwise
        /// would change what the predicate reads.
        /// </remarks>
        static Dictionary<int, int> OutputOrdinals(Project project)
        {
            var map = new Dictionary<int, int>();
            var projects = project.getProjects();

            for (var i = 0; i < projects.size(); i++)
                if ((RexNode)projects.get(i) is RexInputRef reference && map.ContainsKey(reference.getIndex()) == false)
                    map[reference.getIndex()] = i;

            return map;
        }

        /// <summary>
        /// Rewrites a predicate over a projection's input into one over its output, or returns
        /// <c>null</c> where a field it reads is not projected.
        /// </summary>
        sealed class Lift : RexShuttle
        {

            readonly Dictionary<int, int> _ordinals;
            readonly RexBuilder _rexBuilder;

            public Lift(Dictionary<int, int> ordinals, RexBuilder rexBuilder)
            {
                _ordinals = ordinals;
                _rexBuilder = rexBuilder;
            }

            /// <summary>Set where a field the predicate reads is not carried by the projection.</summary>
            public bool Blocked { get; private set; }

            public override RexNode visitInputRef(RexInputRef inputRef)
            {
                if (_ordinals.TryGetValue(inputRef.getIndex(), out var ordinal))
                    return _rexBuilder.makeInputRef(inputRef.getType(), ordinal);

                Blocked = true;
                return inputRef;
            }

        }

        static RexNode? TryLift(RexNode condition, Project project)
        {
            var lift = new Lift(OutputOrdinals(project), project.getCluster().getRexBuilder());
            var lifted = (RexNode)condition.accept(lift);

            return lift.Blocked ? null : lifted;
        }

        /// <summary>
        /// Recovers the split this rule would make, or <c>null</c> where it would not make one.
        /// </summary>
        (Project Project, Filter Filter, RexNode Pinned, RexNode Lifted)? Recognise(RelOptRuleCall call)
        {
            var project = (Project)call.rel(0);
            var filter = (Filter)call.rel(1);

            // Scoped to this convention's own container, as the sibling rule is: a filter over somebody
            // else's table is not this rule's business.
            if (CosmosFilterSplitRule.FindTable(filter.getInput()) is not CosmosTable table ||
                ReferenceEquals(table.Convention, _convention) == false)
                return null;

            var (pinned, residual) = Split(filter, _convention.Container);
            if (pinned.Count == 0 || residual.Count == 0)
                return null;

            if (IsCompletePointRead(filter, pinned, _convention.Container) == false)
                return null;

            // A residual is only worth holding back if something outside the convention can evaluate it.
            // The Cosmos functions are declared for the validator and rendered into Cosmos SQL; none has
            // a CLR implementation, so a plan leaving one above the converter is cheaper on paper and
            // cannot be generated at all. The sibling rule never meets this, because everything it lifts
            // is by definition something Cosmos could not render.
            foreach (var conjunct in residual)
                if (Sql.CosmosOperators.ReferencesServiceOnlyFunction(conjunct))
                    return null;

            var rexBuilder = filter.getCluster().getRexBuilder();

            if (TryLift(Compose(rexBuilder, residual), project) is not RexNode lifted)
                return null;

            return (project, filter, Compose(rexBuilder, pinned), lifted);
        }

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            return Recognise(call) is not null;
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            if (Recognise(call) is not var (project, filter, pinned, lifted) || project is null)
                return;

            var inner = filter.copy(filter.getTraitSet(), filter.getInput(), pinned);
            var projected = project.copy(project.getTraitSet(), inner, project.getProjects(), project.getRowType());

            call.transformTo(LogicalFilter.create(projected, lifted));
        }

        /// <summary>
        /// Conjoins a list of conjuncts back into one condition.
        /// </summary>
        static RexNode Compose(RexBuilder rexBuilder, List<RexNode> conjuncts)
        {
            var list = new java.util.ArrayList();
            foreach (var conjunct in conjuncts)
                list.add(conjunct);

            return RexUtil.composeConjunction(rexBuilder, list);
        }

    }

}
