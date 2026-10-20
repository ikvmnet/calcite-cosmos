using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Moves a projection below a filter that stays in process, carrying whatever the filter reads, so
    /// that the projection reaches the service rather than the whole document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where it comes from.</b> <see cref="CosmosFilterSplitRule"/> leaves a conjunct that does not
    /// render as a filter above the one that does, and the query's projection is above both. One
    /// statement has one <c>SELECT</c>, and it sits under the residual filter or nowhere — so the
    /// residual was a wall, the projection stayed above it, and the statement selected the document.
    /// A two-column query with one unrenderable conjunct read every matching document whole, where the
    /// same query with every conjunct rendered selected two values (#210). It is #145's symptom by
    /// another route.
    /// </para>
    /// <para>
    /// <b>What moves below.</b> <c>Project(p) ⟵ Filter(r) ⟵ x</c> becomes
    /// <c>Project(p') ⟵ Filter(r') ⟵ Project(q) ⟵ x</c>, where <c>q</c> is what
    /// <see cref="CosmosProjectSplitRule"/> would push for <c>p</c> and <c>r</c> together: every
    /// expression of <c>p</c> that renders whole, every maximal sub-expression that renders inside one
    /// that does not — of <c>p</c> or of <c>r</c>. <c>p'</c> and <c>r'</c> are the originals rebased
    /// onto <c>q</c>. Usually the residual reads a path the projection already selects, and nothing is
    /// added for it. Where either still reads the document itself past those, nothing is moved: the
    /// statement would select the document anyway.
    /// </para>
    /// <para>
    /// <b>Why it is sound.</b> A filter and a projection commute when the projection carries every
    /// value the filter reads, and a pushed sub-expression is a value: the projection rule renders it
    /// to answer exactly what Calcite computes in process, which is what lets it be pushed at all. The
    /// filter then reads the same values from a column as it read from the document.
    /// </para>
    /// <para>
    /// <b>Except where a value is refused.</b> They commute in what they answer, not in what they raise:
    /// beneath the filter, the projection is read for every row the service sends, and Calcite computes
    /// it only for the rows the filter keeps. A reading that can refuse a value —
    /// <see cref="CosmosJson.CanRefuse(org.apache.calcite.rel.type.RelDataType)"/>: 2^63 or 42.5 as a
    /// <c>BIGINT</c>, text that is no date — then failed the statement over a row the filter would have
    /// dropped, where Calcite answers, which the error refinement order never allows. The ORDER BY
    /// yardstick found it on a service that prints a stored 2^63 as <c>9.223372036854776E+18</c>, under
    /// <c>LOWER</c> over a string nothing proves ASCII. So a projection holding such a reading stays
    /// above, and the statement selects the document as it did before #210. The filter's own paths are
    /// still carried: Calcite reads them for every row the filter tests.
    /// </para>
    /// <para>
    /// <b>Why it terminates.</b> The filter it produces sits over a projection, and a subtree that has
    /// written its projection takes no other — so the filter it would match next is refused.
    /// </para>
    /// <para>
    /// A transformation adds an equivalence rather than replacing one. The plan selecting the document
    /// survives, and <see cref="CosmosToClrCursorConverter"/> weighs what each returns.
    /// </para>
    /// </remarks>
    public class CosmosProjectResidualRule : RelOptRule
    {

        /// <summary>
        /// Creates the rule for a convention.
        /// </summary>
        /// <param name="convention">The convention whose container the rule is scoped to.</param>
        /// <returns>The rule.</returns>
        public static CosmosProjectResidualRule Create(CosmosConvention convention)
        {
            return new CosmosProjectResidualRule(convention);
        }

        readonly CosmosConvention _convention;

        // Deprecated operand builders, for the reason CosmosFilterSplitRule gives: the Config operand
        // supplier costs more ceremony from C# than it buys, and the API is unchanged.
#pragma warning disable CS0612
        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The convention whose container the rule is scoped to.</param>
        public CosmosProjectResidualRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(LogicalProject), operand((java.lang.Class)typeof(LogicalFilter), any())),
                "CosmosProjectResidualRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            var project = (Project)call.rel(0);
            var filter = (Filter)call.rel(1);

            if (CosmosFilterSplitRule.FindTable(filter.getInput()) is not CosmosTable table || ReferenceEquals(table.Convention, _convention) == false)
                return false;

            if (project.getVariablesSet().isEmpty() == false || project.containsOver() || filter.getVariablesSet().isEmpty() == false)
                return false;

            return TrySplit(project, filter, out _);
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var project = (Project)call.rel(0);
            var filter = (Filter)call.rel(1);

            if (TrySplit(project, filter, out var split) == false)
                return;

            var rexBuilder = project.getCluster().getRexBuilder();
            var projects = split.Projects;
            var input = filter.getInput();

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
                // A fragment the projection already pushes whole is that column, not a second one.
                var digest = fragment.ToString();
                var whole = split.Pushable.FindIndex(i => ((RexNode)projects.get(i)).ToString() == digest);

                if (whole >= 0)
                {
                    ordinalOfFragment[digest] = ordinalOfPushable[split.Pushable[whole]];
                    continue;
                }

                ordinalOfFragment[digest] = inner.size();
                inner.add(fragment);
            }

            // A pushed column keeps the name the query gave it, which is what a plan reading it shows;
            // anything carried only for the filter is named by Calcite.
            var names = new java.util.ArrayList();
            for (var i = 0; i < inner.size(); i++)
                names.add(null);

            foreach (var (i, ordinal) in ordinalOfPushable)
                names.set(ordinal, project.getRowType().getFieldNames().get(i));

            var lower = LogicalProject.create(input, com.google.common.collect.ImmutableList.of(), inner, names, com.google.common.collect.ImmutableSet.of());

            // Every input the residual reads is one the projection selects as a bare reference, which
            // TrySplit has checked; it reads the column.
            foreach (var i in split.Pushable)
                if (projects.get(i) is RexInputRef carried)
                    ordinalOfInput[carried.getIndex()] = ordinalOfPushable[i];

            var shuttle = new CosmosProjectSplitRule.Rebase(rexBuilder, ordinalOfInput, ordinalOfFragment);
            var condition = (RexNode)filter.getCondition().accept(shuttle);
            var filtered = filter.copy(filter.getTraitSet(), lower, condition);

            var outer = new java.util.ArrayList();

            for (var i = 0; i < projects.size(); i++)
            {
                var expression = (RexNode)projects.get(i);

                outer.add(ordinalOfPushable.TryGetValue(i, out var pushed)
                    ? rexBuilder.makeInputRef(lower, pushed)
                    : (RexNode)expression.accept(shuttle));
            }

            call.transformTo(project.copy(project.getTraitSet(), filtered, outer, project.getRowType()));
        }

        /// <summary>
        /// Decides what moves below the filter, or that nothing worth moving does.
        /// </summary>
        bool TrySplit(Project project, Filter filter, out CosmosProjectSplitRule.ProjectionSplit split)
        {
            split = new CosmosProjectSplitRule.ProjectionSplit();

            if (project.getProjects().size() == 0)
                return false;

            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields, out _, out _, out var written, out var container) == false)
                return false;

            // One SELECT per statement: the subtree under the filter must not have written one. This
            // is also what stops the rule matching its own result.
            if ((written & CosmosClauses.Projection) != 0)
                return false;

            // What the filters beneath the residual establish of the rows the moved projection reads
            // (L1): the residual is above it and applied in process, so it proves nothing here.
            var facts = CosmosRowFacts.Of(filter.getInput(), container, OracleLicence.Select);
            var translator = new CosmosRexTranslator(project.getCluster().getRexBuilder(), fields, new CosmosParameterList(), facts, null, _convention.Container);

            // Only a filter that is wholly residual: one the filter split leaves nothing of to push. A
            // filter with a part that renders is split first, and the projection is moved past what
            // remains of it; asked of anything else, this would carry renderable comparisons as
            // columns, and race the filter rules for every filter in the plan. Split is the filter
            // rules' own decision, facts established by sibling conjuncts included, so the two cannot
            // disagree about which conjuncts render.
            var (pushable, residual) = CosmosFilterSplitRule.Split(filter, _convention.Container);
            if (pushable.Count > 0 || residual.Count == 0)
                return false;

            // The expressions the projection rules would push, so that the three agree on what renders.
            var projects = split.Projects = CosmosProjectRule.Pushed(_convention, project, fields);

            // The projection has to render whole, so that the residual is the only thing left in process.
            // A projection that does not is CosmosProjectSplitRule's to take apart, and taking it apart
            // here as well gave every merged view's CASE a projection of its own to explore — planning a
            // five-view hierarchy went from seconds to minutes.
            for (var i = 0; i < projects.size(); i++)
            {
                var expression = (RexNode)projects.get(i);

                if (translator.TryTranslateProjection(expression, out _, out var reading) == false)
                    return false;

                // Beneath the filter, a reading that can refuse a value would be asked of rows the filter
                // drops, where Calcite never reads them. A bare reference is read by the scan either way.
                if (expression is not RexInputRef && reading == CosmosReading.Typed && CosmosJson.CanRefuse(expression.getType()))
                    return false;

                split.Pushable.Add(i);
                split.Nontrivial |= expression is not RexInputRef;
            }

            CollectPaths(translator, filter.getCondition(), split);

            // Worth a transformation only where nothing has to be carried as it stands. An input the
            // residual still reads past every path it reads is, in practice, the
            // document — and a statement selecting the document beside the columns ships no less than
            // the one selecting it alone. Carried anyway, it was also a loop: the carried input is a
            // bare reference the next firing pushes as a column, and carries again beside (#210).
            // An input the projection already selects as a bare reference is that column, not one more.
            foreach (var i in split.Pushable)
                if (projects.get(i) is RexInputRef carried)
                    split.Inputs.Remove(carried.getIndex());

            if (split.Inputs.Count > 0)
                return false;

            // And something other than a bare input moves below: a projection of inputs alone is the
            // plan that selects them already.
            return split.Nontrivial;
        }

        /// <summary>
        /// Collects the document paths a residual reads, each as the accessor that reads it.
        /// </summary>
        /// <remarks>
        /// Paths and nothing larger. A comparison or a branch of the residual could render as a boolean
        /// column too, but a column per sub-predicate is a projection per spelling of the predicate, and
        /// the planner explores every one; a path is read once whatever the residual does with it, and
        /// the comparison over it costs nothing to make in process.
        /// </remarks>
        static void CollectPaths(CosmosRexTranslator translator, RexNode node, CosmosProjectSplitRule.ProjectionSplit split)
        {
            switch (node)
            {
                case RexInputRef inputRef:
                    split.Inputs.Add(inputRef.getIndex());
                    return;
                case RexLiteral or RexDynamicParam:
                    return;
            }

            if (node.getType().getSqlTypeName() != org.apache.calcite.sql.type.SqlTypeName.BOOLEAN
                && translator.TryResolvePath(node, out var path) && path is not null
                && translator.TryTranslateProjection(node, out _, out _))
            {
                var digest = node.ToString();

                if (split.Index.ContainsKey(digest) == false)
                {
                    split.Index[digest] = split.Fragments.Count;
                    split.Fragments.Add(node);
                }

                return;
            }

            if (node is RexCall call)
            {
                var operands = call.getOperands();
                for (var i = 0; i < operands.size(); i++)
                    CollectPaths(translator, (RexNode)operands.get(i), split);

                return;
            }

            var bits = RelOptUtil.InputFinder.bits(node);
            for (var bit = bits.nextSetBit(0); bit >= 0; bit = bits.nextSetBit(bit + 1))
                split.Inputs.Add(bit);
        }

    }

}
