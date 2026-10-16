using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    public partial class CosmosSortTests
    {

        /// <summary>
        /// Covers the pure resolution logic the Rel nodes and their rules share. The nodes themselves
        /// require a table and schema layer that does not yet exist.
        /// </summary>
        public class Resolution
        {

            readonly JavaTypeFactoryImpl _types = new();
            readonly RexBuilder _rex;

            public Resolution()
            {
                _rex = new RexBuilder(_types);
            }

            static readonly CosmosPath[] Fields =
            {
                CosmosPath.Root("c"),                                                   // 0 — the document column
                CosmosPath.Root("c").Property("name"),                                  // 1 — nullable
                CosmosPath.Root("c").Property("inventory").Property("quantity"),        // 2 — nullable
                CosmosPath.Root("c").Property("id"),                                    // 3 — NOT NULL
            };

            org.apache.calcite.rel.type.RelDataType RowType()
            {
                org.apache.calcite.rel.type.RelDataType Nullable(SqlTypeName name) =>
                    _types.createTypeWithNullability(_types.createSqlType(name), true);

                return _types.builder()
                    .add("DOC", Nullable(SqlTypeName.ANY))
                    .add("name", Nullable(SqlTypeName.VARCHAR))
                    .add("quantity", Nullable(SqlTypeName.INTEGER))
                    .add("id", _types.createSqlType(SqlTypeName.VARCHAR))
                    .build();
            }

            /// <remarks>
            /// Null placement is stated explicitly as UNSPECIFIED so these cases isolate path
            /// resolution. Calcite's own defaults conflict with Cosmos in both directions, which is
            /// covered separately below.
            /// </remarks>
            static RelCollation Collation(params (int Index, RelFieldCollation.Direction Direction)[] keys)
            {
                var list = new java.util.ArrayList();
                foreach (var (index, direction) in keys)
                    list.add(new RelFieldCollation(index, direction, RelFieldCollation.NullDirection.UNSPECIFIED));

                return RelCollations.of(list);
            }

            static RelCollation Collation(int index, RelFieldCollation.Direction direction, RelFieldCollation.NullDirection nulls)
            {
                var list = new java.util.ArrayList();
                list.add(new RelFieldCollation(index, direction, nulls));
                return RelCollations.of(list);
            }

            // ── Field binding ─────────────────────────────────────────────────────────

            [Fact]
            public void TheDocumentColumnBindsToTheDocumentRoot()
            {
                var rowType = _types.builder()
                    .add(CosmosImplementor.DocumentColumnName, SqlTypeName.VARCHAR)
                    .build();

                CosmosImplementor.BindFields(rowType).Should().ContainSingle().Which!.ToString().Should().Be("c");
            }

            [Fact]
            public void PromotedColumnsBindToTheirProperties()
            {
                var rowType = _types.builder()
                    .add(CosmosImplementor.DocumentColumnName, SqlTypeName.VARCHAR)
                    .add("id", SqlTypeName.VARCHAR)
                    .add("_ts", SqlTypeName.BIGINT)
                    .build();

                var fields = CosmosImplementor.BindFields(rowType);

                fields[0]!.ToString().Should().Be("c");
                fields[1]!.ToString().Should().Be("c.id");
                fields[2]!.ToString().Should().Be("c._ts");
            }

            [Fact]
            public void BindingHonoursTheRootAlias()
            {
                var rowType = _types.builder().add("id", SqlTypeName.VARCHAR).build();
                CosmosImplementor.BindFields(rowType, "p")[0]!.ToString().Should().Be("p.id");
            }

            // ── Path resolution ───────────────────────────────────────────────────────

            CosmosRexTranslator Translator() => new(_rex, Fields, new CosmosParameterList(), JsonFactSet.Empty);

            [Fact]
            public void FieldReferenceResolvesToAPath()
            {
                Translator().TryResolvePath(_rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 1), out var path).Should().BeTrue();
                path!.ToString().Should().Be("c.name");
            }

            [Fact]
            public void ItemChainResolvesToAPath()
            {
                var map = _rex.makeInputRef(_types.createSqlType(SqlTypeName.ANY), 0);
                var inner = _rex.makeCall(SqlStdOperatorTable.ITEM, map, _rex.makeLiteral("address", _types.createSqlType(SqlTypeName.VARCHAR, 7)));
                var outer = _rex.makeCall(SqlStdOperatorTable.ITEM, inner, _rex.makeLiteral("city", _types.createSqlType(SqlTypeName.VARCHAR, 4)));

                Translator().TryResolvePath(outer, out var path).Should().BeTrue();
                path!.ToString().Should().Be("c.address.city");
            }

            [Fact]
            public void ComputedExpressionDoesNotResolveToAPath()
            {
                var node = _rex.makeCall(
                    SqlStdOperatorTable.UPPER,
                    _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 1));

                Translator().TryResolvePath(node, out var path).Should().BeFalse();
                path.Should().BeNull();
            }

            // ── Sort key resolution ───────────────────────────────────────────────────

            [Fact]
            public void SortKeysResolveToPolicyPaths()
            {
                CosmosSort.TryResolveSortKeys(
                    Collation((1, RelFieldCollation.Direction.ASCENDING), (2, RelFieldCollation.Direction.DESCENDING)),
                    Fields,
                    RowType(),
                    "c",
                    out var keys,
                    out var paths).Should().BeTrue();

                keys.Should().SatisfyRespectively(
                    k => { k.Path.Should().Be("/name"); k.Descending.Should().BeFalse(); },
                    k => { k.Path.Should().Be("/inventory/quantity"); k.Descending.Should().BeTrue(); });

                paths[1].ToString().Should().Be("c.inventory.quantity");
            }

            /// <remarks>
            /// A promoted VARIANT column (a declared path; see CosmosTable.getRowType and #163) is
            /// declined as a sort key. Calcite cannot order one in process — <c>VariantValue</c> is not
            /// Comparable and names no order across types — so a pushed sort over it has no oracle, and
            /// the SQL standard defines no ordering for a variant to match. A caller orders by
            /// <c>JSON_VALUE</c> over the path instead, which is text, and which the service orders only
            /// where the path is declared a string — see <see cref="AKeyNotReadAsStoredIsRefused"/>.
            /// See #165.
            /// </remarks>
            [Fact]
            public void AVariantColumnIsRefusedAsASortKey()
            {
                var fields = new List<CosmosPath?>
                {
                    CosmosPath.Root("c"),                        // 0 — the document column
                    CosmosPath.Root("c").Property("name"),       // 1 — a plain VARCHAR path, the control
                    CosmosPath.Root("c").Property("category"),   // 2 — the promoted VARIANT path
                };

                var rowType = _types.builder()
                    .add(CosmosImplementor.DocumentColumnName, _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true))
                    .add("name", _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true))
                    .add("$.category", _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARIANT), true))
                    .build();

                // The VARCHAR path resolves — the control that isolates the type as the cause.
                CosmosSort.TryResolveSortKeys(Collation((1, RelFieldCollation.Direction.ASCENDING)), fields, rowType, "c", out _, out _)
                    .Should().BeTrue();

                // The VARIANT column is declined.
                CosmosSort.TryResolveSortKeys(Collation((2, RelFieldCollation.Direction.ASCENDING)), fields, rowType, "c", out _, out _)
                    .Should().BeFalse();
            }

            /// <remarks>
            /// The service orders the path and SQL the column, which is one order only where the column
            /// is the stored value — a text accessor over an undeclared path is not, and ordering by its
            /// path ordered the stored values by JSON type where SQL orders their text. What the column
            /// is comes in per ordinal, asked by the rule and the node of
            /// <see cref="CosmosRexTranslator.IsReadAsStored"/>; this is only that the answer is obeyed,
            /// and that one key failing it fails the collation.
            /// </remarks>
            [Fact]
            public void AKeyNotReadAsStoredIsRefused()
            {
                var stored = new[] { false, false, true, true };

                bool Resolve(params int[] ordinals)
                {
                    var collation = new (int, RelFieldCollation.Direction)[ordinals.Length];
                    for (var i = 0; i < ordinals.Length; i++)
                        collation[i] = (ordinals[i], RelFieldCollation.Direction.ASCENDING);

                    return CosmosSort.TryResolveSortKeys(Collation(collation), Fields, RowType(), "c", null, null, null, null, stored, out _, out _);
                }

                Resolve(2).Should().BeTrue("the column is the stored value");
                Resolve(1).Should().BeFalse("the column is a rendering of the stored value");
                Resolve(0).Should().BeFalse("the column is the document's text");
                Resolve(2, 1).Should().BeFalse("one key the service would order otherwise is the whole collation");
            }

            /// <remarks>
            /// <para>
            /// What the rule and the node ask per ordinal, of fields all read as declared. A field that
            /// cannot be null is the service's own and the value at its path; a <c>VARIANT</c> column is
            /// the raw value. A nullable field typed a number or a boolean is what a <c>RETURNING</c>
            /// accessor binds, which reads null for an object or an array the service sorts after every
            /// scalar — so it is the stored value only where the container declares the path that type,
            /// or null, outright. Not <c>DECIMAL</c>, which parses rather than asserting and addresses no
            /// path at all (<see cref="CosmosRexTranslator.IsParsingJsonValue"/>), so no field is bound
            /// to one.
            /// </para>
            /// <para>
            /// A declaration of something else, or of nothing about the path, is no declaration of the
            /// type; and one that admits the type beside another admits the other too.
            /// </para>
            /// </remarks>
            [Fact]
            public void ATypedAccessorIsReadAsStoredOnlyWhereItsTypeIsDeclared()
            {
                var fields = new List<CosmosPath?>
                {
                    CosmosPath.Root("c").Property("_ts"),       // 0 — BIGINT NOT NULL
                    CosmosPath.Root("c").Property("v"),         // 1 — VARIANT
                    CosmosPath.Root("c").Property("n"),         // 2 — INTEGER
                    CosmosPath.Root("c").Property("b"),         // 3 — BOOLEAN
                };

                org.apache.calcite.rel.type.RelDataType Nullable(SqlTypeName name) =>
                    _types.createTypeWithNullability(_types.createSqlType(name), true);

                var rowType = _types.builder()
                    .add("_ts", _types.createSqlType(SqlTypeName.BIGINT))
                    .add("v", Nullable(SqlTypeName.VARIANT))
                    .add("n", Nullable(SqlTypeName.INTEGER))
                    .add("b", Nullable(SqlTypeName.BOOLEAN))
                    .build();

                var readings = new[] { CosmosReading.Typed, CosmosReading.Typed, CosmosReading.Typed, CosmosReading.Typed };

                IReadOnlyList<bool> Stored(string? schema)
                {
                    var container = new Adapter.Metadata.CosmosContainerMetadata("items", new[] { "/ref" });
                    if (schema is not null)
                        container = container.WithFacts(Adapter.Metadata.CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(schema)));

                    return CosmosSort.StoredFields(new CosmosRexTranslator(_rex, fields, new CosmosParameterList(), CosmosOracle.For(container.Facts).Declared, null, container, readings), rowType);
                }

                Stored(null).Should().Equal(true, true, false, false);
                Stored("""{ "type": "object", "properties": { "n": { "type": ["integer", "null"] }, "b": { "type": "boolean" } } }""")
                    .Should().Equal(true, true, true, true);
                Stored("""{ "type": "object", "properties": { "n": { "type": "number" } } }""")
                    .Should().Equal(true, true, true, false);
                Stored("""{ "type": "object", "properties": { "n": { "type": "string" }, "b": { "type": "integer" } } }""")
                    .Should().Equal(true, true, false, false);
                Stored("""{ "type": "object", "properties": { "n": { "type": ["integer", "object"] }, "b": { "type": ["boolean", "string"] } } }""")
                    .Should().Equal(true, true, false, false);
            }

            /// <remarks>
            /// An array is refused however it is read: the service orders arrays by a rule of its own,
            /// measured to put <c>[]</c> after <c>[1, 5]</c>, which is not SQL's element-wise order.
            /// </remarks>
            [Fact]
            public void ACollectionIsRefusedAsASortKey()
            {
                var fields = new List<CosmosPath?>
                {
                    CosmosPath.Root("c"),                        // 0 — the document column
                    CosmosPath.Root("c").Property("tags"),       // 1 — an array read as declared
                };

                var rowType = _types.builder()
                    .add(CosmosImplementor.DocumentColumnName, _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true))
                    .add("tags", _types.createTypeWithNullability(_types.createArrayType(_types.createSqlType(SqlTypeName.VARCHAR), -1), true))
                    .build();

                CosmosSort.TryResolveSortKeys(Collation((1, RelFieldCollation.Direction.ASCENDING)), fields, rowType, "c", null, null, null, null, new[] { true, true }, out _, out _)
                    .Should().BeFalse();
            }

            [Fact]
            public void StrictDirectionsAreTreatedAsPlainDirections()
            {
                CosmosSort.TryResolveSortKeys(
                    Collation((1, RelFieldCollation.Direction.STRICTLY_DESCENDING)),
                    Fields,
                    RowType(),
                    "c",
                    out var keys,
                    out _).Should().BeTrue();

                keys[0].Descending.Should().BeTrue();
            }

            /// <remarks>
            /// Cosmos ORDER BY offers only ASC and DESC; a clustered collation has no equivalent.
            /// </remarks>
            [Fact]
            public void ClusteredCollationIsRefused()
            {
                CosmosSort.TryResolveSortKeys(
                    Collation((1, RelFieldCollation.Direction.CLUSTERED)),
                    Fields,
                    RowType(),
                    "c",
                    out _,
                    out _).Should().BeFalse();
            }

            [Fact]
            public void OutOfRangeFieldIsRefused()
            {
                CosmosSort.TryResolveSortKeys(
                    Collation((9, RelFieldCollation.Direction.ASCENDING)),
                    Fields,
                    RowType(),
                    "c",
                    out _,
                    out _).Should().BeFalse();
            }

            [Fact]
            public void EmptyCollationResolvesToNoKeys()
            {
                CosmosSort.TryResolveSortKeys(RelCollations.EMPTY, Fields, RowType(), "c", out var keys, out _).Should().BeTrue();
                keys.Should().BeEmpty();
            }

            // ── Null placement ────────────────────────────────────────────────────────
            //
            // Cosmos orders JSON types totally — undefined < null < boolean < number < string <
            // array < object — and DESC is the exact reverse of ASC. Nulls therefore sort first
            // ascending and last descending, with no way to ask for anything else. Verified against
            // the emulator; see DESIGN.md.

            bool Resolves(RelCollation collation) =>
                CosmosSort.TryResolveSortKeys(collation, Fields, RowType(), "c", out _, out _);

            bool Resolves(RelCollation collation, params int[] nonNullFields) =>
                CosmosSort.TryResolveSortKeys(collation, Fields, RowType(), "c", nonNullFields, null, null, null, null, out _, out _);

            // ── A placement the plan has already settled ──────────────────────────────
            //
            // A predicate that removes the nulls removes the disagreement about where they go. What the
            // row type says a field may hold is then the weaker fact; what the rows being sorted
            // actually hold is the one that decides.

            /// <remarks>
            /// The two placements refused above, accepted once the ordinal is guaranteed non-null. This
            /// is the whole of the change: the same collation, the same nullable row type, a different
            /// answer.
            /// </remarks>
            [Fact]
            public void AGuaranteedNonNullKeyAcceptsEitherPlacement()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST), 1).Should().BeTrue();
                Resolves(Collation(1, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.FIRST), 1).Should().BeTrue();
            }

            /// <remarks>
            /// Calcite's defaults are the two placements Cosmos cannot honour, so this is the shape an
            /// ordinary <c>ORDER BY name</c> arrives in.
            /// </remarks>
            [Fact]
            public void AGuaranteedNonNullKeyAcceptsCalciteDefaultPlacement()
            {
                var ascending = new java.util.ArrayList();
                ascending.add(new RelFieldCollation(1, RelFieldCollation.Direction.ASCENDING));
                Resolves(RelCollations.of(ascending), 1).Should().BeTrue();

                var descending = new java.util.ArrayList();
                descending.add(new RelFieldCollation(1, RelFieldCollation.Direction.DESCENDING));
                Resolves(RelCollations.of(descending), 1).Should().BeTrue();
            }

            /// <remarks>
            /// The guarantee is per ordinal and not a blanket one. A predicate over one column says
            /// nothing about the column beside it.
            /// </remarks>
            [Fact]
            public void AGuaranteeOverAnotherOrdinalDoesNotReachTheKey()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST), 2).Should().BeFalse();
            }

            /// <remarks>
            /// Every key has to be covered, not merely one of them. The container declares no composite
            /// index here, so this is about resolution alone; index legality is decided separately.
            /// </remarks>
            [Fact]
            public void AMultiKeySortNeedsTheGuaranteeOnEveryNullableKey()
            {
                var partial = new java.util.ArrayList();
                partial.add(new RelFieldCollation(1, RelFieldCollation.Direction.ASCENDING));
                partial.add(new RelFieldCollation(2, RelFieldCollation.Direction.ASCENDING));

                CosmosSort.TryResolveSortKeys(RelCollations.of(partial), Fields, RowType(), "c", new[] { 1 }, null, null, null, null, out _, out _).Should().BeFalse();
                CosmosSort.TryResolveSortKeys(RelCollations.of(partial), Fields, RowType(), "c", new[] { 1, 2 }, null, null, null, null, out _, out _).Should().BeTrue();
            }

            /// <remarks>
            /// An empty guarantee is the state before any predicate, and has to leave the rule exactly
            /// as it was rather than weakening it.
            /// </remarks>
            [Fact]
            public void NoGuaranteeLeavesThePlacementRuleUnchanged()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST), System.Array.Empty<int>()).Should().BeFalse();
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.FIRST), System.Array.Empty<int>()).Should().BeTrue();
            }

            // ── Keys rooted at an array-traversal alias ───────────────────────────────
            //
            // The service refuses to order by one at all. Measured against Azure, which rejects both
            // `ORDER BY t0` and `ORDER BY t0.x` over `JOIN t0 IN c.tags` with a 400 while accepting the
            // same JOIN ordered by a container path. The emulator accepts all three, which is why a
            // single-key allowance stood here for so long.
            //
            // There is a second reason it could never have been right: a path rooted at an unnest alias is
            // relative to the array element, so its policy form ("/key" from "t0.key") is not comparable
            // with the container's index paths ("/tags/[]/key"), and comparing them could match a composite
            // index that does not in fact serve the sort.

            static readonly CosmosPath[] UnnestFields =
            {
                CosmosPath.Root("c"),                            // 0
                CosmosPath.Root("c").Property("id"),             // 1
                CosmosPath.Root("t0").Property("key"),           // 2 — element-relative
            };

            org.apache.calcite.rel.type.RelDataType UnnestRowType() => _types.builder()
                .add("DOC", _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.ANY), true))
                .add("id", _types.createSqlType(SqlTypeName.VARCHAR))
                .add("element", _types.createSqlType(SqlTypeName.VARCHAR))
                .build();

            /// <remarks>
            /// Refused however few keys there are: the service rejects ordering by an element alias
            /// outright, so there is no arity at which it becomes legal.
            /// </remarks>
            [Fact]
            public void SingleKeyOnAnUnnestAliasIsRefused()
            {
                CosmosSort.TryResolveSortKeys(
                    Collation((2, RelFieldCollation.Direction.ASCENDING)),
                    UnnestFields,
                    UnnestRowType(),
                    "c",
                    out _,
                    out _).Should().BeFalse();
            }

            /// <remarks>
            /// Its index-addressability cannot be established, so the sort must not be pushed down.
            /// </remarks>
            [Fact]
            public void MultiKeyInvolvingAnUnnestAliasIsRefused()
            {
                CosmosSort.TryResolveSortKeys(
                    Collation((1, RelFieldCollation.Direction.ASCENDING), (2, RelFieldCollation.Direction.ASCENDING)),
                    UnnestFields,
                    UnnestRowType(),
                    "c",
                    out _,
                    out _).Should().BeFalse();
            }

            [Fact]
            public void AscendingWithNullsFirstIsAccepted()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.FIRST)).Should().BeTrue();
            }

            [Fact]
            public void DescendingWithNullsLastIsAccepted()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.LAST)).Should().BeTrue();
            }

            [Fact]
            public void UnspecifiedNullPlacementIsAccepted()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.UNSPECIFIED)).Should().BeTrue();
                Resolves(Collation(1, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.UNSPECIFIED)).Should().BeTrue();
            }

            /// <remarks>
            /// Cosmos cannot place nulls last while ascending. Pushing this down would return rows in
            /// an order the plan did not ask for — a silent wrong answer rather than a failure.
            /// </remarks>
            [Fact]
            public void AscendingWithNullsLastIsRefused()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST)).Should().BeFalse();
            }

            [Fact]
            public void DescendingWithNullsFirstIsRefused()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.FIRST)).Should().BeFalse();
            }

            /// <remarks>
            /// Calcite defaults ascending to nulls last and descending to nulls first — the opposite of
            /// Cosmos on both counts. Sorting a nullable key therefore cannot be pushed down unless the
            /// plan explicitly asks for Cosmos's own placement.
            /// </remarks>
            [Fact]
            public void CalciteDefaultPlacementConflictsOnANullableKey()
            {
                var ascending = new java.util.ArrayList();
                ascending.add(new RelFieldCollation(1, RelFieldCollation.Direction.ASCENDING));
                Resolves(RelCollations.of(ascending)).Should().BeFalse();

                var descending = new java.util.ArrayList();
                descending.add(new RelFieldCollation(1, RelFieldCollation.Direction.DESCENDING));
                Resolves(RelCollations.of(descending)).Should().BeFalse();
            }

            /// <remarks>
            /// A key that cannot be null has no null placement to disagree about, so the default
            /// collation pushes down normally. In practice this is what keeps sorting on <c>id</c> and
            /// the system properties available.
            /// </remarks>
            [Fact]
            public void NonNullableKeyAcceptsAnyPlacement()
            {
                var list = new java.util.ArrayList();
                list.add(new RelFieldCollation(3, RelFieldCollation.Direction.ASCENDING));
                Resolves(RelCollations.of(list)).Should().BeTrue();

                Resolves(Collation(3, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST)).Should().BeTrue();
                Resolves(Collation(3, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.FIRST)).Should().BeTrue();
            }

        }

    }

}
