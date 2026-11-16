using org.apache.calcite.plan;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Merges a projection that declares a correlation variable none of its expressions reads into the projection beneath
    /// it, declaring none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What leaves one.</b> A scalar sub-query in a select list makes its projection declare the variable the sub-query
    /// is correlated on. Where nothing above reads the column the sub-query computes, Calcite's field trimmer drops the
    /// column, sub-query and all, and keeps the declaration (<c>RelFieldTrimmer.trimFields(Project, …)</c>), before any
    /// rule removes the sub-query in a host that trims first, the connection the README opens among them. No rule converts
    /// a projection declaring a variable, the cursor convention's and the enumerable one's alike; Calcite's own merge refuses
    /// one too; and the projection a trimmer leaves stands over the one it trimmed. So such a statement did not plan at
    /// all, declared or not.
    /// </para>
    /// <para>
    /// <b>Why it is the same relation.</b> A correlation variable is read only where an expression names it; one no
    /// expression names binds nothing, so the projection computes the same rows without it, and composed onto the projection
    /// beneath it computes what the two computed. Merged, it is a projection the adapter pushes
    /// (<see cref="CosmosProjectRule"/>), or Calcite computes. One whose expressions still name a variable, or hold a
    /// sub-query, is left as it is. A new relation, not the same projection rebuilt without the declaration: the planner
    /// tells two projections apart by their input and expressions alone, and would read that one as the one it already has.
    /// </para>
    /// <para>
    /// One instance for every container, as Calcite's own rules are registered once whatever registers them.
    /// </para>
    /// </remarks>
    public class CosmosUncorrelatedProjectMergeRule : RelOptRule
    {

        /// <summary>
        /// The rule.
        /// </summary>
        public static CosmosUncorrelatedProjectMergeRule Instance { get; } = new();

        // As CosmosFilterSplitRule: RelOptRule's operand builders are deprecated in favour of RelRule.Config, whose operand
        // supplier costs more ceremony from C# than it buys.
#pragma warning disable CS0612
        CosmosUncorrelatedProjectMergeRule() :
            base(operand((java.lang.Class)typeof(LogicalProject), Convention.NONE, some(operand((java.lang.Class)typeof(LogicalProject), Convention.NONE, any()))), "CosmosUncorrelatedProjectMergeRule")
        {

        }
#pragma warning restore CS0612

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            var top = (LogicalProject)call.rel(0);
            var bottom = (LogicalProject)call.rel(1);
            return top.getVariablesSet().isEmpty() == false && Uncorrelated(top) && (bottom.getVariablesSet().isEmpty() || Uncorrelated(bottom));
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var top = (LogicalProject)call.rel(0);
            var bottom = (LogicalProject)call.rel(1);
            call.transformTo(LogicalProject.create(bottom.getInput(), top.getHints(), RelOptUtil.pushPastProject(top.getProjects(), bottom), top.getRowType(), java.util.Collections.emptySet()));
        }

        /// <summary>
        /// Determines whether no expression of a projection names a correlation variable or holds a sub-query.
        /// </summary>
        static bool Uncorrelated(Project project)
        {
            if (RexUtil.SubQueryFinder.containsSubQuery(project))
                return false;

            var projects = project.getProjects();
            for (var i = 0; i < projects.size(); i++)
                if (RexUtil.containsCorrelation((RexNode)projects.get(i)))
                    return false;

            return true;
        }

    }

}
