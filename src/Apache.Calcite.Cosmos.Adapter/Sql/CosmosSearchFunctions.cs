using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Names the full text and scoring functions, by name, and which way each scoring function ranks.
    /// </summary>
    /// <remarks>
    /// See DESIGN.md, <i>Two routes to a name, and why there are two</i> and <i>The Rex readers, apart from the translator</i>.
    /// </remarks>
    internal static class CosmosSearchFunctions
    {

        /// <summary>
        /// Determines whether an expression is one of the functions <c>ORDER BY RANK</c> ranks by.
        /// </summary>
        internal static bool IsScoringFunction(RexNode? node) => node is RexCall call && BestFirstDescending(call.getOperator().getName()) is not null;

        /// <summary>
        /// Determines whether an expression is a scoring function whose best match is its greatest value, so that
        /// <c>ORDER BY RANK</c>, which answers the best first, is the descending order of it.
        /// </summary>
        internal static bool IsBestFirstDescending(RexNode? node) => node is RexCall call && BestFirstDescending(call.getOperator().getName()) == true;

        /// <summary>
        /// Says which way a scoring function ranks, by name: <c>true</c> where its best match is its greatest value,
        /// <c>false</c> where nothing the metadata holds says which, and <c>null</c> for a name that is no scoring function.
        /// </summary>
        /// <remarks>
        /// The one list of them: <see cref="IsScoringFunction"/>, <see cref="IsBestFirstDescending"/> and
        /// <see cref="CosmosSchemaFunction.Refusal"/> all read it. <c>VECTORDISTANCE</c> ranks by the container's distance
        /// function, which the metadata does not hold. See DESIGN.md, <i>Two routes to a name, and why there are two</i>.
        /// </remarks>
        internal static bool? BestFirstDescending(string name) => name switch
        {
            "FULLTEXTSCORE" or "RRF" or "CLR_FT_SCORE" or "CLR_FT_RRF" => true,
            "VECTORDISTANCE" => false,
            _ => null,
        };

        /// <summary>
        /// Determines whether an expression is a full text function — a predicate or the score — whose first argument is the
        /// path searched.
        /// </summary>
        /// <remarks>
        /// By name. What the container declares about that path is what the call costs, priced by <see cref="Rel.CosmosFilter"/>.
        /// See DESIGN.md, <i>The declaration prices a full text function, and still gates a vector one</i>.
        /// </remarks>
        internal static bool IsFullTextFunction(RexNode? node) => node is RexCall call && call.getOperator().getName() switch
        {
            "FULLTEXTCONTAINS" or "FULLTEXTCONTAINSALL" or "FULLTEXTCONTAINSANY" or "FULLTEXTSCORE" => true,
            "CLR_FT_CONTAINS" or "CLR_FT_CONTAINS_ALL" or "CLR_FT_CONTAINS_ANY" or "CLR_FT_SCORE" => true,
            _ => false,
        };

    }

}
