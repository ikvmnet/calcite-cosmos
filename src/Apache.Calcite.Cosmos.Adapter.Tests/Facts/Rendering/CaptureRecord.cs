using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// One conjunct the translator wrote for a filter: at admission (site 1), into the statement sent (site 2), or as a
    /// residual conjunct's weakening (site 3), with the node weakened to. It holds the statement, whose scope keeps its
    /// tree of contexts alive until the check imports from it; the text; the values the text's parameters were bound to;
    /// and the comparisons and rechecks a parameter's value decides.
    /// </summary>
    internal sealed record CaptureRecord(int Site, CosmosFilterStatement Statement, int Conjunct, string Text, IReadOnlyList<CosmosParameter> Parameters,
        IReadOnlyList<CosmosOrderComparison> Comparisons, IReadOnlyList<CosmosRecheck> Rechecks, RexNode? Weakened = null);

}
