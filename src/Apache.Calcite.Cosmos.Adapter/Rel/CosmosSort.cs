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
        public static bool TryResolveSortKeys(RelCollation collation, IReadOnlyList<CosmosPath?> fields, org.apache.calcite.rel.type.RelDataType rowType, string rootAlias, out IReadOnlyList<CosmosSortKey> keys, out IReadOnlyList<CosmosPath?> paths)
        {
            return TryResolveSortKeys(collation, fields, rowType, rootAlias, null, null, null, null, null, out keys, out paths);
        }

        /// <summary>
        /// Reads, per ordinal, whether the column is the value the service holds at the path it binds
        /// to, as it is read back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The service orders the path and SQL orders the column</b>, and the two are one order only
        /// where they are one value. A <c>JSON_VALUE</c> read as text binds the path beneath it, so the
        /// sort above it wrote <c>ORDER BY c.label</c> and the service ordered the raw values — by JSON
        /// type first, numbers numerically — where SQL orders their renderings as text. Measured on the
        /// emulator over the differential suite's typed container,
        /// <c>SELECT JSON_VALUE(c."DOC", '$.label') FROM typed AS c ORDER BY 1 NULLS FIRST</c> came
        /// back <c>[null, null, 'true', '30', 'bikes', 'huge', 'shoes', null, null]</c> where SQL
        /// answers <c>[null ×4, '30', 'bikes', 'huge', 'shoes', 'true']</c>, the guarded object and
        /// array reading null in the column and sorting last at the service; and over the price,
        /// <c>'7', '8', '9', '9', '30', '30.7', '30'</c> where text puts <c>'30'</c> first. A
        /// <c>JSON_QUERY</c> column and the document column are the same disagreement over objects
        /// and arrays, which the service orders by rules of its own.
        /// </para>
        /// <para>
        /// So the sort asks what the lookup join's restriction and a grouping key ask —
        /// <see cref="CosmosRexTranslator.IsReadAsStored"/>, whose one exception for text is a path what
        /// is known of the rows says holds a string — and the rule and the node each ask it of the
        /// binding their own walk produced and of the facts the rule decided under, so that the two
        /// cannot disagree.
        /// </para>
        /// <para>
        /// <b>A typed accessor is asked too, and it is the sort that needs it.</b> A <c>RETURNING</c>
        /// that asserts a type reads null for an object and an array, and the service sorts both after
        /// every scalar: over the typed container,
        /// <c>JSON_VALUE(c."DOC", '$.label' RETURNING INTEGER)</c> under <c>NULLS FIRST</c> and a page of
        /// three came back <c>[null, null, 30]</c> where SQL answers three nulls, and <c>RETURNING
        /// BOOLEAN</c> <c>[null, null, true]</c>. Unpaged, the same order failed the read on the object
        /// before the projection was guarded, and with the guard it would have answered in the wrong
        /// order. So the key is the stored value only where what is known of the rows sorted gives the
        /// path the type the clause names, or null. The grouping and the lookup take a typed accessor
        /// before asking, the one grouping by its guard and the other restricting by a typed parameter;
        /// ordering has nothing to take it by, the service ordering only a path.
        /// </para>
        /// </remarks>
        /// <param name="translator">A translator over the input's binding, readings and container.</param>
        /// <param name="rowType">The input's row type.</param>
        /// <returns>Whether each ordinal is read as stored.</returns>
        internal static IReadOnlyList<bool> StoredFields(CosmosRexTranslator translator, org.apache.calcite.rel.type.RelDataType rowType)
        {
            var stored = new bool[rowType.getFieldCount()];

            for (var i = 0; i < stored.Length; i++)
                stored[i] = translator.IsReadAsStored(RexInputRef.of(i, rowType));

            return stored;
        }

        /// <summary>
        /// Resolves a collation into sort keys expressed as policy-form paths, taking the plan's own
        /// guarantee that certain fields are never null.
        /// </summary>
        /// <remarks>
        /// The row type states what a field <em>may</em> hold; <paramref name="nonNullFields"/>
        /// states what the rows being sorted actually do, which for a null placement is the
        /// stronger fact: a key that cannot be null in the rows being sorted has no placement left to
        /// be wrong about, whichever way each side would have placed one. Both senses of absent have to
        /// go — the service sorts <c>undefined</c> below <c>null</c> below everything else — and a path
        /// known present and a scalar has neither. See <see cref="NonNullFields"/> for where it comes
        /// from.
        /// </remarks>
        /// <param name="collation">The requested collation.</param>
        /// <param name="fields">The ordinal-to-path binding of the input.</param>
        /// <param name="rowType">The input row type, consulted for the nullability of each key.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="nonNullFields">Ordinals the plan guarantees are never null, or <c>null</c>.</param>
        /// <param name="sortableFields">
        /// Ordinals holding an expression the service will order by, or <c>null</c>.
        /// </param>
        /// <param name="facts">
        /// What every row being sorted is known to satisfy, consulted for the stored form of a key the
        /// plan types as temporal. <c>null</c> knows nothing, and such a key is refused — see
        /// <see cref="OrderIsLexical"/>.
        /// </param>
        /// <param name="orderingPaths">
        /// Per ordinal, the path a computed column may be ordered by, or <c>null</c>. See
        /// <see cref="OrderingPaths"/>.
        /// </param>
        /// <param name="storedFields">
        /// Per ordinal, whether the column is the value the service holds at its path — see
        /// <see cref="StoredFields"/>. An ordinal that binds a path and is not is refused. <c>null</c>
        /// takes every field to be read as declared; the rule and the node always pass it.
        /// </param>
        /// <param name="keys">On success, the resolved keys in order.</param>
        /// <param name="paths">On success, the resolved paths in order.</param>
        /// <returns><c>true</c> if every key resolved; otherwise <c>false</c>.</returns>
        public static bool TryResolveSortKeys(RelCollation collation, IReadOnlyList<CosmosPath?> fields, org.apache.calcite.rel.type.RelDataType rowType, string rootAlias, IReadOnlyList<int>? nonNullFields, IReadOnlyList<bool>? sortableFields, JsonFactSet? facts, IReadOnlyList<CosmosPath?>? orderingPaths, IReadOnlyList<bool>? storedFields, out IReadOnlyList<CosmosSortKey> keys, out IReadOnlyList<CosmosPath?> paths)
        {
            keys = System.Array.Empty<CosmosSortKey>();
            paths = System.Array.Empty<CosmosPath>();

            if (collation is null || fields is null || rowType is null || string.IsNullOrEmpty(rootAlias))
                return false;

            var typeFields = rowType.getFieldList();
            var collations = collation.getFieldCollations();
            var resolvedKeys = new CosmosSortKey[collations.size()];
            var resolvedPaths = new CosmosPath?[collations.size()];

            for (var i = 0; i < collations.size(); i++)
            {
                var field = (RelFieldCollation)collations.get(i);
                var index = field.getFieldIndex();
                if (index < 0 || index >= fields.Count || index >= typeFields.size())
                    return false;

                // A computed column that converts an order-preserving path may still be ordered by
                // that path. It is not a binding — nothing else may read it — so it is consulted
                // only here, and only where the ordinal binds to nothing. See OrderingPaths.
                var ordered = fields[index] is null && orderingPaths is not null && index < orderingPaths.Count
                    ? orderingPaths[index]
                    : null;

                // Such an ordinal cannot be null, the claim behind it being that the path holds a
                // scalar in every document; so the placement the two sides would disagree about does
                // not arise, and the row type's nullability is not the stronger fact here.
                var nullable = ordered is null
                    && ((org.apache.calcite.rel.type.RelDataTypeField)typeFields.get(index)).getType().isNullable()
                    && IsGuaranteedNonNull(nonNullFields, index) == false;

                if (TryGetDescending(field, nullable, out var descending) == false)
                    return false;

                // A key over a computed projection has no path to sort by, because Cosmos cannot order
                // by a projection alias. The one exception is an expression the service accepts in the
                // clause, which the sort writes out a second time rather than referring to — see
                // CosmosImplementor.SortableExpressions for what qualifies and why nothing else does.
                var path = fields[index] ?? ordered;
                if (path is null)
                {
                    if (sortableFields is null || index >= sortableFields.Count || sortableFields[index] == false)
                        return false;

                    // Measured: the service refuses a second key beside one of these, with the same
                    // 2206 a computed key gets on its own. So this is the whole collation or nothing.
                    if (collations.size() != 1)
                        return false;

                    resolvedPaths[i] = null;
                    resolvedKeys[i] = new CosmosSortKey(string.Empty, descending);
                    continue;
                }

                // A path rooted at an array-traversal alias is relative to the element rather than the
                // container, and the service refuses to order by one at all — measured against Azure,
                // which rejects both `ORDER BY t0` and `ORDER BY t0.x` with a 400 while accepting the
                // same JOIN ordered by a container path. The emulator accepts all three, which is why
                // this stood as a single-key allowance for so long.
                if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                    return false;

                // The service orders the path and SQL the column, which is one order only where the
                // column is the stored value. Asked only where the ordinal binds directly: a key reached
                // through `ordered` is a conversion whose licence CosmosProject.IsOrderable decided, and
                // a computed column binds no path to be read any way at all. See StoredFields.
                if (fields[index] is not null && storedFields is not null && (index >= storedFields.Count || storedFields[index] == false))
                    return false;

                var type = ((org.apache.calcite.rel.type.RelDataTypeField)typeFields.get(index)).getType().getSqlTypeName();

                // A promoted VARIANT column (a declared path; see CosmosTable.getRowType) is declined
                // rather than pushed. Calcite cannot order one in process -- VariantValue is not
                // Comparable and names no order across types -- so no oracle can measure a pushed sort,
                // and the SQL standard defines no ordering for a variant to match. Declining leaves the
                // adapter behaving as the engine does. A caller orders by JSON_VALUE over the path
                // instead, which orders at the service only where the container declares the path the
                // type the accessor reads -- a string for the text accessor, the type a RETURNING names
                // otherwise -- see StoredFields. See #165, and #163 for the type.
                if (type == org.apache.calcite.sql.type.SqlTypeName.VARIANT)
                    return false;

                // Nor a collection, read as declared or not. The service orders arrays by a rule of its
                // own that is not SQL's element-wise order -- measured on the emulator, [] sorted after
                // [1, 5] and before [2] -- and the column is the list SQL compares, so the stored value
                // being the column is not enough here.
                if (type == org.apache.calcite.sql.type.SqlTypeName.ARRAY
                    || type == org.apache.calcite.sql.type.SqlTypeName.MULTISET
                    || type == org.apache.calcite.sql.type.SqlTypeName.MAP)
                    return false;

                // Cosmos has no temporal type, so a key the plan types as one is a string at the
                // service and the ORDER BY compares it lexically. See OrderIsLexical.
                //
                // Asked only where the ordinal binds directly. A key reached through `ordered` is a
                // chain whose licence was decided by CosmosProject.IsOrderable -- which asks the same
                // two questions and, for a parse, asks the format instead of the engine.
                if (ordered is null && IsTemporal(type) && OrderIsLexical(facts, path, type) == false)
                    return false;

                resolvedPaths[i] = path;
                resolvedKeys[i] = new CosmosSortKey(path.ToPolicyPath(), descending);
            }

            keys = resolvedKeys;
            paths = resolvedPaths;
            return true;
        }

        /// <summary>
        /// Determines whether a key the plan types as temporal is one the service orders the way the
        /// plan means.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The question exists because Cosmos has no date type.</b> A path the plan reads as a
        /// <c>TIMESTAMP</c> — through <c>CAST</c>, or through <c>JSON_VALUE … RETURNING TIMESTAMP</c>,
        /// or through a view's parse — holds a JSON <em>string</em>, and <c>ORDER BY</c> over it is a
        /// lexicographic string comparison. That is chronological order only where every value at the
        /// path shares one fixed shape, which is exactly what
        /// <see cref="Metadata.CosmosRepresentation.PreservesOrder"/> records and what nothing else
        /// says. Mixed precision sorts <c>'…:56.5Z'</c> before <c>'…:56Z'</c> — <c>'.'</c> is 0x2E and
        /// <c>'Z'</c> is 0x5A — and a mixed <c>Z</c>/offset breaks it the same way.
        /// </para>
        /// <para>
        /// <b>Refusing without a container is the safe direction and not a compromise.</b> Declining a
        /// sort that would have been correct costs an in-process ordering; pushing one that is not
        /// returns the rows in the wrong order and nothing downstream notices. The .NET SDK's default
        /// serializer writes exactly the mixed path this refuses, so the unconfined case is the common
        /// one rather than the exotic one.
        /// </para>
        /// <para>
        /// <b>Of the rows being sorted.</b> A sort carries no predicate of its own, and what it may lean
        /// on is what the filters beneath establish of every row it orders (L1): a form the declaration
        /// states only of one kind of document is the key's form beneath a filter keeping that kind.
        /// </para>
        /// </remarks>
        /// <param name="facts">What every row being sorted is known to satisfy, or <c>null</c>.</param>
        /// <param name="path">The path the key resolved to.</param>
        /// <param name="type">The type the plan gives the key, which is what the engine would read into.</param>
        /// <returns><c>true</c> where the stored form makes the lexical order the plan's order.</returns>
        static bool OrderIsLexical(JsonFactSet? facts, CosmosPath path, org.apache.calcite.sql.type.SqlTypeName type)
        {
            if (facts is null)
                return false;

            if (Metadata.CosmosDocumentPaths.From(path) is not JsonDocumentPath document)
                return false;

            if (facts.RepresentationOf(document) is not Metadata.CosmosRepresentation representation
                || representation.PreservesOrder == false)
                return false;

            // And the engine has to be able to compute the key at all. A key it cannot compute is a
            // query that raises, and ordering the container by the stored strings answers rows
            // instead -- which is a different query rather than a faster one. Measured: every
            // ISO-8601 instant raises, whether the plan reached it by a cast or by a RETURNING
            // clause. See CosmosTemporalForms.EngineReads.
            return Metadata.CosmosStoredForms.EngineReads(representation, Metadata.CosmosTemporalParse.PartsOf(type));
        }

        /// <summary>
        /// Reads which output ordinals the rows being sorted license ordering by, where the ordinal binds
        /// to no path of its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>No walk of its own, and that is the point.</b> Which ordinal holds which candidate is
        /// structural: the rule reads it off the one walk <see cref="CosmosImplementor.TryBindOutput(RelNode?, out IReadOnlyList{CosmosPath?}, out IReadOnlyList{CosmosReading}, out IReadOnlyList{CosmosOrdering}, out CosmosClauses)"/>
        /// makes, and implementation off what the projection beneath recorded
        /// (<see cref="CosmosImplementor.Orderings"/>), which it derives the same way. An earlier
        /// version of the rule walked again, and unwrapped a subset a second time on a justification
        /// weaker than the binding's: that any member <em>binds</em> the same because members are
        /// equivalent, which is a claim about the binding and not about which expressions one
        /// representative happens to carry.
        /// </para>
        /// <para>
        /// What is left is a lookup of the candidates in the facts the sort was decided under — the
        /// filters beneath the sort, which may be more than the projection's own — asked by the rule
        /// and again by <see cref="Implement"/> of the same facts, so the two cannot disagree. See
        /// <see cref="CosmosProject.IsOrderable"/>.
        /// </para>
        /// </remarks>
        /// <param name="facts">What every row being sorted is known to satisfy.</param>
        /// <param name="candidates">The candidates, per ordinal.</param>
        /// <returns>The path per ordinal, or <c>null</c> where the facts license none.</returns>
        internal static IReadOnlyList<CosmosPath?> OrderingPaths(JsonFactSet facts, IReadOnlyList<CosmosOrdering> candidates)
        {
            var ordering = new CosmosPath?[candidates.Count];

            for (var i = 0; i < candidates.Count; i++)
                ordering[i] = CosmosProject.IsOrderable(facts, candidates[i].Path, candidates[i].Format, candidates[i].Held)
                    ? candidates[i].Path
                    : null;

            return ordering;
        }

        /// <summary>
        /// Determines whether a SQL type is one Cosmos has no equivalent of and stores as a string.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><c>true</c> for a date or a time.</returns>
        static bool IsTemporal(org.apache.calcite.sql.type.SqlTypeName type) =>
            type == org.apache.calcite.sql.type.SqlTypeName.DATE
            || type == org.apache.calcite.sql.type.SqlTypeName.TIME
            || type == org.apache.calcite.sql.type.SqlTypeName.TIME_WITH_LOCAL_TIME_ZONE
            || type == org.apache.calcite.sql.type.SqlTypeName.TIMESTAMP
            || type == org.apache.calcite.sql.type.SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE;

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
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="collation">The requested collation.</param>
        /// <param name="offset">The number of rows to skip, or <c>null</c>.</param>
        /// <param name="fetch">The maximum number of rows to return, or <c>null</c>.</param>
        /// <param name="nonNullFields">Ordinals of the input the plan guarantees are never null, or <c>null</c>.</param>
        public CosmosSort(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RelCollation collation, RexNode? offset, RexNode? fetch, IReadOnlyList<int>? nonNullFields) :
            this(cluster, traitSet, input, collation, offset, fetch, nonNullFields, null)
        {

        }

        /// <summary>
        /// Initializes a new instance carrying the fields the plan guarantees are never null, and what the rows it
        /// orders are known to satisfy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Both are taken at construction and never recomputed.</b> They are what the filters beneath establish of
        /// every row the sort orders (L1), and which filters a walk of the input finds depends on which member of a set it
        /// reads, where implementation reads the one tree the planner chose. Deciding once, where the rule decides, is
        /// what keeps the rule and <see cref="Implement"/> agreeing — the property <see cref="Convert.CosmosSortRule"/>
        /// exists to hold, and one an answer re-derived later would break by throwing on a plan the planner had already
        /// chosen. The guarantee was read off Calcite's pulled-up predicates before it was read off the oracle, and that
        /// question was measured to answer with the predicate while the input was logical and with nothing once it had
        /// been converted.
        /// </para>
        /// <para>
        /// It is sound to carry: every member of an equivalence set produces the same rows, so what held of the input the
        /// rule saw holds of whichever input the planner finally picks.
        /// </para>
        /// </remarks>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="collation">The requested collation.</param>
        /// <param name="offset">The number of rows to skip, or <c>null</c>.</param>
        /// <param name="fetch">The maximum number of rows to return, or <c>null</c>.</param>
        /// <param name="nonNullFields">Ordinals of the input the plan guarantees are never null, or <c>null</c>.</param>
        /// <param name="facts">What every row the sort orders is known to satisfy, or <c>null</c> for the declaration.</param>
        internal CosmosSort(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RelCollation collation, RexNode? offset, RexNode? fetch, IReadOnlyList<int>? nonNullFields, JsonFactSet? facts) :
            base(cluster, traitSet, input, collation, offset, fetch)
        {
            _nonNullFields = nonNullFields ?? System.Array.Empty<int>();
            Facts = facts;
        }

        /// <summary>
        /// Gets the ordinals of the input the plan guarantees are never null.
        /// </summary>
        public IReadOnlyList<int> NonNullFields => _nonNullFields;

        /// <summary>
        /// Gets what every row the sort orders is known to satisfy, as its rule decided; <c>null</c> where it was built
        /// without, which reads what the container declares of every document.
        /// </summary>
        internal JsonFactSet? Facts { get; }

        /// <inheritdoc />
        public override Sort copy(RelTraitSet traitSet, RelNode newInput, RelCollation newCollation, RexNode? offset, RexNode? fetch)
        {
            return new CosmosSort(getCluster(), traitSet, newInput, newCollation, offset, fetch, _nonNullFields, Facts);
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

            var sortable = new bool[implementor.SortableExpressions.Count];
            for (var i = 0; i < sortable.Length; i++)
                sortable[i] = implementor.SortableExpressions[i] is not null;

            // Every question the rule asked, asked again of the facts it decided under.
            var facts = Facts ?? implementor.UnconditionalFacts;
            var stored = StoredFields(implementor.CreateTranslator(null, facts), getInput().getRowType());

            if (TryResolveSortKeys(getCollation(), implementor.Fields, getInput().getRowType(), implementor.RootAlias, _nonNullFields, sortable, facts, OrderingPaths(facts, implementor.Orderings), stored, out var keys, out var paths) == false)
                throw new CosmosTranslationException("The sort keys do not resolve to document paths.");

            if (implementor.Container.IsSortSupported(keys) == false)
                throw new CosmosTranslationException("The container has no composite index supporting this sort.");

            for (var i = 0; i < keys.Count; i++)
            {
                // A path where there is one, and otherwise the expression the projection recorded,
                // written out again because the alias is not addressable.
                var index = ((RelFieldCollation)getCollation().getFieldCollations().get(i)).getFieldIndex();
                var expression = paths[i]?.ToString() ?? implementor.SortableExpressions[index];

                if (expression is null)
                    throw new CosmosTranslationException("A sort key resolves to neither a path nor an expression the service will order by.");

                implementor.Query.AddOrderBy(expression, keys[i].Descending);
            }

            if (offset is not null)
                implementor.Query.Offset = implementor.RowLimit(offset);
            if (fetch is not null)
                implementor.Query.Fetch = implementor.RowLimit(fetch);
        }

    }

}
