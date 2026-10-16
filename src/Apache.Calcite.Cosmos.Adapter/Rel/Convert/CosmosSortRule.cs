using System;

using org.apache.calcite.rex;

using System.Collections.Generic;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

using Apache.Calcite.Cosmos.Facts;

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
            return (CosmosSortRule)Config.INSTANCE
                .withConversion(typeof(Sort), new DelegatePredicate<Sort>(s => IsSupported(convention, s)), Convention.NONE, convention, "CosmosSortRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosSortRule>(c => new CosmosSortRule(c)))
                .toRule(typeof(CosmosSortRule));
        }

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
            if (CosmosImplementor.TryBindOutput(sort.getInput(), out var fields, out var readings, out var candidates, out var written, out var container) == false)
                return false;

            // A statement has one ORDER BY, and its OFFSET/LIMIT is applied after it rather than
            // before. Onto a subtree that has written either, this sort is not the statement's: above
            // an ordering it is a second one, which implementation refuses; above a page it renders
            // into a statement that orders the container and then takes a page, where the plan asked
            // for the page to be taken and then ordered. The second is the reason to decide it here —
            // those are different rows, and nothing downstream would have noticed.
            if ((written & (CosmosClauses.OrderBy | CosmosClauses.RowLimit)) != 0)
                return false;

            // What every row the sort orders is known to satisfy: what the filters beneath establish
            // (L1), the ORDER BY being applied to the rows the WHERE kept. Decided once for this sort
            // and handed to the node, which asks every question below again of the same facts.
            var facts = Facts(sort, container);

            // Whether each column is the value the service orders by is asked of the walk's own binding,
            // readings and container -- what the node asks of the implementor the same walk built.
            var translator = new Sql.CosmosRexTranslator(sort.getCluster().getRexBuilder(), fields, new Sql.CosmosParameterList(), null, container, readings, facts);
            var stored = CosmosSort.StoredFields(translator, sort.getInput().getRowType());

            if (CosmosSort.TryResolveSortKeys(sort.getCollation(), fields, sort.getInput().getRowType(), CosmosImplementor.DefaultRootAlias, NonNullFields(facts, sort, fields, candidates), SortableFields(candidates), facts, CosmosSort.OrderingPaths(facts, candidates), stored, out var keys, out _) == false)
                return false;

            return convention.Container.IsSortSupported(keys);
        }

        /// <summary>
        /// Returns what every row a sort orders is known to satisfy: the filters beneath it (L1), the same each time it is
        /// asked of the same sort. See <see cref="Metadata.CosmosRowFacts"/>.
        /// </summary>
        static JsonFactSet Facts(Sort sort, Metadata.CosmosContainerMetadata? container) =>
            Metadata.CosmosRowFacts.For(sort, container, OracleLicence.OrderBy);

        /// <summary>
        /// Reads which output ordinals hold an expression the service will order by.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>No walk of its own.</b> This used to find the projection beneath the sort by walking to
        /// it, unwrapping a <c>RelSubset</c> on the way and reading the operator name off whichever
        /// member came back. That is a question about which expression sits at an ordinal, and two
        /// walks can answer it differently: the members of a subset are equivalent, which licenses
        /// deriving a <em>binding</em> from any one of them and says nothing about which expressions
        /// one representative happens to carry. A rule that read one member while implementation read
        /// another would fire on a key implementation then refused.
        /// </para>
        /// <para>
        /// So it is derived on the one walk <see cref="CosmosImplementor.TryBindOutput"/> makes,
        /// beside the binding and the ordering candidate, and this is only the projection of that
        /// answer onto the shape <see cref="CosmosSort.TryResolveSortKeys"/> takes.
        /// </para>
        /// </remarks>
        /// <param name="ordering">What the binding derived per ordinal.</param>
        /// <returns>Whether each ordinal holds such an expression.</returns>
        static IReadOnlyList<bool> SortableFields(IReadOnlyList<CosmosOrdering> ordering)
        {
            var sortable = new bool[ordering.Count];

            for (var i = 0; i < ordering.Count; i++)
                sortable[i] = ordering[i].Expression;

            return sortable;
        }

        /// <summary>
        /// Reads the fields the rows being sorted are known never to hold null in, over the sort's
        /// input.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asked here and handed to the node, rather than asked twice — see
        /// <see cref="CosmosSort.NonNullFields"/>. The facts are the rows' (L1): a key the filter
        /// beneath proves present and a scalar has no null for the two placements to disagree about,
        /// which is what lets <c>WHERE t IS NOT NULL ORDER BY t</c> push under either collation. A
        /// sort carries no predicate of its own, and nothing it says is among them.
        /// </para>
        /// <para>
        /// This used to start from Calcite's pulled-up predicates (<c>RelMdPredicates</c>), an
        /// <c>IS NOT NULL</c> over a field reference, which reached a promoted column and nothing
        /// else: a document path projects as an accessor call, not a reference, and the predicate was
        /// dropped there. Once a promoted column was a <c>VARIANT</c>, which no sort pushes, it reached
        /// nothing. What the oracle reads off the filter reaches every path the filter reads, through
        /// whatever projects it.
        /// </para>
        /// </remarks>
        /// <param name="facts">What every row the sort orders is known to satisfy.</param>
        /// <param name="sort">The sort.</param>
        /// <param name="fields">The binding the walk derived of its input.</param>
        /// <param name="candidates">The ordering candidates the same walk derived.</param>
        /// <returns>The ordinals, in order.</returns>
        static IReadOnlyList<int> NonNullFields(JsonFactSet facts, Sort sort, IReadOnlyList<Sql.CosmosPath?> fields, IReadOnlyList<CosmosOrdering> candidates)
        {
            var all = new List<int>();
            var rowFields = sort.getInput().getRowType().getFieldList();

            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i] is not Sql.CosmosPath path)
                    continue;

                // A field the plan already types as non-nullable needs nothing said about it, and
                // saying it anyway would put a promoted column in a list whose purpose is to name the
                // ones the plan would otherwise have refused.
                if (i < rowFields.size() &&
                    ((org.apache.calcite.rel.type.RelDataTypeField)rowFields.get(i)).getType().isNullable() == false)
                    continue;

                if (string.Equals(path.Alias, CosmosImplementor.DefaultRootAlias, StringComparison.Ordinal) == false)
                    continue;

                if (Metadata.CosmosDocumentPaths.From(path) is JsonDocumentPath document && NeverNull(facts, document))
                    all.Add(i);
            }

            // And the computed case, which binds to no path and could therefore never qualify above.
            // See AlwaysDefined: a geodesic distance over declared geographies is neither null nor
            // undefined, which is what the placement needs and the only thing that gives it.
            for (var i = 0; i < candidates.Count; i++)
                if (all.Contains(i) == false && AlwaysDefined(facts, candidates[i]))
                    all.Add(i);

            all.Sort();
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
        static bool NeverNull(JsonFactSet facts, JsonDocumentPath path) =>
            facts.IsAlwaysScalar(path);

        /// <summary>
        /// Determines whether a sortable computed key can be neither null nor undefined, from what the
        /// container declares about the paths it reads.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two ways a distance fails to be a number, and the declaration has to close both.</b> It
        /// is <em>null</em> where an operand is — <c>JSON_QUERY</c> answering null over an absent
        /// path, a JSON null or a scalar — and it is <em>undefined</em> at the service where the
        /// operand is an object the service will not measure. Those are different sets, measured:
        /// <c>{"kind":"somewhere"}</c> and <c>{"type":"Point","coordinates":[999,999]}</c> are both
        /// objects and both answer undefined. A claim about the <em>type</em> at the path closes the
        /// first and leaves the second, which is a key that still arrives undefined and still sorts at
        /// the wrong end of the placement. <see cref="JsonFactSet.IsAlwaysGeography"/> is
        /// the claim that closes both, and it is the only one that does.
        /// </para>
        /// <para>
        /// <b>Why the placement is what this is for.</b> Cosmos orders undefined first ascending and
        /// last descending and offers no control; Calcite's default is the opposite on both counts. A
        /// connection setting <c>defaultNullCollation=LOW</c> asks for the order the service already
        /// produces and the sort pushes without any of this — which is what the README tells a caller
        /// to do. What a declaration adds is the default: an ORM writes a bare <c>ORDER BY</c> and
        /// cannot be told to write anything else, and a key that can be neither null nor undefined has
        /// no placement to disagree about, so it pushes under either collation. The declaration is a
        /// <c>$ref</c> to a published GeoJSON geometry schema — see <see cref="Metadata.CosmosGeography"/>.
        /// </para>
        /// <para>
        /// <b>And nothing is rewritten to make it true.</b> An earlier attempt added
        /// <c>ST_ISVALID</c> to the statement so that undefined keys could not arrive; that removes
        /// rows the query defines and the plan would have raised over, which is a wrong answer rather
        /// than a slower one. A declaration that the paths hold geographies says those rows do not
        /// exist. If one does, the container contradicted its own declaration — the single thing this
        /// model has always said it cannot check in advance.
        /// </para>
        /// </remarks>
        static bool AlwaysDefined(JsonFactSet facts, CosmosOrdering candidate)
        {
            if (candidate.Expression == false || candidate.Operands is not { } operands)
                return false;

            foreach (var operand in operands)
                if (Metadata.CosmosDocumentPaths.From(operand) is not JsonDocumentPath document
                    || facts.IsAlwaysGeography(document) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosSortRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode? convert(RelNode rel)
        {
            var sort = (Sort)rel;

            if (CosmosImplementor.TryBindOutput(sort.getInput(), out var fields, out _, out var candidates, out _, out var container) == false)
                return null;

            // Built under the facts the legality test decided under, which the node asks again.
            var facts = Facts(sort, container);

            return new CosmosSort(
                sort.getCluster(),
                sort.getTraitSet().replace(@out),
                convert(sort.getInput(), sort.getInput().getTraitSet().replace(@out)),
                sort.getCollation(),
                sort.offset,
                sort.fetch,
                NonNullFields(facts, sort, fields, candidates),
                facts);
        }

    }

}
