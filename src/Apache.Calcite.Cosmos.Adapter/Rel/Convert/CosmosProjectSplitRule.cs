using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Splits a projection whose expressions are partly renderable into the columns Cosmos can produce
    /// and the columns it cannot, so that the service projects what it is able to instead of nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SELECT a, b</c>, where <c>a</c> renders and <c>b</c> does not, becomes a projection of
    /// <c>b</c> over a projection of <c>a</c>. The inner one converts to a <see cref="CosmosProject"/>
    /// and reaches the service; the outer stays in Calcite and computes what is left. Without this the
    /// whole projection is declined and every document crosses the wire whole — to compute, in the
    /// case that prompted this, a constant timestamp (#125).
    /// </para>
    /// <para>
    /// <b>Why it is sound.</b> A projection has no predicate and discards no rows, so the two halves
    /// compose by construction: the inner produces a column per pushable expression, and the outer
    /// names those columns and evaluates the rest. The outer carries the original row type, so what
    /// the caller sees — names, types, order — is unchanged. Nothing here reasons about what an
    /// expression <em>means</em>, only about where it can be evaluated.
    /// </para>
    /// <para>
    /// <b>The inner half carries what the outer needs.</b> A residual expression reads its operands
    /// from the input, and the outer sits above the inner rather than above the input, so every input
    /// the residual references is projected too and the references are rewritten to find it. That is
    /// what makes the split general rather than a special case for expressions over constants.
    /// </para>
    /// <para>
    /// <b>The split is at the top level only.</b> <c>CAST(JSON_VALUE(DOC, '$.x') AS DOUBLE)</c> is one
    /// residual expression, and the whole of it is computed above — the accessor inside it is not
    /// pushed on its own, which would be a finer split than this makes. What the residual needs is
    /// therefore <c>DOC</c>, and the document crosses the wire for that column. Correctness is
    /// restored either way; the finer split would also save the bytes, and is not attempted here.
    /// </para>
    /// <para>
    /// <b>It terminates,</b> and the guard is that a pushable expression must be more than a bare
    /// reference. The outer projection this produces is exactly that — references to the pushed
    /// columns beside the residual expressions — so it cannot match again. Without the guard a
    /// projection of one reference and one residual would split into the same shape forever.
    /// </para>
    /// </remarks>
    public class CosmosProjectSplitRule : RelOptRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits projections over.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosProjectSplitRule Create(CosmosConvention convention)
        {
            return new CosmosProjectSplitRule(convention);
        }

        readonly CosmosConvention _convention;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits projections over.</param>
        // Deprecated operand builders, for the reason CosmosFilterSplitRule gives: the Config operand
        // supplier costs more ceremony from C# than it buys, and the API is unchanged.
#pragma warning disable CS0612
        public CosmosProjectSplitRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(Project), any()),
                "CosmosProjectSplitRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            var project = (Project)call.rel(0);

            // Scoped to this convention's own container, as the filter split is: a rule set is
            // registered per container and somebody else's projection is not this rule's business.
            if (CosmosFilterSplitRule.FindTable(project.getInput()) is not CosmosTable table || ReferenceEquals(table.Convention, _convention) == false)
                return false;

            // The gates the all-or-nothing rule applies to the whole projection apply to any part of
            // it, so a projection it would refuse outright is refused here before anything is split.
            if (project.getVariablesSet().isEmpty() == false || project.containsOver())
                return false;

            return TrySplit(project, out _, out _);
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var project = (Project)call.rel(0);

            if (TrySplit(project, out var pushable, out var residual) == false)
                return;

            var rexBuilder = project.getCluster().getRexBuilder();
            var projects = project.getProjects();
            var input = project.getInput();

            // The inner projection: every pushable expression, then every input the residual reads.
            var inner = new java.util.ArrayList();
            var ordinalOfPushable = new Dictionary<int, int>();
            var ordinalOfInput = new Dictionary<int, int>();

            foreach (var i in pushable)
            {
                ordinalOfPushable[i] = inner.size();
                inner.add(projects.get(i));
            }

            foreach (var index in InputsOf(projects, residual))
            {
                if (ordinalOfInput.ContainsKey(index))
                    continue;

                ordinalOfInput[index] = inner.size();
                inner.add(rexBuilder.makeInputRef(input, index));
            }

            var lower = LogicalProject.create(input, com.google.common.collect.ImmutableList.of(), inner, (java.util.List?)null, com.google.common.collect.ImmutableSet.of());

            // The outer projection: a reference for each pushed column, and each residual expression
            // with its references moved onto the inner projection's output.
            var shuttle = new Rebase(rexBuilder, ordinalOfInput);
            var outer = new java.util.ArrayList();

            for (var i = 0; i < projects.size(); i++)
            {
                var expression = (RexNode)projects.get(i);

                outer.add(ordinalOfPushable.TryGetValue(i, out var pushed)
                    ? rexBuilder.makeInputRef(lower, pushed)
                    : expression.accept(shuttle));
            }

            call.transformTo(project.copy(project.getTraitSet(), lower, outer, project.getRowType()));
        }

        /// <summary>
        /// Partitions a projection's expressions by whether each renders as Cosmos SQL.
        /// </summary>
        /// <remarks>
        /// Tested against the binding derived by walking the input — the same one
        /// <see cref="CosmosProjectRule"/> and <see cref="CosmosProject.Implement"/> use — so an
        /// expression this calls pushable is one that rule would accept, and the inner projection it
        /// builds converts rather than failing at implementation.
        /// </remarks>
        /// <param name="project">The projection.</param>
        /// <param name="pushable">On success, the ordinals that render.</param>
        /// <param name="residual">On success, the ordinals that do not.</param>
        /// <returns><c>true</c> where the split is worth making.</returns>
        bool TrySplit(Project project, out List<int> pushable, out List<int> residual)
        {
            pushable = new List<int>();
            residual = new List<int>();

            var projects = project.getProjects();
            if (projects.size() == 0)
                return false;

            if (CosmosImplementor.TryBindOutput(project.getInput(), out var fields, out var written) == false)
                return false;

            // A statement has one SELECT and Cosmos has no derived table to nest a second in, so a
            // subtree that already wrote one cannot take a projection at all, split or whole.
            if ((written & CosmosClauses.Projection) != 0)
                return false;

            var facts = _convention.Container?.Facts.Derive(null) ?? Metadata.CosmosFactSet.Empty;
            var translator = new CosmosRexTranslator(project.getCluster().getRexBuilder(), fields, new CosmosParameterList(), null, _convention.Container, null, facts);

            var nontrivial = false;

            for (var i = 0; i < projects.size(); i++)
            {
                var expression = (RexNode)projects.get(i);

                if (translator.TryTranslateProjection(expression, out _, out _))
                {
                    pushable.Add(i);

                    // See the remarks on termination: a split whose pushable half is nothing but bare
                    // references produces an outer projection of exactly that shape, which would match
                    // again.
                    nontrivial |= expression is not RexInputRef;
                }
                else
                {
                    residual.Add(i);
                }
            }

            return pushable.Count > 0 && residual.Count > 0 && nontrivial;
        }

        /// <summary>
        /// Returns the input ordinals the residual expressions read.
        /// </summary>
        static IEnumerable<int> InputsOf(java.util.List projects, List<int> residual)
        {
            var found = new SortedSet<int>();

            foreach (var i in residual)
            {
                var bits = RelOptUtil.InputFinder.bits((RexNode)projects.get(i));

                for (var bit = bits.nextSetBit(0); bit >= 0; bit = bits.nextSetBit(bit + 1))
                    found.Add(bit);
            }

            return found;
        }

        /// <summary>
        /// Moves an expression's input references onto the inner projection's output.
        /// </summary>
        sealed class Rebase : RexShuttle
        {

            readonly RexBuilder _rexBuilder;
            readonly Dictionary<int, int> _ordinals;

            public Rebase(RexBuilder rexBuilder, Dictionary<int, int> ordinals)
            {
                _rexBuilder = rexBuilder;
                _ordinals = ordinals;
            }

            public override RexNode visitInputRef(RexInputRef inputRef)
            {
                return _ordinals.TryGetValue(inputRef.getIndex(), out var ordinal)
                    ? _rexBuilder.makeInputRef(inputRef.getType(), ordinal)
                    : inputRef;
            }

        }

    }

}
