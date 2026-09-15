using System.Collections.Generic;

using org.apache.calcite.rel;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// The expressions a node in a registered plan stands for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Once a tree is registered with the Volcano planner an operator's input is an equivalence set
    /// rather than a node</b>, so anything inspecting more than its own node has to see through one.
    /// There are two ways to, and which is right depends on what is being asked.
    /// </para>
    /// <para>
    /// <b>Asking what a subtree <em>means</em> — a binding, a row type, which container it reads as a
    /// relation — may take any member</b>, because the members are equivalent and equivalence is
    /// exactly the claim that they mean the same. That is the argument
    /// <see cref="CosmosImplementor.TryBindOutput"/> states and it holds.
    /// </para>
    /// <para>
    /// <b>Asking whether a subtree <em>has a shape</em> may not.</b> Members are equivalent as
    /// relations and not as trees: one may present a <c>Filter</c> where another has pushed the
    /// predicate into the scan, and one may be a node the asker's own cases do not mention while its
    /// neighbour is the shape being looked for. So a search over one arbitrary representative answers
    /// "no shape here" for a plan that has it, and which representative it gets depends on how far the
    /// planner had progressed — the answer changes under the asker rather than being wrong once.
    /// </para>
    /// <para>
    /// <b>Where a search is existential, enumerating is also sound.</b> Finding the shape in any member
    /// establishes it of the relation, the members being equivalent; failing to find it in a member
    /// establishes nothing about the others. So taking the first member that answers is the right
    /// reading, and the only thing enumeration costs is the work of asking more than once.
    /// </para>
    /// </remarks>
    public static class CosmosPlanMembers
    {

        /// <summary>
        /// Returns the expressions to ask, which is the node itself unless it is an equivalence set.
        /// </summary>
        /// <remarks>
        /// Both the registered alternatives and the expression the set was built from. The latter is
        /// not always among the former — it is the node the set was created around, and a set the
        /// planner has since rebuilt may no longer list it — and it is the one the rest of this
        /// adapter used to take, so including it keeps every shape that was found before still found.
        /// </remarks>
        /// <param name="node">The node, which may be an equivalence set or <c>null</c>.</param>
        /// <returns>The expressions, which may be empty.</returns>
        public static IReadOnlyList<RelNode> Of(RelNode? node)
        {
            if (node is null)
                return System.Array.Empty<RelNode>();

            if (node is not org.apache.calcite.plan.volcano.RelSubset subset)
                return new[] { node };

            var members = new List<RelNode>();
            var alternatives = subset.getRelList();

            for (var i = 0; i < alternatives.size(); i++)
                if ((RelNode)alternatives.get(i) is RelNode alternative)
                    members.Add(alternative);

            if (subset.getOriginal() is RelNode original && members.Contains(original) == false)
                members.Add(original);

            if (subset.getBest() is RelNode best && members.Contains(best) == false)
                members.Add(best);

            return members;
        }

        /// <summary>
        /// Returns the first expression a node stands for that is of a given kind, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// One level, which is what a caller looking for the node immediately beneath an operator
        /// wants — the members of a set are expressions rather than further sets, so there is nothing
        /// to descend through here. A caller that walks operators descends itself and asks again.
        /// </remarks>
        /// <typeparam name="T">The kind looked for.</typeparam>
        /// <param name="node">The node, which may be an equivalence set.</param>
        /// <returns>The first member of that kind, or <c>null</c>.</returns>
        public static T? First<T>(RelNode? node) where T : class
        {
            foreach (var member in Of(node))
                if (member is T match)
                    return match;

            return null;
        }

        /// <summary>
        /// Creates the set a search carries to keep itself finite.
        /// </summary>
        /// <remarks>
        /// A registered plan is a graph rather than a tree — a set's members have inputs that are sets,
        /// and nothing forbids one reaching a set already on the way down. Walking one representative
        /// could not loop because it never branched; walking every member can, and would also re-ask
        /// the same node once per path that reaches it. Identity is the right equality: Calcite leaves
        /// <c>equals</c> on a <c>RelNode</c> as the object's own and keeps structural comparison in
        /// <c>deepEquals</c>, so two distinct expressions that look alike stay distinct here.
        /// </remarks>
        /// <returns>An empty set.</returns>
        public static HashSet<RelNode> NewSeen() =>
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance as IEqualityComparer<RelNode>);

    }

}
