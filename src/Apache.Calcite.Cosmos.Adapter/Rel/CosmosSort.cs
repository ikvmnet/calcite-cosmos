using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

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
        /// Finds the fields of an input the plan itself guarantees are never null, by reading the
        /// predicates that hold over every row it produces.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The null-placement rule below refuses a nullable key because Cosmos and Calcite disagree
        /// about where nulls go. A predicate that removes the nulls removes the disagreement with
        /// them: a key that cannot be null in the rows being sorted has no placement left to be
        /// wrong about, whichever way each side would have placed one.
        /// </para>
        /// <para>
        /// <b>Both senses of absent have to go, and <c>IS NOT NULL</c> takes both.</b> Cosmos
        /// distinguishes a property holding JSON <c>null</c> from a property that is not there, and
        /// sorts <c>undefined</c> below <c>null</c> below everything else. The adapter renders SQL
        /// <c>IS NOT NULL</c> as <c>IS_DEFINED(p) AND NOT IS_NULL(p)</c> — see <c>DESIGN.md</c> —
        /// which excludes exactly those two, so the guarantee is whole rather than half of one.
        /// </para>
        /// <para>
        /// Only the explicit <c>IS NOT NULL</c> form is read. A comparison such as
        /// <c>c.v &gt; 'a'</c> also drops nulls under SQL's three-valued logic, and appears to under
        /// Cosmos's rule that a comparison across types yields <c>undefined</c> — but that rule is
        /// unmeasured here, and over a path typed <c>ANY</c> the values compared are whatever the
        /// documents happen to hold. A wrong answer is the failure mode, so the wider form waits on
        /// evidence.
        /// </para>
        /// <para>
        /// <b>This reaches promoted columns and not paths inside the map column</b>, and the reason
        /// is structural rather than about nullability. <c>RelMdPredicates</c> carries a predicate
        /// through a projection only where the projection is a <see cref="RexInputRef"/>: a
        /// promoted column projects as a plain reference and its predicate survives, while a
        /// document path projects as <c>ITEM($0, 'name')</c> over the map column — not a reference,
        /// and over an input the projection does not output — so the predicate is dropped.
        /// Measured; see <c>DESIGN.md</c>.
        /// </para>
        /// </remarks>
        /// <param name="input">The node whose rows are being sorted.</param>
        /// <param name="mq">The metadata query to ask.</param>
        /// <returns>The ordinals guaranteed non-null, ascending; empty if none, or if either argument is <c>null</c>.</returns>
        public static IReadOnlyList<int> FindNonNullFields(RelNode input, RelMetadataQuery mq)
        {
            if (input is null || mq is null)
                return System.Array.Empty<int>();

            var predicates = mq.getPulledUpPredicates(input);
            if (predicates is null)
                return System.Array.Empty<int>();

            var found = new List<int>();
            var pulled = predicates.pulledUpPredicates;

            for (var i = 0; i < pulled.size(); i++)
            {
                var conjunctions = RelOptUtil.conjunctions((RexNode)pulled.get(i));

                for (var j = 0; j < conjunctions.size(); j++)
                {
                    if ((RexNode)conjunctions.get(j) is not RexCall call)
                        continue;

                    if ((SqlKind.__Enum)call.getKind().ordinal() != SqlKind.__Enum.IS_NOT_NULL)
                        continue;

                    var operands = call.getOperands();
                    if (operands.size() != 1 || (RexNode)operands.get(0) is not RexInputRef reference)
                        continue;

                    if (found.Contains(reference.getIndex()) == false)
                        found.Add(reference.getIndex());
                }
            }

            found.Sort();
            return found;
        }

        /// <summary>
        /// Determines whether an ordinal is among the fields guaranteed non-null.
        /// </summary>
        static bool IsGuaranteedNonNull(IReadOnlyList<int>? nonNullFields, int index)
        {
            if (nonNullFields is null)
                return false;

            for (var i = 0; i < nonNullFields.Count; i++)
                if (nonNullFields[i] == index)
                    return true;

            return false;
        }

        /// <summary>
        /// Resolves a collation into sort keys expressed as policy-form paths.
        /// </summary>
        /// <remarks>
        /// Every key must denote a path: <c>ORDER BY</c> legality is decided against the
        /// container's composite indexes, which are declared over paths. A collation over a
        /// computed expression cannot be checked and so cannot be pushed down.
        /// </remarks>
        /// <param name="collation">The requested collation.</param>
        /// <param name="fields">The ordinal-to-path binding of the input.</param>
        /// <param name="rowType">The input row type, consulted for the nullability of each key.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="keys">On success, the resolved keys in order.</param>
        /// <param name="paths">On success, the resolved paths in order.</param>
        /// <returns><c>true</c> if every key resolved; otherwise <c>false</c>.</returns>
        public static bool TryResolveSortKeys(RelCollation collation, IReadOnlyList<CosmosPath?> fields, org.apache.calcite.rel.type.RelDataType rowType, string rootAlias, out IReadOnlyList<CosmosSortKey> keys, out IReadOnlyList<CosmosPath> paths)
        {
            return TryResolveSortKeys(collation, fields, rowType, rootAlias, null, out keys, out paths);
        }

        /// <summary>
        /// Resolves a collation into sort keys expressed as policy-form paths, taking the plan's own
        /// guarantee that certain fields are never null.
        /// </summary>
        /// <remarks>
        /// The row type states what a field <em>may</em> hold; <paramref name="nonNullFields"/>
        /// states what the rows being sorted actually do, which for a null placement is the
        /// stronger fact. See <see cref="FindNonNullFields"/> for where it comes from and what it
        /// reaches.
        /// </remarks>
        /// <param name="collation">The requested collation.</param>
        /// <param name="fields">The ordinal-to-path binding of the input.</param>
        /// <param name="rowType">The input row type, consulted for the nullability of each key.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="nonNullFields">Ordinals the plan guarantees are never null, or <c>null</c>.</param>
        /// <param name="keys">On success, the resolved keys in order.</param>
        /// <param name="paths">On success, the resolved paths in order.</param>
        /// <returns><c>true</c> if every key resolved; otherwise <c>false</c>.</returns>
        public static bool TryResolveSortKeys(RelCollation collation, IReadOnlyList<CosmosPath?> fields, org.apache.calcite.rel.type.RelDataType rowType, string rootAlias, IReadOnlyList<int>? nonNullFields, out IReadOnlyList<CosmosSortKey> keys, out IReadOnlyList<CosmosPath> paths)
        {
            keys = System.Array.Empty<CosmosSortKey>();
            paths = System.Array.Empty<CosmosPath>();

            if (collation is null || fields is null || rowType is null || string.IsNullOrEmpty(rootAlias))
                return false;

            var typeFields = rowType.getFieldList();
            var collations = collation.getFieldCollations();
            var resolvedKeys = new CosmosSortKey[collations.size()];
            var resolvedPaths = new CosmosPath[collations.size()];

            for (var i = 0; i < collations.size(); i++)
            {
                var field = (RelFieldCollation)collations.get(i);
                var index = field.getFieldIndex();
                if (index < 0 || index >= fields.Count || index >= typeFields.size())
                    return false;

                var nullable = ((org.apache.calcite.rel.type.RelDataTypeField)typeFields.get(index)).getType().isNullable()
                    && IsGuaranteedNonNull(nonNullFields, index) == false;

                if (TryGetDescending(field, nullable, out var descending) == false)
                    return false;

                // A key over a computed projection has no path to sort by. Cosmos cannot order by a
                // projection alias, so there is nothing to fall back to and the sort is declined.
                var path = fields[index];
                if (path is null)
                    return false;

                // A path rooted at an array-traversal alias is relative to the element rather than the
                // container, and the service refuses to order by one at all — measured against Azure,
                // which rejects both `ORDER BY t0` and `ORDER BY t0.x` with a 400 while accepting the
                // same JOIN ordered by a container path. The emulator accepts all three, which is why
                // this stood as a single-key allowance for so long.
                if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                    return false;

                resolvedPaths[i] = path;
                resolvedKeys[i] = new CosmosSortKey(path.ToPolicyPath(), descending);
            }

            keys = resolvedKeys;
            paths = resolvedPaths;
            return true;
        }

        /// <summary>
        /// Maps a field collation onto a plain ascending or descending flag, refusing any
        /// collation whose null placement Cosmos cannot honour.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cosmos <c>ORDER BY</c> offers only <c>ASC</c> and <c>DESC</c>. Clustered collations have
        /// no equivalent and are refused.
        /// </para>
        /// <para>
        /// Cosmos also offers no control over where nulls sort. Its ordering is a total order over
        /// JSON types — <c>undefined</c> &lt; <c>null</c> &lt; boolean &lt; number &lt; string &lt;
        /// array &lt; object — and <c>DESC</c> is the exact reverse of <c>ASC</c>. Missing and null
        /// properties therefore sort below everything ascending and above everything descending,
        /// which is precisely nulls-first ascending and nulls-last descending.
        /// </para>
        /// <para>
        /// Calcite's defaults are the opposite on both counts: ascending defaults to nulls last and
        /// descending to nulls first. The placement is therefore only honourable when the key
        /// cannot be null at all, or when the plan happens to ask for Cosmos's own order. On a
        /// nullable key a conflicting placement is refused, because pushing it down would return
        /// rows in an order the plan did not ask for — a wrong answer rather than a failure.
        /// </para>
        /// <para>
        /// Verified empirically against the Cosmos emulator; see <c>DESIGN.md</c>.
        /// </para>
        /// </remarks>
        static bool TryGetDescending(RelFieldCollation field, bool nullable, out bool descending)
        {
            switch ((RelFieldCollation.Direction.__Enum)field.getDirection().ordinal())
            {
                case RelFieldCollation.Direction.__Enum.ASCENDING:
                case RelFieldCollation.Direction.__Enum.STRICTLY_ASCENDING:
                    descending = false;
                    break;
                case RelFieldCollation.Direction.__Enum.DESCENDING:
                case RelFieldCollation.Direction.__Enum.STRICTLY_DESCENDING:
                    descending = true;
                    break;
                default:
                    descending = false;
                    return false;
            }

            // A key that cannot be null has no null placement to disagree about.
            if (nullable == false)
                return true;

            switch ((RelFieldCollation.NullDirection.__Enum)field.nullDirection.ordinal())
            {
                case RelFieldCollation.NullDirection.__Enum.UNSPECIFIED:
                    return true;
                case RelFieldCollation.NullDirection.__Enum.FIRST:
                    return descending == false;
                case RelFieldCollation.NullDirection.__Enum.LAST:
                    return descending;
                default:
                    return false;
            }
        }

        readonly IReadOnlyList<int> _nonNullFields;

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
        /// Initializes a new instance carrying the fields the plan guarantees are never null.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The guarantee is taken at construction and never recomputed.</b> It is read from the
        /// predicates over the node's input, and what that question answers depends on which
        /// equivalent of the input the metadata is asked about: measured, the same query answers
        /// with the predicate while the input is still logical and with nothing once the input has
        /// been converted. Deciding once, where the rule decides, is what keeps the rule and
        /// <see cref="Implement"/> agreeing — the property <see cref="Convert.CosmosSortRule"/>
        /// exists to hold, and one an answer re-derived later would break by throwing on a plan the
        /// planner had already chosen.
        /// </para>
        /// <para>
        /// It is sound to carry: every member of an equivalence set produces the same rows, so a
        /// predicate that holds over the input the rule saw holds over whichever input the planner
        /// finally picks.
        /// </para>
        /// </remarks>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="collation">The requested collation.</param>
        /// <param name="offset">The number of rows to skip, or <c>null</c>.</param>
        /// <param name="fetch">The maximum number of rows to return, or <c>null</c>.</param>
        /// <param name="nonNullFields">Ordinals of the input the plan guarantees are never null, or <c>null</c>.</param>
        public CosmosSort(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RelCollation collation, RexNode? offset, RexNode? fetch, IReadOnlyList<int>? nonNullFields) :
            base(cluster, traitSet, input, collation, offset, fetch)
        {
            _nonNullFields = nonNullFields ?? System.Array.Empty<int>();
        }

        /// <summary>
        /// Gets the ordinals of the input the plan guarantees are never null.
        /// </summary>
        public IReadOnlyList<int> NonNullFields => _nonNullFields;

        /// <inheritdoc />
        public override Sort copy(RelTraitSet traitSet, RelNode newInput, RelCollation newCollation, RexNode? offset, RexNode? fetch)
        {
            return new CosmosSort(getCluster(), traitSet, newInput, newCollation, offset, fetch, _nonNullFields);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The guarantee belongs in the digest rather than beside it. Two sorts alike in collation,
        /// offset and fetch render to different statements when one of them may push a key the
        /// other may not, and a planner that conflated them would keep whichever it saw first.
        /// </remarks>
        public override RelWriter explainTerms(RelWriter pw)
        {
            return base.explainTerms(pw).itemIf("nonNull", string.Join(", ", _nonNullFields), _nonNullFields.Count > 0);
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

            if (TryResolveSortKeys(getCollation(), implementor.Fields, getInput().getRowType(), implementor.RootAlias, _nonNullFields, out var keys, out var paths) == false)
                throw new CosmosTranslationException("The sort keys do not resolve to document paths.");

            if (implementor.Container.IsSortSupported(keys) == false)
                throw new CosmosTranslationException("The container has no composite index supporting this sort.");

            for (var i = 0; i < keys.Count; i++)
                implementor.Query.AddOrderBy(paths[i].ToString(), keys[i].Descending);

            if (offset is not null)
                implementor.Query.Offset = RexLiteral.intValue(offset);
            if (fetch is not null)
                implementor.Query.Fetch = RexLiteral.intValue(fetch);
        }

    }

}
