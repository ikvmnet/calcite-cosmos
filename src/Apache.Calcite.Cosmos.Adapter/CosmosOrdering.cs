using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// How one output ordinal may take part in an <c>ORDER BY</c>, where it binds to no path of its
    /// own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both members answer a question about <em>which expression sits at this ordinal</em>, which is
    /// the part that depends on which tree is looked at. Deriving them on the one walk
    /// <see cref="CosmosImplementor.TryBindOutput"/> makes is what keeps a rule and the implementation
    /// from answering differently — a rule that read one member of a <c>RelSubset</c> while
    /// implementation read another would fire on a key implementation then refused, after the planner
    /// had committed to the plan.
    /// </para>
    /// <para>
    /// Neither says whether the container <em>licenses</em> anything. That is a pure lookup over the
    /// path and the declared facts, which cannot disagree with itself, and each site makes it for
    /// itself — see <see cref="Rel.CosmosProject.IsOrderable"/>.
    /// </para>
    /// </remarks>
    /// <param name="Path">
    /// The path a sort may order by, where the ordinal projects a conversion over one; otherwise
    /// <c>null</c>. Structural only — see <see cref="Rel.CosmosProject.OrderingCandidateOf"/>.
    /// </param>
    /// <param name="Expression">
    /// Whether the ordinal holds an expression the service will order by, written out a second time
    /// rather than referred to. Only a geodesic distance qualifies — see
    /// <see cref="CosmosImplementor.SortableExpressions"/> for what was measured and refused.
    /// </param>
    /// <param name="Operands">
    /// The document paths that expression reads, where every operand is one the structure accounts
    /// for; <c>null</c> otherwise, and empty where the expression reads none. What it is for is the
    /// null placement: a distance is null only where an operand is, so a container declaring these
    /// paths always present makes the key non-nullable and the placement moot. See
    /// <see cref="Rel.CosmosProject.SortableOperandsOf"/>.
    /// </param>
    /// <param name="Format">
    /// The format a parse reads <paramref name="Path"/> with, where the ordinal holds a chain rather
    /// than a cast; <c>null</c> otherwise. Structural, like the path — which format the query wrote is
    /// a question about the expression at this ordinal, and whether it reads the declared shape is the
    /// caller's pure lookup. See <see cref="Rel.CosmosProject.OrderingCandidateOf"/>.
    /// </param>
    /// <param name="Held">
    /// The halves of an instant the conversion's value holds, which is what says whether the parse
    /// truncates. See <see cref="Metadata.CosmosTemporalParts"/>.
    /// </param>
    public readonly record struct CosmosOrdering(Sql.CosmosPath? Path, bool Expression, IReadOnlyList<Sql.CosmosPath>? Operands = null, string? Format = null, Metadata.CosmosTemporalParts Held = Metadata.CosmosTemporalParts.None);

}
