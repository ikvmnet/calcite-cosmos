using System;
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
    /// <b>The split goes inside an expression as well as between them.</b>
    /// <c>CAST(JSON_VALUE(DOC, '$.x') AS DOUBLE)</c> does not render — the cast converts where the
    /// service would not — but the accessor inside it renders perfectly well. So the residual is
    /// walked for the <em>maximal</em> sub-expressions that translate, each is projected as a column of
    /// its own, and the residual is rewritten to read them. What used to cross the wire for that column
    /// was the whole document; what crosses now is one scalar.
    /// </para>
    /// <para>
    /// <b>Maximal is the whole of the discipline.</b> The walk stops at the first node that renders
    /// rather than descending past it, because a deeper split would push <c>c.x</c> and compute an
    /// accessor above it that the service was willing to answer. Top-down, first success wins.
    /// </para>
    /// <para>
    /// <b>What the residual still reads is collected by the same walk,</b> and that is what saves the
    /// bytes rather than merely moving the work. An input reached inside a pushed sub-expression is
    /// <em>not</em> needed above — the column supplies it — so the walk records an input only where it
    /// reaches one outside every pushed fragment. Collecting them from the original expression instead
    /// would project <c>DOC</c> beside the scalar extracted from it and leave the document on the wire.
    /// </para>
    /// <para>
    /// <b>A bare reference and a literal are never fragments.</b> The reference is already available to
    /// the outer projection and the literal costs nothing to compute there, so pushing either buys a
    /// column and no work — and a projection of nothing but references is the shape this rule produces,
    /// which is what it must not match again.
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

            return TrySplit(project, out _);
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var project = (Project)call.rel(0);

            if (TrySplit(project, out var split) == false)
                return;

            var rexBuilder = project.getCluster().getRexBuilder();
            var projects = project.getProjects();
            var input = project.getInput();

            // The inner projection, in three parts: every whole expression that renders, every maximal
            // sub-expression found inside one that does not, and every input the residual still reads
            // past those.
            var inner = new java.util.ArrayList();
            var ordinalOfPushable = new Dictionary<int, int>();
            var ordinalOfFragment = new Dictionary<string, int>(StringComparer.Ordinal);
            var ordinalOfInput = new Dictionary<int, int>();

            foreach (var i in split.Pushable)
            {
                ordinalOfPushable[i] = inner.size();
                inner.add(projects.get(i));
            }

            foreach (var fragment in split.Fragments)
            {
                ordinalOfFragment[fragment.ToString()] = inner.size();
                inner.add(fragment);
            }

            foreach (var index in split.Inputs)
            {
                ordinalOfInput[index] = inner.size();
                inner.add(rexBuilder.makeInputRef(input, index));
            }

            var lower = LogicalProject.create(input, com.google.common.collect.ImmutableList.of(), inner, (java.util.List?)null, com.google.common.collect.ImmutableSet.of());

            // The outer projection: a reference for each pushed column, and each residual expression
            // with its pushed sub-expressions and its remaining references moved onto the inner
            // projection's output.
            var shuttle = new Rebase(rexBuilder, ordinalOfInput, ordinalOfFragment);
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
        /// Partitions a projection into what the service can produce and what is left to compute above
        /// it, looking inside an expression as well as between them.
        /// </summary>
        /// <remarks>
        /// Tested against the binding derived by walking the input — the same one
        /// <see cref="CosmosProjectRule"/> and <see cref="CosmosProject.Implement"/> use — so an
        /// expression this calls pushable is one that rule would accept, and the inner projection it
        /// builds converts rather than failing at implementation. A sub-expression is held to exactly
        /// the same test, which is what makes a fragment a column the same machinery renders and reads.
        /// </remarks>
        /// <param name="project">The projection.</param>
        /// <param name="split">On success, what goes below and what stays above.</param>
        /// <returns><c>true</c> where the split is worth making.</returns>
        bool TrySplit(Project project, out ProjectionSplit split)
        {
            split = new ProjectionSplit();

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

            for (var i = 0; i < projects.size(); i++)
            {
                var expression = (RexNode)projects.get(i);

                if (translator.TryTranslateProjection(expression, out _, out _))
                {
                    split.Pushable.Add(i);

                    // See the remarks on termination: a split whose pushed half is nothing but bare
                    // references produces an outer projection of exactly that shape, which would match
                    // again.
                    split.Nontrivial |= expression is not RexInputRef;
                    continue;
                }

                split.Residual.Add(i);
                Collect(translator, expression, split);
            }

            return split.Residual.Count > 0
                && (split.Pushable.Count > 0 || split.Fragments.Count > 0)
                && split.Nontrivial;
        }

        /// <summary>
        /// Walks a residual expression, recording the maximal sub-expressions that render and the
        /// inputs it still reads past them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Top-down, and the first success wins.</b> A node that renders is taken whole and its
        /// operands are not visited, which is what <em>maximal</em> means: descending past it would
        /// push a path and compute an accessor over it that the service was willing to answer.
        /// </para>
        /// <para>
        /// <b>An input is recorded only where the walk reaches one.</b> Inside a fragment it is not
        /// reached, because the fragment is not descended into — and that is deliberate rather than
        /// incidental: the pushed column supplies what the residual needs, so projecting the input
        /// beside it would put the document back on the wire and leave the split saving nothing but
        /// the arithmetic.
        /// </para>
        /// <para>
        /// A node that is neither a call nor a reference — a field access over a correlation variable,
        /// say — is left to the residual whole, and whatever it reads is swept for by
        /// <see cref="InputsOf"/>. Nothing of that kind reaches here today, <see cref="matches"/>
        /// refusing a projection that carries variables, but the sweep is what makes the walk total
        /// rather than exhaustive over the shapes that happen to exist.
        /// </para>
        /// </remarks>
        static void Collect(CosmosRexTranslator translator, RexNode node, ProjectionSplit split)
        {
            if (node is RexInputRef inputRef)
            {
                split.Inputs.Add(inputRef.getIndex());
                return;
            }

            // A literal is computed above for nothing. Tested before renderability because a literal
            // renders perfectly well and would otherwise become a column carrying a constant.
            if (node is RexLiteral)
                return;

            if (translator.TryTranslateProjection(node, out _, out _))
            {
                var digest = node.ToString();

                if (split.Index.ContainsKey(digest) == false)
                {
                    split.Index[digest] = split.Fragments.Count;
                    split.Fragments.Add(node);
                }

                split.Nontrivial = true;
                return;
            }

            if (node is RexCall call)
            {
                var operands = call.getOperands();

                for (var i = 0; i < operands.size(); i++)
                    Collect(translator, (RexNode)operands.get(i), split);

                return;
            }

            foreach (var index in InputsOf(node))
                split.Inputs.Add(index);
        }

        /// <summary>
        /// What a split decided: the ordinals that go below whole, the ordinals that stay above, the
        /// sub-expressions lifted out of those, and the inputs still read past them.
        /// </summary>
        sealed class ProjectionSplit
        {

            /// <summary>The projection ordinals that render whole.</summary>
            public List<int> Pushable { get; } = new();

            /// <summary>The projection ordinals computed above.</summary>
            public List<int> Residual { get; } = new();

            /// <summary>The maximal renderable sub-expressions, in the order they were found.</summary>
            public List<RexNode> Fragments { get; } = new();

            /// <summary>
            /// Each fragment's digest, so that the same sub-expression written twice becomes one column.
            /// </summary>
            /// <remarks>
            /// Keyed by <c>ToString</c> rather than by the node, because a <see cref="RexNode"/>'s
            /// equality is Java's and the dictionary's is not — and the digest is what Calcite itself
            /// compares nodes by.
            /// </remarks>
            public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);

            /// <summary>The input ordinals the residual reads outside every fragment.</summary>
            public SortedSet<int> Inputs { get; } = new();

            /// <summary>Whether anything more than a bare reference goes below.</summary>
            public bool Nontrivial { get; set; }

        }

        /// <summary>
        /// Returns the input ordinals an expression reads.
        /// </summary>
        static IEnumerable<int> InputsOf(RexNode node)
        {
            var found = new SortedSet<int>();
            var bits = RelOptUtil.InputFinder.bits(node);

            for (var bit = bits.nextSetBit(0); bit >= 0; bit = bits.nextSetBit(bit + 1))
                found.Add(bit);

            return found;
        }

        /// <summary>
        /// Moves an expression onto the inner projection's output: a pushed sub-expression becomes the
        /// column that now holds it, and a reference the residual still reads becomes its new ordinal.
        /// </summary>
        /// <remarks>
        /// The fragment test runs in <c>visitCall</c> <em>before</em> the base implementation, which is
        /// what keeps the substitution maximal: recursing first would rewrite the operands of a node
        /// that is about to be replaced whole, and the replacement would then no longer match what was
        /// pushed.
        /// </remarks>
        sealed class Rebase : RexShuttle
        {

            readonly RexBuilder _rexBuilder;
            readonly Dictionary<int, int> _ordinals;
            readonly Dictionary<string, int> _fragments;

            public Rebase(RexBuilder rexBuilder, Dictionary<int, int> ordinals, Dictionary<string, int> fragments)
            {
                _rexBuilder = rexBuilder;
                _ordinals = ordinals;
                _fragments = fragments;
            }

            public override RexNode visitCall(RexCall call)
            {
                return _fragments.TryGetValue(call.ToString(), out var ordinal)
                    ? _rexBuilder.makeInputRef(call.getType(), ordinal)
                    : base.visitCall(call);
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
