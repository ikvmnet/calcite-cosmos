using System;

using org.apache.calcite.rex;

using System.Collections.Generic;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Sort"/> to a <see cref="CosmosSort"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unusually for a conversion rule, legality here depends on container metadata rather than on
    /// the plan alone. Cosmos requires a composite index for any <c>ORDER BY</c> over two or more
    /// properties, and rejects the query outright when none matches. Converting such a sort would
    /// therefore produce a statement the service refuses, so the rule consults the container's
    /// indexing policy before firing.
    /// </para>
    /// <para>
    /// A single-key sort needs no composite index. It may be slower without one, but it runs.
    /// </para>
    /// </remarks>
    public class CosmosSortRule : CosmosConverterRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosSortRule Create(CosmosConvention convention)
        {
            var rule = (CosmosSortRule)Config.INSTANCE
                .withConversion(typeof(Sort), new DelegatePredicate<Sort>(s => IsSupported(convention, s)), Convention.NONE, convention, "CosmosSortRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosSortRule>(c => new CosmosSortRule(c)))
                .toRule(typeof(CosmosSortRule));

            // Conversion needs the container the same way the legality test does, and the out trait
            // carries it only behind a cast at every use. A rule is created per convention.
            rule._convention = convention;
            return rule;
        }

        CosmosConvention? _convention;

        /// <summary>
        /// Determines whether a sort can be pushed into the given container.
        /// </summary>
        /// <remarks>
        /// The binding is derived by walking the input, not read off its row type by name, and the same
        /// walk reports which clauses the subtree has already written. Above a projection the names are
        /// aliases, and binding them by name invents paths the container does not have — which let this
        /// rule fire on a sort key that implementation then refused, and, worse,
        /// checked a multi-key sort against the composite indexes using paths like <c>/u</c>. Deciding on
        /// the same binding implementation will use is what makes the answer here final.
        /// </remarks>
        static bool IsSupported(CosmosConvention convention, Sort sort)
        {
            if (CosmosImplementor.TryBindOutput(sort.getInput(), out var fields, out _, out var candidates, out var written) == false)
                return false;

            // A statement has one ORDER BY, and its OFFSET/LIMIT is applied after it rather than
            // before. Onto a subtree that has written either, this sort is not the statement's: above
            // an ordering it is a second one, which implementation refuses; above a page it renders
            // into a statement that orders the container and then takes a page, where the plan asked
            // for the page to be taken and then ordered. The second is the reason to decide it here —
            // those are different rows, and nothing downstream would have noticed.
            if ((written & (CosmosClauses.OrderBy | CosmosClauses.RowLimit)) != 0)
                return false;

            if (CosmosSort.TryResolveSortKeys(sort.getCollation(), fields, sort.getInput().getRowType(), CosmosImplementor.DefaultRootAlias, NonNullFields(convention, sort), SortableFields(sort.getInput(), fields.Count), convention.Container, OrderingPaths(convention, candidates), out var keys, out _) == false)
                return false;

            return convention.Container.IsSortSupported(keys);
        }

        /// <summary>
        /// Reads which output ordinals hold an expression the service will order by.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked of the plan rather than of a binding, because the expression itself has not been
        /// rendered yet — this decides whether the sort <em>can</em> be pushed, and implementation
        /// renders it from what the projection recorded. Both answer the same question about the same
        /// ordinal, which is the property that matters.
        /// </para>
        /// <para>
        /// Only a geodesic distance qualifies, and only immediately beneath the sort. A filter between
        /// the two does not change a row's shape, so the ordinals still line up and it is walked
        /// through; anything else is not, because an ordinal that means something different is worse
        /// than one that means nothing.
        /// </para>
        /// </remarks>
        static IReadOnlyList<bool> SortableFields(RelNode? input, int width)
        {
            var sortable = new bool[width];

            while (true)
            {
                if (input is org.apache.calcite.plan.volcano.RelSubset subset)
                    input = subset.getOriginal() ?? subset.getBest();

                if (input is Filter filter)
                {
                    input = filter.getInput();
                    continue;
                }

                break;
            }

            if (input is not Project project)
                return sortable;

            var projects = project.getProjects();
            for (var i = 0; i < projects.size() && i < width; i++)
                sortable[i] = (RexNode)projects.get(i) is RexCall call
                    && string.Equals(call.getOperator().getName(), Apache.Calcite.Geography.Sql.GeographyOperatorTable.StGeogDistance.getName(), StringComparison.Ordinal);

            return sortable;
        }

        /// <summary>
        /// Reads which output ordinals the container licenses ordering by, where the ordinal binds to
        /// no path of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>No walk of its own, and that is the point.</b> Which ordinal holds which candidate is
        /// derived on the one walk <see cref="CosmosImplementor.TryBindOutput"/> makes — the walk being
        /// the part that depends on which member of a <c>RelSubset</c> is looked at, and therefore the
        /// part a second traversal could answer differently from the first. An earlier version of this
        /// did walk, and unwrapped a subset a second time on a justification weaker than the one
        /// <c>TryBindOutput</c> states: that any member <em>binds</em> the same because members are
        /// equivalent, which is a claim about the binding and not about which expressions one
        /// representative happens to carry.
        /// </para>
        /// <para>
        /// What is left here is a pure lookup, which cannot disagree with the same lookup made during
        /// implementation however the planner has moved on.
        /// </para>
        /// </remarks>
        /// <param name="convention">The convention, carrying the container.</param>
        /// <param name="candidates">The candidates the binding derived.</param>
        /// <returns>The path per ordinal, or <c>null</c> where the container licenses none.</returns>
        static IReadOnlyList<Sql.CosmosPath?> OrderingPaths(CosmosConvention? convention, IReadOnlyList<Sql.CosmosPath?> candidates)
        {
            var ordering = new Sql.CosmosPath?[candidates.Count];

            if (convention?.Container is not Metadata.CosmosContainerMetadata container || container.Facts.IsEmpty)
                return ordering;

            // Only what holds outright, for the reason NonNullFields gives: a sort carries no
            // predicate of its own to prove a guarded fact from.
            var facts = container.Facts.Derive(null);

            for (var i = 0; i < candidates.Count; i++)
                ordering[i] = CosmosProject.IsOrderable(facts, candidates[i]) ? candidates[i] : null;

            return ordering;
        }

        /// <summary>
        /// Reads the fields the plan guarantees are never null over the sort's input.
        /// </summary>
        /// <remarks>
        /// Asked here and handed to the node, rather than asked twice. The answer depends on which
        /// equivalent of the input the metadata is asked about, so the rule and the node have to be
        /// deciding on the same one — the same reason the binding above is derived by walking the
        /// input rather than read off its row type.
        /// </remarks>
        static IReadOnlyList<int> NonNullFields(CosmosConvention? convention, Sort sort)
        {
            var guaranteed = CosmosSort.FindNonNullFields(sort.getInput(), sort.getCluster().getMetadataQuery());

            if (convention?.Container is not Metadata.CosmosContainerMetadata container)
                return guaranteed;

            if (CosmosImplementor.TryBindOutput(sort.getInput(), out var fields, out _) == false)
                return guaranteed;

            // Only what holds outright. A sort carries no predicate of its own, so there is nothing
            // here to prove a guarded fact from, and a fact conditional on a filter below would need
            // that filter to have reached the service before it could be leaned on.
            var facts = container.Facts.Derive(null);
            var all = new List<int>(guaranteed);
            var rowFields = sort.getInput().getRowType().getFieldList();

            for (var i = 0; i < fields.Count; i++)
            {
                if (all.Contains(i) || fields[i] is not Sql.CosmosPath path)
                    continue;

                // A field the plan already types as non-nullable needs nothing said about it, and
                // saying it anyway would put a promoted column in a list whose purpose is to name the
                // ones the plan would otherwise have refused.
                if (i < rowFields.size() &&
                    ((org.apache.calcite.rel.type.RelDataTypeField)rowFields.get(i)).getType().isNullable() == false)
                    continue;

                if (string.Equals(path.Alias, CosmosImplementor.DefaultRootAlias, StringComparison.Ordinal) == false)
                    continue;

                if (Metadata.CosmosDocumentPath.From(path) is Metadata.CosmosDocumentPath document && NeverNull(facts, document))
                    all.Add(i);
            }

            return all;
        }

        /// <summary>
        /// Determines whether the accessor over a path can be relied on never to answer SQL null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two claims, and both are needed. The path has to be <em>there</em>, since the accessor
        /// answers null over an absent one; and it has to hold a scalar of a known type, since the
        /// accessor answers null for a JSON null and for an object or an array alike — neither being
        /// a scalar, which is SQL/JSON's own line. A type admitting null is not enough for the same
        /// reason the presence is not.
        /// </para>
        /// <para>
        /// What it buys is the null-placement rule, which is what actually stands between a declared
        /// path and a pushed sort: Cosmos orders nulls first ascending and Calcite's default is last,
        /// so a nullable key is refused however well the ordering is otherwise understood. A key that
        /// cannot be null leaves the two nothing to disagree about — the same argument a query
        /// removing the nulls itself makes, settled by the container instead of by the predicate.
        /// </para>
        /// </remarks>
        static bool NeverNull(Metadata.CosmosFactSet facts, Metadata.CosmosDocumentPath path) =>
            facts.IsAlwaysScalar(path);

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosSortRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var sort = (Sort)rel;

            return new CosmosSort(
                sort.getCluster(),
                sort.getTraitSet().replace(@out),
                convert(sort.getInput(), sort.getInput().getTraitSet().replace(@out)),
                sort.getCollation(),
                sort.offset,
                sort.fetch,
                NonNullFields(_convention, sort));
        }

    }

}
