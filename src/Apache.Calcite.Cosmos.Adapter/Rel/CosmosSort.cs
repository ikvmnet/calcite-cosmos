using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Sort implemented in the <see cref="CosmosConvention"/> calling convention, rendered as an
    /// <c>ORDER BY</c> clause together with <c>OFFSET</c>/<c>LIMIT</c>.
    /// </summary>
    public class CosmosSort : Sort, CosmosRel
    {

        /// <summary>
        /// Resolves a collation into the keys the service is handed, asking <see cref="CosmosServiceOrder"/> whether the
        /// order it computes over them is one Calcite allows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two questions, and only one of them is about order.</b> What a key <em>is</em> is structural: a path the
        /// column binds, a conversion of one a projection recorded, or a geodesic distance written out a second time —
        /// see <see cref="CosmosImplementor.SortableExpressions"/> — and nothing else, since Cosmos cannot order by a
        /// projection alias. Whether the service's order of it is Calcite's is a question about the facts, asked of the
        /// facts the sort was decided under, once, in <see cref="CosmosServiceOrder.OrderBy"/>: how the column reads the
        /// stored value, what characters and types the rows hold there, where its nulls go, and which keys the facts make
        /// one value, which are not written. Both the rule and <see cref="Implement"/> come here, with the same binding
        /// and the same facts, so the two cannot disagree.
        /// </para>
        /// <para>
        /// <b>What the service accepts is asked by the caller</b>, of the keys this writes: every key's path indexed, and a
        /// composite index for more than one — see <see cref="Metadata.CosmosContainerMetadata.IsSortSupported"/>.
        /// </para>
        /// </remarks>
        /// <param name="collation">The requested collation.</param>
        /// <param name="fields">The ordinal-to-path binding of the input.</param>
        /// <param name="readings">How each ordinal is read back; an ordinal past the end is read as declared.</param>
        /// <param name="orderings">What each ordinal holds where it binds no path: a conversion of one, or a distance.</param>
        /// <param name="rowType">The input row type, which gives each key the type Calcite orders.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="facts">What every row sorted is known to satisfy (L1).</param>
        /// <param name="keys">On success, the keys written, as the service's policy names their paths.</param>
        /// <param name="paths">On success, per key written, its path, or <c>null</c> for a distance.</param>
        /// <param name="written">On success, per key written, its position in the collation.</param>
        /// <param name="checks">On success, per key written, how Calcite compares its values and what one has to be to
        /// certify the rows around it — <see cref="CosmosOrderAnchor.None"/> for every key of an order proved when the
        /// plan is made.</param>
        /// <returns><c>true</c> where every key resolved and the order is proved, or anchored.</returns>
        internal static bool TryResolveSortKeys(
            RelCollation collation,
            IReadOnlyList<CosmosPath?> fields,
            IReadOnlyList<CosmosReading> readings,
            IReadOnlyList<CosmosOrdering> orderings,
            org.apache.calcite.rel.type.RelDataType rowType,
            string rootAlias,
            JsonFactSet facts,
            out IReadOnlyList<CosmosSortKey> keys,
            out IReadOnlyList<CosmosPath?> paths,
            out IReadOnlyList<int> written,
            out IReadOnlyList<CosmosOrderedKey> ordered)
        {
            keys = System.Array.Empty<CosmosSortKey>();
            paths = System.Array.Empty<CosmosPath>();
            written = System.Array.Empty<int>();
            ordered = System.Array.Empty<CosmosOrderedKey>();

            if (collation is null || fields is null || readings is null || orderings is null || rowType is null || facts is null || string.IsNullOrEmpty(rootAlias))
                return false;

            var typeFields = rowType.getFieldList();
            var collations = collation.getFieldCollations();
            var operands = new CosmosOrderKey[collations.size()];
            var resolved = new CosmosPath?[collations.size()];

            for (var i = 0; i < collations.size(); i++)
            {
                var field = (RelFieldCollation)collations.get(i);
                var index = field.getFieldIndex();
                if (index < 0 || index >= fields.Count || index >= typeFields.size())
                    return false;

                if (TryOperand(index, fields, readings, orderings, rowType, rootAlias, out var operand, out var path) == false)
                    return false;

                // Measured: the service refuses a second key beside a distance, with the same 2206 a computed key gets
                // on its own. So this is the whole collation or nothing.
                if (operand.Kind == CosmosOrderOperandKind.Distance && collations.size() != 1)
                    return false;

                operands[i] = new CosmosOrderKey(operand, field);
                resolved[i] = path;
            }

            var verdict = CosmosServiceOrder.OrderBy(facts, operands);
            if (verdict.IsProved == false && verdict.IsAnchored == false)
                return false;

            var writtenKeys = new CosmosSortKey[verdict.Written.Count];
            var writtenPaths = new CosmosPath?[verdict.Written.Count];

            // The direction each key is written in is the one the question decided Calcite runs.
            for (var j = 0; j < verdict.Written.Count; j++)
            {
                writtenPaths[j] = resolved[verdict.Written[j]];
                writtenKeys[j] = new CosmosSortKey(writtenPaths[j]?.ToPolicyPath() ?? string.Empty, verdict.Keys[j].Descending);
            }

            keys = writtenKeys;
            paths = writtenPaths;
            written = verdict.Written;
            ordered = verdict.Keys;
            return true;
        }

        /// <summary>
        /// Builds the operand a key orders, from the binding alone: a path the ordinal binds, a conversion of one a
        /// projection recorded, or a distance; or answers <c>false</c> for a key the service cannot be handed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A path rooted at an array-traversal alias</b> is relative to the element rather than the container, and the
        /// service refuses to order by one at all — measured against Azure, which rejects both <c>ORDER BY t0</c> and
        /// <c>ORDER BY t0.x</c> with a 400 while accepting the same <c>JOIN</c> ordered by a container path. The emulator
        /// accepts all three, which is why this stood as a single-key allowance for so long.
        /// </para>
        /// <para>
        /// <b>A conversion is reached only where the ordinal binds nothing.</b> It is not a binding — nothing else may read
        /// it — so a filter or a partition key never sees it; whether its stored form orders as the converted value is the
        /// question's, see <see cref="CosmosOrderOperandKind.Conversion"/>.
        /// </para>
        /// </remarks>
        static bool TryOperand(int index, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings, IReadOnlyList<CosmosOrdering> orderings, org.apache.calcite.rel.type.RelDataType rowType, string rootAlias, out CosmosOrderOperand operand, out CosmosPath? path)
        {
            operand = null!;
            path = fields[index];

            var ordering = index < orderings.Count ? orderings[index] : default;

            if (path is not null)
            {
                if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false || Metadata.CosmosDocumentPaths.From(path) is not JsonDocumentPath document)
                    return false;

                var type = ((org.apache.calcite.rel.type.RelDataTypeField)rowType.getFieldList().get(index)).getType().getSqlTypeName();
                operand = CosmosOrderOperand.ForPath(document, type, index < readings.Count ? readings[index] : CosmosReading.Typed);
                return true;
            }

            if (ordering.Path is CosmosPath converted)
            {
                if (string.Equals(converted.Alias, rootAlias, StringComparison.Ordinal) == false || Metadata.CosmosDocumentPaths.From(converted) is not JsonDocumentPath document)
                    return false;

                path = converted;
                operand = CosmosOrderOperand.ForConversion(document, ordering.Format, ordering.Held);
                return true;
            }

            if (ordering.Expression)
            {
                var measured = new List<JsonDocumentPath>();
                var accounted = ordering.Operands is not null;

                foreach (var between in ordering.Operands ?? System.Array.Empty<CosmosPath>())
                    if (Metadata.CosmosDocumentPaths.From(between) is JsonDocumentPath document)
                        measured.Add(document);
                    else
                        accounted = false;

                operand = CosmosOrderOperand.ForDistance(accounted ? measured : null);
                return true;
            }

            // A computed column binds no path, and Cosmos cannot order by a projection alias.
            return false;
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="collation">The requested collation.</param>
        /// <param name="offset">The number of rows to skip, or <c>null</c>.</param>
        /// <param name="fetch">The maximum number of rows to return, or <c>null</c>.</param>
        public CosmosSort(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RelCollation collation, RexNode? offset, RexNode? fetch) :
            this(cluster, traitSet, input, collation, offset, fetch, null)
        {

        }

        /// <summary>
        /// Initializes a new instance carrying what the rows it orders are known to satisfy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Taken at construction and never recomputed.</b> The facts are what the filters beneath establish of every row
        /// the sort orders (L1), and which filters a walk of the input finds depends on which member of a set it reads,
        /// where implementation reads the one tree the planner chose. Deciding once, where the rule decides, is what keeps
        /// the rule and <see cref="Implement"/> agreeing — the property <see cref="Convert.CosmosSortRule"/> exists to
        /// hold, and one an answer re-derived later would break by throwing on a plan the planner had already chosen.
        /// </para>
        /// <para>
        /// It is sound to carry: every member of an equivalence set produces the same rows, so what held of the input the
        /// rule saw holds of whichever input the planner finally picks.
        /// </para>
        /// <para>
        /// <b>And whether its order is completed as the rows arrive</b>, which the digest shows as <c>order=anchored</c>.
        /// A sort is built only where its order was proved or anchored under its own facts, and implemented under them, so
        /// two sorts alike in collation, offset and fetch over one input answer rows Calcite allows whichever the planner
        /// keeps, even where one writes a constant key the other does not, or checks what the other proved; but an
        /// <c>EXPLAIN</c> that showed the two alike would hide which reads the rows as they come and which checks them.
        /// </para>
        /// </remarks>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="collation">The requested collation.</param>
        /// <param name="offset">The number of rows to skip, or <c>null</c>.</param>
        /// <param name="fetch">The maximum number of rows to return, or <c>null</c>.</param>
        /// <param name="facts">What every row the sort orders is known to satisfy, or <c>null</c> for the declaration.</param>
        /// <param name="anchored">Whether the order is proved only around anchors, and checked by the reader.</param>
        internal CosmosSort(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RelCollation collation, RexNode? offset, RexNode? fetch, JsonFactSet? facts, bool anchored = false) :
            base(cluster, traitSet, input, collation, offset, fetch)
        {
            Facts = facts;
            Anchored = anchored;
        }

        /// <summary>
        /// Gets what every row the sort orders is known to satisfy, as its rule decided; <c>null</c> where it was built
        /// without, which reads what the container declares of every document.
        /// </summary>
        internal JsonFactSet? Facts { get; }

        /// <summary>
        /// Gets whether the order is proved only around anchors, so that the reader checks the rows as they arrive, as its
        /// rule decided.
        /// </summary>
        internal bool Anchored { get; }

        /// <inheritdoc />
        public override Sort copy(RelTraitSet traitSet, RelNode newInput, RelCollation newCollation, RexNode? offset, RexNode? fetch)
        {
            return new CosmosSort(getCluster(), traitSet, newInput, newCollation, offset, fetch, Facts, Anchored);
        }

        /// <inheritdoc />
        public override RelWriter explainTerms(RelWriter pw)
        {
            return base.explainTerms(pw).itemIf("order", "anchored", Anchored);
        }

        /// <inheritdoc />
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            return base.computeSelfCost(planner, mq)?.multiplyBy(CosmosConvention.CostMultiplier);
        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            implementor.Visit(getInput());

            if (implementor.Query.HasOrderBy)
                throw new CosmosTranslationException("A sort has already been applied.");

            // Cosmos rejects GROUP BY and ORDER BY in the same statement.
            if (implementor.Query.HasGroupBy)
                throw new CosmosTranslationException("Cosmos SQL does not support ORDER BY together with GROUP BY.");

            // Every question the rule asked, asked again of the facts it decided under.
            var facts = implementor.FactsOf(Facts);

            if (TryResolveSortKeys(getCollation(), implementor.Fields, implementor.Readings, implementor.Orderings, getInput().getRowType(), implementor.RootAlias, facts, out var keys, out var paths, out var written, out var ordered) == false)
                throw new CosmosTranslationException("The sort keys do not resolve to an order the service computes as Calcite would.");

            if (implementor.Container.IsSortSupported(keys) == false)
                throw new CosmosTranslationException("The container does not index every key of this sort, or has no composite index supporting it.");

            var anchored = new List<(string Expression, CosmosOrderedKey Key)>(keys.Count);
            var names = getInput().getRowType().getFieldNames();

            for (var i = 0; i < keys.Count; i++)
            {
                // A path where there is one, and otherwise the expression the projection recorded,
                // written out again because the alias is not addressable.
                var collation = (RelFieldCollation)getCollation().getFieldCollations().get(written[i]);
                var index = collation.getFieldIndex();
                var expression = paths[i]?.ToString() ?? (index < implementor.SortableExpressions.Count ? implementor.SortableExpressions[index] : null);

                if (expression is null)
                    throw new CosmosTranslationException("A sort key resolves to neither a path nor an expression the service will order by.");

                implementor.Query.AddOrderBy(expression, ordered[i].Descending);

                // What the reader compares, where the order is completed as the rows arrive: the stored value of the
                // path, selected for it — or for a DISTINCT, the column it already selects, which reads the path's value
                // where it holds one of the key's type and null where it does not, as the key's own reading does.
                anchored.Add((expression, ordered[i] with { Member = implementor.Query.Distinct ? (string)names.get(index) : null }));
            }

            // An order the plan proved only around anchors, checked by the reader (CosmosOrderProgram); one it proved
            // outright is read as it comes.
            if (anchored.Exists(k => k.Key.Anchor != CosmosOrderAnchor.None))
                implementor.OrderAnchored(anchored);

            if (offset is not null)
                implementor.Query.Offset = implementor.RowLimit(offset);
            if (fetch is not null)
                implementor.Query.Fetch = implementor.RowLimit(fetch);
        }

    }

}
