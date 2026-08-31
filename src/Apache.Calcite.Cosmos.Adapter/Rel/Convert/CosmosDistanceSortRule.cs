using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Recognises an ordering by <c>ST_DISTANCE</c> and turns it into a <see cref="CosmosDistanceSort"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calcite sorts by field ordinal, so ordering by an expression makes it a column first. Where the
    /// query does not select the distance that is three nodes:
    /// </para>
    /// <code>
    /// LogicalProject(id=[$0])                                  drops the distance
    ///   LogicalSort(sort0=[$1])                                sorts on it
    ///     LogicalProject(id=[$1], $f1=[ST_DISTANCE($0, '…')])  adds it
    ///       TableScan
    /// </code>
    /// <para>
    /// and the middle one is what <see cref="CosmosSort"/> cannot express: its keys must resolve to
    /// document paths and a computed column has none. Collapsed into a single node the distance is
    /// rendered into the clause and the surviving columns into the select list, which is the statement
    /// the service will run.
    /// </para>
    /// <para>
    /// Where the query <em>does</em> select the distance it is two nodes rather than three — the sort
    /// keys on a column the select list already carries — and they collapse the same way. Cosmos has no
    /// name for a projected column, so the clause repeats the expression either way, and a node
    /// repeating it has to own both ends. <see cref="Create"/> matches the first shape and
    /// <see cref="CreateProjected"/> the second; everything else about them is the same, which is why
    /// they are one rule with two operand trees rather than two rules.
    /// </para>
    /// <para>
    /// <b>One key, and it has to be the distance.</b> Measured, a distance ordering paired with a second
    /// key is rejected with <em>"ORDER BY item expression could not be mapped to a document path"</em> —
    /// no index serves the pair — so a composite is refused here rather than emitted.
    /// </para>
    /// </remarks>
    public class CosmosDistanceSortRule : RelOptRule
    {

        /// <summary>
        /// Creates the rule matching a distance ordering whose column is dropped again.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
#pragma warning disable CS0612
        public static CosmosDistanceSortRule Create(CosmosConvention convention)
        {
            return new CosmosDistanceSortRule(
                convention,
                operand((java.lang.Class)typeof(Project),
                    operand((java.lang.Class)typeof(Sort),
                        operand((java.lang.Class)typeof(Project), any()))),
                dropped: true);
        }

        /// <summary>
        /// Creates the rule matching a distance ordering whose column the query also selects.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosDistanceSortRule CreateProjected(CosmosConvention convention)
        {
            return new CosmosDistanceSortRule(
                convention,
                operand((java.lang.Class)typeof(Sort),
                    operand((java.lang.Class)typeof(Project), any())),
                dropped: false);
        }

        readonly CosmosConvention _convention;
        readonly bool _dropped;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <param name="operand">The tree of nodes to match.</param>
        /// <param name="dropped">Whether the matched tree begins with a projection that discards the distance column.</param>
        public CosmosDistanceSortRule(CosmosConvention convention, RelOptRuleOperand operand, bool dropped) :
            base(operand, "CosmosDistanceSortRule." + (dropped ? "dropped" : "projected") + "." + convention.getName())
        {
            _convention = convention;
            _dropped = dropped;
        }
#pragma warning restore CS0612

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            return Analyse(call) is not null;
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            if (Analyse(call) is not Match match)
                return;

            var inner = Inner(call);

            call.transformTo(new CosmosDistanceSort(
                inner.getCluster(),
                inner.getTraitSet().replace(_convention),
                org.apache.calcite.plan.RelOptRule.convert(inner.getInput(), inner.getInput().getTraitSet().replace(_convention)),
                match.Projects,
                match.RowType,
                match.Distance,
                match.Descending,
                match.Offset,
                match.Fetch));
        }

        /// <summary>
        /// The projection that computes the distance, which is the innermost matched node either way.
        /// </summary>
        Project Inner(RelOptRuleCall call) => (Project)call.rel(_dropped ? 2 : 1);

        /// <summary>
        /// The sort carrying the distance ordering.
        /// </summary>
        Sort Ordering(RelOptRuleCall call) => (Sort)call.rel(_dropped ? 1 : 0);

        /// <summary>
        /// What the matched shape amounts to, or <c>null</c> where it is not one this can render.
        /// </summary>
        sealed record Match(java.util.List Projects, org.apache.calcite.rel.type.RelDataType RowType, RexNode Distance, bool Descending, RexNode? Offset, RexNode? Fetch);

        /// <summary>
        /// Reads the matched nodes and decides whether they are an ordering by a distance this can
        /// express, resolving everything the resulting node will need.
        /// </summary>
        Match? Analyse(RelOptRuleCall call)
        {
            var sort = Ordering(call);
            var inner = Inner(call);

            // A distance ordering cannot carry a tiebreak: the service refuses the pair.
            var collations = sort.getCollation().getFieldCollations();
            if (collations.size() != 1)
                return null;

            var collation = (org.apache.calcite.rel.RelFieldCollation)collations.get(0);
            var key = collation.getFieldIndex();
            if (key < 0 || key >= inner.getProjects().size())
                return null;

            var distance = (RexNode)inner.getProjects().get(key);
            if (CosmosRexTranslator.IsDistanceFunction(distance) == false)
                return null;

            // The same null-placement question an ordinary sort key raises, asked of the expression's
            // declared type: Cosmos orders an absent value below everything ascending and above
            // everything descending, and Calcite's defaults are the reverse of both. ST_DISTANCE is
            // declared non-nullable, so this settles rather than refuses — see CosmosOperators, which
            // records what that declaration costs.
            var typeFields = inner.getRowType().getFieldList();
            if (key >= typeFields.size())
                return null;

            var nullable = ((org.apache.calcite.rel.type.RelDataTypeField)typeFields.get(key)).getType().isNullable();
            if (CosmosSort.TryGetDescending(collation, nullable, out var descending) == false)
                return null;

            java.util.List projects;
            org.apache.calcite.rel.type.RelDataType rowType;

            if (_dropped)
            {
                var outer = (Project)call.rel(0);

                // Every surviving column must come straight through, and none of them may be the
                // distance. A computed outer projection would have to be rebuilt over the inner one,
                // and an outer reference to the distance means the query selects it — which is the
                // other shape, matched by the other operand tree rather than by rewriting this one.
                var kept = new java.util.ArrayList();

                for (var i = 0; i < outer.getProjects().size(); i++)
                {
                    if ((RexNode)outer.getProjects().get(i) is not RexInputRef reference)
                        return null;

                    var ordinal = reference.getIndex();
                    if (ordinal == key || ordinal < 0 || ordinal >= inner.getProjects().size())
                        return null;

                    kept.add(inner.getProjects().get(ordinal));
                }

                projects = kept;
                rowType = outer.getRowType();
            }
            else
            {
                // The distance stays a column, which is legal — unlike a score, the service projects
                // one — and the clause repeats the expression rather than naming the alias.
                projects = inner.getProjects();
                rowType = sort.getRowType();
            }

            if (projects.isEmpty())
                return null;

            // Everything has to render, and against the binding implementation will use.
            if (CosmosImplementor.TryBindOutput(inner.getInput(), out var fields, out var projected) == false || projected)
                return null;

            var translator = new CosmosRexTranslator(inner.getCluster().getRexBuilder(), fields, new CosmosParameterList());

            for (var i = 0; i < projects.size(); i++)
                if (translator.TryTranslate((RexNode)projects.get(i), out _) == false)
                    return null;

            if (translator.TryTranslate(distance, out _) == false)
                return null;

            return new Match(projects, rowType, distance, descending, sort.offset, sort.fetch);
        }

    }

}
