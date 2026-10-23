using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The weakening's assembly at a leaf: what its implied forms make together. It prunes nothing and chooses nothing;
    /// whether a leaf takes its best or this fold is a rule's (<c>DESIGN.md</c>, <i>The rendering graph in generate mode</i>).
    /// </summary>
    internal static class RenderingWeakening
    {

        /// <summary>
        /// The implied forms joined in the set's order, by <c>AND</c> at <c>Pos</c>, where each keeps every row the leaf
        /// keeps, and by <c>OR</c> at <c>Neg</c>, where each drops every row it drops; the connective's identity for none.
        /// </summary>
        public static ServiceForm AtLeaf(ValueSet<ServiceForm> implied, RenderPosition pos)
        {
            var connective = pos == RenderPosition.Pos ? CosmosServiceTest.And : CosmosServiceTest.Or;
            return implied.Aggregate(ServiceForm.ConstantOf(pos == RenderPosition.Pos), (joined, member) => ServiceForm.Joined(connective, joined, member));
        }

    }

}
