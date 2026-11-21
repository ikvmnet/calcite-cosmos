using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;

using org.apache.calcite.plan.volcano;
using org.apache.calcite.rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// A statement over the planner corpus's containers converted, rewritten and registered with a Volcano planner, as a host
    /// registers it before its rules run, and its members as the correlate rule reads them: each input an equivalence set,
    /// read through the node it was built from.
    /// </summary>
    static class RegisteredMembers
    {

        /// <summary>
        /// Returns the registered members of a statement, the root's first and each member's inputs after it.
        /// </summary>
        public static IReadOnlyList<RelNode> Of(string sql)
        {
            var logical = new PlannerHarness().ToLogical(sql);
            var planner = logical.getCluster().getPlanner();
            planner.setRoot(logical);

            var members = new List<RelNode>();
            var seen = CosmosPlanMembers.NewSeen();
            Walk(planner.getRoot());
            return members;

            void Walk(RelNode node)
            {
                var member = node is RelSubset subset ? subset.getOriginal() ?? subset.getBest() : node;
                if (member is null || seen.Add(member) == false)
                    return;

                members.Add(member);
                var inputs = member.getInputs();
                for (var i = 0; i < inputs.size(); i++)
                    Walk((RelNode)inputs.get(i));
            }
        }

    }

}
