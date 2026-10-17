using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

using Apache.Calcite.Cosmos.Adapter.Metadata;
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
        /// Covers the pure resolution logic the Rel nodes and their rules share: what a key is, from the binding, and
        /// whether the service's order of it is one Calcite allows, from the facts — see <see cref="CosmosServiceOrder"/>.
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
                CosmosPath.Root("c").Property("name"),                                  // 1 — text
                CosmosPath.Root("c").Property("inventory").Property("quantity"),        // 2 — an INTEGER accessor
                CosmosPath.Root("c").Property("id"),                                    // 3 — the service's id
            };

            static readonly CosmosReading[] Readings = { CosmosReading.Json, CosmosReading.Text, CosmosReading.Typed, CosmosReading.Typed };

            org.apache.calcite.rel.type.RelDataType RowType()
            {
                org.apache.calcite.rel.type.RelDataType Nullable(SqlTypeName name) =>
                    _types.createTypeWithNullability(_types.createSqlType(name), true);

                return _types.builder()
                    .add("DOC", Nullable(SqlTypeName.VARCHAR))
                    .add("name", Nullable(SqlTypeName.VARCHAR))
                    .add("quantity", Nullable(SqlTypeName.INTEGER))
                    .add("id", _types.createSqlType(SqlTypeName.VARCHAR))
                    .build();
            }

            /// <summary>
            /// The facts a container declaring this schema states of every document, the service's own among them.
            /// </summary>
            static JsonFactSet Declared(string? schema = null)
            {
                var container = new CosmosContainerMetadata("items", new[] { "/pk" });
                if (schema is not null)
                    container = container.WithFacts(CosmosSchemaRecognition.ReadFrom(JsonNode.Parse(schema)!));

                return CosmosOracle.For(container.Facts).Declared;
            }

            /// <summary>
            /// A name drawn from two strings below U+D800 and present, and a quantity an integer and present: every key
            /// of <see cref="Fields"/> provable under any placement.
            /// </summary>
            const string Settled = """
                { "type": "object", "required": ["name", "inventory"],
                  "properties": {
                    "name": { "enum": ["a", "B"] },
                    "inventory": { "type": "object", "required": ["quantity"], "properties": { "quantity": { "type": "integer" } } } } }
                """;

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

            bool Resolves(RelCollation collation, JsonFactSet facts) =>
                CosmosSort.TryResolveSortKeys(collation, Fields, Readings, Array.Empty<CosmosOrdering>(), RowType(), "c", facts, out _, out _, out _);

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
                    Readings,
                    Array.Empty<CosmosOrdering>(),
                    RowType(),
                    "c",
                    Declared(Settled),
                    out var keys,
                    out var paths,
                    out var written).Should().BeTrue();

                keys.Should().SatisfyRespectively(
                    k => { k.Path.Should().Be("/name"); k.Descending.Should().BeFalse(); },
                    k => { k.Path.Should().Be("/inventory/quantity"); k.Descending.Should().BeTrue(); });

                paths[1]!.ToString().Should().Be("c.inventory.quantity");
                written.Should().Equal(0, 1);
            }

            /// <remarks>
            /// A promoted VARIANT column (a declared path; see CosmosTable.getRowType and #163) is declined as a sort key.
            /// Calcite cannot order one in process — <c>VariantValue</c> is not Comparable and names no order across types
            /// — so a pushed sort over it has no oracle, and the SQL standard defines no ordering for a variant to match. A
            /// caller orders by <c>JSON_VALUE</c> over the path instead. See #165.
            /// </remarks>
            [Fact]
            public void AVariantColumnIsRefusedAsASortKey()
            {
                var fields = new List<CosmosPath?>
                {
                    CosmosPath.Root("c"),                        // 0 — the document column
                    CosmosPath.Root("c").Property("name"),       // 1 — text, the control
                    CosmosPath.Root("c").Property("category"),   // 2 — the promoted VARIANT path
                };

                var rowType = _types.builder()
                    .add(CosmosImplementor.DocumentColumnName, _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true))
                    .add("name", _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARCHAR), true))
                    .add("$.category", _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.VARIANT), true))
                    .build();

                var facts = Declared("""{ "type": "object", "required": ["name", "category"], "properties": { "name": { "enum": ["a", "b"] }, "category": { "enum": ["x", "y"] } } }""");

                bool Resolves(int ordinal) =>
                    CosmosSort.TryResolveSortKeys(Collation((ordinal, RelFieldCollation.Direction.ASCENDING)), fields, new[] { CosmosReading.Json, CosmosReading.Text, CosmosReading.Typed }, Array.Empty<CosmosOrdering>(), rowType, "c", facts, out _, out _, out _);

                Resolves(1).Should().BeTrue("the text path resolves, the control that isolates the type as the cause");
                Resolves(2).Should().BeFalse("a VARIANT has no order in Calcite, whatever the path holds");
            }

            /// <remarks>
            /// The service orders the path and SQL the column, which is one order only where the column reads the stored
            /// values as they order: a text accessor over a path that may hold a number is a rendering, and the service
            /// orders the number by type and value where Calcite orders its text. The document column is JSON text the
            /// service orders by no rule SQL shares. One key failing fails the collation.
            /// </remarks>
            [Fact]
            public void AKeyNotReadAsTheStoredOrderIsRefused()
            {
                var facts = Declared("""{ "type": "object", "required": ["name", "inventory"], "properties": { "name": { "type": ["string", "integer"] }, "inventory": { "type": "object", "required": ["quantity"], "properties": { "quantity": { "type": "integer" } } } } }""");

                Resolves(Collation((2, RelFieldCollation.Direction.ASCENDING)), facts).Should().BeTrue("an integer read as one");
                Resolves(Collation((1, RelFieldCollation.Direction.ASCENDING)), facts).Should().BeFalse("a rendering of what may be a number");
                Resolves(Collation((0, RelFieldCollation.Direction.ASCENDING)), facts).Should().BeFalse("the document's text");
                Resolves(Collation((2, RelFieldCollation.Direction.ASCENDING), (1, RelFieldCollation.Direction.ASCENDING)), facts).Should().BeFalse("one key the service orders otherwise is the whole collation");
            }

            /// <remarks>
            /// <para>
            /// A typed accessor reads null for every type but its own, and the service orders those types where they
            /// fall: below the numbers or above them. So an <c>INTEGER</c> key over a path declared nothing has nulls on
            /// both sides of its values and no placement is Calcite's; declared an integer or null, both nulls are below,
            /// which is Calcite's under <c>NULLS FIRST</c> ascending; declared an integer and present, there is no null.
            /// </para>
            /// <para>
            /// A path declared to hold nothing the key reads has no values to order, every row reading null, so any
            /// order of the last key is one Calcite allows.
            /// </para>
            /// </remarks>
            [Fact]
            public void ATypedAccessorOrdersWhereItsNullClassLiesOnOneSide()
            {
                bool Integer(string? schema, RelFieldCollation.NullDirection nulls) =>
                    Resolves(Collation(2, RelFieldCollation.Direction.ASCENDING, nulls), Declared(schema));

                static string Quantity(string declared, bool required) => $$"""{ "type": "object", "required": ["inventory"], "properties": { "inventory": { "type": "object", "required": [{{(required ? "\"quantity\"" : "")}}], "properties": { "quantity": {{declared}} } } } }""";

                Integer(null, RelFieldCollation.NullDirection.FIRST).Should().BeFalse("undeclared, the path may hold an array above the numbers");
                Integer(Quantity("""{ "type": ["integer", "null"] }""", false), RelFieldCollation.NullDirection.FIRST).Should().BeTrue();
                Integer(Quantity("""{ "type": ["integer", "null"] }""", false), RelFieldCollation.NullDirection.LAST).Should().BeFalse();
                Integer(Quantity("""{ "type": "integer" }""", true), RelFieldCollation.NullDirection.LAST).Should().BeTrue("present and an integer, nothing reads null");
                Integer(Quantity("""{ "type": "integer" }""", true), RelFieldCollation.NullDirection.FIRST).Should().BeTrue();
                Integer(Quantity("""{ "type": "string" }""", true), RelFieldCollation.NullDirection.LAST).Should().BeTrue("every row reads null, and Calcite ties them all");
            }

            /// <remarks>
            /// An array is refused however it is read: the service orders arrays by a rule of its own, measured to put
            /// <c>[]</c> after <c>[1, 5]</c>, which is not SQL's element-wise order.
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

                CosmosSort.TryResolveSortKeys(Collation((1, RelFieldCollation.Direction.ASCENDING)), fields, new[] { CosmosReading.Json, CosmosReading.Typed }, Array.Empty<CosmosOrdering>(), rowType, "c", Declared("""{ "type": "object", "required": ["tags"], "properties": { "tags": { "type": "array" } } }"""), out _, out _, out _)
                    .Should().BeFalse();
            }

            /// <remarks>
            /// Calcite's runtime runs only <c>DESCENDING</c> descending: a strictly descending key runs ascending, so
            /// writing <c>DESC</c> for it would hand the service the other order. A strictly ascending one runs ascending.
            /// </remarks>
            [Fact]
            public void AStrictlyDescendingKeyIsRefusedAndAStrictlyAscendingOneIsAscending()
            {
                Resolves(Collation((2, RelFieldCollation.Direction.STRICTLY_DESCENDING)), Declared(Settled)).Should().BeFalse();

                CosmosSort.TryResolveSortKeys(Collation((2, RelFieldCollation.Direction.STRICTLY_ASCENDING)), Fields, Readings, Array.Empty<CosmosOrdering>(), RowType(), "c", Declared(Settled), out var keys, out _, out _)
                    .Should().BeTrue();
                keys[0].Descending.Should().BeFalse();
            }

            /// <remarks>
            /// Cosmos ORDER BY offers only ASC and DESC; a clustered collation has no equivalent.
            /// </remarks>
            [Fact]
            public void ClusteredCollationIsRefused()
            {
                Resolves(Collation((2, RelFieldCollation.Direction.CLUSTERED)), Declared(Settled)).Should().BeFalse();
            }

            [Fact]
            public void OutOfRangeFieldIsRefused()
            {
                Resolves(Collation((9, RelFieldCollation.Direction.ASCENDING)), Declared(Settled)).Should().BeFalse();
            }

            [Fact]
            public void EmptyCollationResolvesToNoKeys()
            {
                CosmosSort.TryResolveSortKeys(RelCollations.EMPTY, Fields, Readings, Array.Empty<CosmosOrdering>(), RowType(), "c", Declared(), out var keys, out _, out _).Should().BeTrue();
                keys.Should().BeEmpty();
            }

            // ── Characters ────────────────────────────────────────────────────────────
            //
            // The service orders a document's strings by code point, and Calcite and the SDK's merge of two partitions by
            // UTF-16 code unit; the two part only where a surrogate meets a unit from U+E000.

            /// <remarks>
            /// <c>id</c> may hold any character but <c>/</c> and <c>\</c>, so its order is not proved by anything the
            /// service states; the values a declaration or a filter pins it to are. Measured on the probe account, a
            /// cross-partition <c>ORDER BY c.id</c> over U+E000, U+FF21 and U+1F600 came back in neither order.
            /// </remarks>
            [Fact]
            public void AnIdIsOrderedOnlyWhereItsCharactersAreKnown()
            {
                Resolves(Collation((3, RelFieldCollation.Direction.ASCENDING)), Declared()).Should().BeFalse();
                Resolves(Collation((3, RelFieldCollation.Direction.ASCENDING)), Declared("""{ "type": "object", "properties": { "id": { "enum": ["a", "b", "é"] } } }""")).Should().BeTrue();
                Resolves(Collation((3, RelFieldCollation.Direction.ASCENDING)), Declared("""{ "type": "object", "properties": { "id": { "enum": ["", "Ａ"] } } }""")).Should().BeTrue("no surrogate");
                Resolves(Collation((3, RelFieldCollation.Direction.ASCENDING)), Declared("""{ "type": "object", "properties": { "id": { "enum": ["", "😀"] } } }""")).Should().BeFalse("U+E000 and U+1F600 order apart");
            }

            /// <remarks>
            /// The service's own properties are paths like any other, and what lets them order is what the service states
            /// of them: <c>_ts</c> an integer below 2^53, <c>_etag</c> an entity tag with no unit at or above U+D800.
            /// </remarks>
            [Fact]
            public void TheServicesPropertiesOrderByWhatTheServiceStates()
            {
                var fields = new List<CosmosPath?> { CosmosPath.Root("c").Property("_ts"), CosmosPath.Root("c").Property("_etag") };
                var rowType = _types.builder()
                    .add("_ts", _types.createSqlType(SqlTypeName.BIGINT))
                    .add("_etag", _types.createSqlType(SqlTypeName.VARCHAR))
                    .build();

                bool Resolves(int ordinal, RelFieldCollation.Direction direction) =>
                    CosmosSort.TryResolveSortKeys(Collation((ordinal, direction)), fields, new[] { CosmosReading.Typed, CosmosReading.Typed }, Array.Empty<CosmosOrdering>(), rowType, "c", Declared(), out _, out _, out _);

                Resolves(0, RelFieldCollation.Direction.DESCENDING).Should().BeTrue("_ts is a BIGINT the service bounds");
                Resolves(1, RelFieldCollation.Direction.ASCENDING).Should().BeTrue("_etag's characters are stated, though no default policy lets it be ordered");
            }

            // ── Null placement ────────────────────────────────────────────────────────
            //
            // Cosmos orders JSON types totally — undefined < null < boolean < number < string < array < object — and DESC
            // is the exact reverse of ASC, measured on the emulator and on Azure. So what a key reads as null sorts where
            // its type falls, and a placement is Calcite's only where that is the end Calcite puts nulls.

            static string Name(string declared, bool required) => $$"""{ "type": "object", "required": [{{(required ? "\"name\"" : "")}}], "properties": { "name": {{declared}} } }""";

            [Fact]
            public void AscendingWithNullsFirstIsAccepted()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.FIRST), Declared(Name("""{ "enum": ["a", "b", null] }""", false))).Should().BeTrue();
            }

            [Fact]
            public void DescendingWithNullsLastIsAccepted()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.LAST), Declared(Name("""{ "enum": ["a", "b", null] }""", false))).Should().BeTrue();
            }

            /// <remarks>
            /// Calcite runs an unspecified placement last in both directions, so ascending it is not the service's.
            /// </remarks>
            [Fact]
            public void AnUnspecifiedPlacementIsLast()
            {
                var facts = Declared(Name("""{ "enum": ["a", "b"] }""", false));

                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.UNSPECIFIED), facts).Should().BeFalse();
                Resolves(Collation(1, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.UNSPECIFIED), facts).Should().BeTrue();
            }

            /// <remarks>
            /// Cosmos cannot place an absent path last while ascending. Pushing this down would return rows in an order
            /// the plan did not ask for — a silent wrong answer rather than a failure.
            /// </remarks>
            [Fact]
            public void AscendingWithNullsLastIsRefused()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST), Declared(Name("""{ "enum": ["a", "b"] }""", false))).Should().BeFalse();
            }

            [Fact]
            public void DescendingWithNullsFirstIsRefused()
            {
                Resolves(Collation(1, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.FIRST), Declared(Name("""{ "enum": ["a", "b"] }""", false))).Should().BeFalse();
            }

            /// <remarks>
            /// A key the rows hold a value at in every document has no null to place, so Calcite's defaults — the two
            /// placements the service cannot honour — push.
            /// </remarks>
            [Fact]
            public void AKeyNeverNullAcceptsAnyPlacement()
            {
                var facts = Declared(Name("""{ "enum": ["a", "b"] }""", true));

                var ascending = new java.util.ArrayList();
                ascending.add(new RelFieldCollation(1, RelFieldCollation.Direction.ASCENDING));
                Resolves(RelCollations.of(ascending), facts).Should().BeTrue();

                Resolves(Collation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.LAST), facts).Should().BeTrue();
                Resolves(Collation(1, RelFieldCollation.Direction.DESCENDING, RelFieldCollation.NullDirection.FIRST), facts).Should().BeTrue();
            }

            /// <remarks>
            /// <para>
            /// Before the last key Calcite ties every null, and the service orders an absent path below a JSON null: the
            /// later key would order rows the service has already ordered. Measured on the emulator, the witness
            /// <c>ORDER BY t NULLS FIRST, id FETCH 2</c> over a <c>t</c> null in one document and absent in the next.
            /// </para>
            /// <para>
            /// Null one way only, it is one tie class at the service too.
            /// </para>
            /// </remarks>
            [Fact]
            public void ALeadingKeyNullTwoWaysIsRefused()
            {
                var both = new java.util.ArrayList();
                both.add(new RelFieldCollation(1, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.FIRST));
                both.add(new RelFieldCollation(2, RelFieldCollation.Direction.ASCENDING, RelFieldCollation.NullDirection.FIRST));

                Resolves(RelCollations.of(both), Declared("""{ "type": "object", "required": ["inventory"], "properties": { "name": { "enum": ["a", "b", null] }, "inventory": { "type": "object", "required": ["quantity"], "properties": { "quantity": { "type": "integer" } } } } }"""))
                    .Should().BeFalse("absent in one document and null in the next");
                Resolves(RelCollations.of(both), Declared("""{ "type": "object", "required": ["inventory"], "properties": { "name": { "enum": ["a", "b"] }, "inventory": { "type": "object", "required": ["quantity"], "properties": { "quantity": { "type": "integer" } } } } }"""))
                    .Should().BeTrue("absent or a value");
            }

            // ── Keys that order nothing ───────────────────────────────────────────────

            /// <remarks>
            /// A key the facts make one value in every row orders nothing, and is not handed to the service — which also
            /// keeps it out of the composite index question: <c>WHERE category = 'bikes' ORDER BY category, _ts</c> is a
            /// sort on <c>_ts</c>.
            /// </remarks>
            [Fact]
            public void AKeyOneValueInEveryRowIsNotWritten()
            {
                CosmosSort.TryResolveSortKeys(
                    Collation((1, RelFieldCollation.Direction.ASCENDING), (2, RelFieldCollation.Direction.DESCENDING)),
                    Fields,
                    Readings,
                    Array.Empty<CosmosOrdering>(),
                    RowType(),
                    "c",
                    Declared("""{ "type": "object", "required": ["name", "inventory"], "properties": { "name": { "const": "a" }, "inventory": { "type": "object", "required": ["quantity"], "properties": { "quantity": { "type": "integer" } } } } }"""),
                    out var keys,
                    out _,
                    out var written).Should().BeTrue();

                keys.Should().ContainSingle().Which.Path.Should().Be("/inventory/quantity");
                written.Should().Equal(1);
            }

            /// <remarks>
            /// A second key on a path an earlier key orders exactly ties wherever the first does, and Azure refuses even
            /// <c>ORDER BY c.a, c.a</c> without a composite index; so it is not written either.
            /// </remarks>
            [Fact]
            public void AKeyRepeatingAnExactOneIsNotWritten()
            {
                var fields = new List<CosmosPath?> { CosmosPath.Root("c").Property("n"), CosmosPath.Root("c").Property("n") };
                var rowType = _types.builder()
                    .add("n", _types.createSqlType(SqlTypeName.INTEGER))
                    .add("m", _types.createSqlType(SqlTypeName.INTEGER))
                    .build();

                CosmosSort.TryResolveSortKeys(
                    Collation((0, RelFieldCollation.Direction.ASCENDING), (1, RelFieldCollation.Direction.DESCENDING)),
                    fields,
                    new[] { CosmosReading.Typed, CosmosReading.Typed },
                    Array.Empty<CosmosOrdering>(),
                    rowType,
                    "c",
                    Declared("""{ "type": "object", "required": ["n"], "properties": { "n": { "type": "integer" } } }"""),
                    out var keys,
                    out _,
                    out _).Should().BeTrue();

                keys.Should().ContainSingle().Which.Descending.Should().BeFalse();
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
                CosmosPath.Root("c").Property("_ts"),            // 1
                CosmosPath.Root("t0").Property("key"),           // 2 — element-relative
            };

            org.apache.calcite.rel.type.RelDataType UnnestRowType() => _types.builder()
                .add("DOC", _types.createTypeWithNullability(_types.createSqlType(SqlTypeName.ANY), true))
                .add("_ts", _types.createSqlType(SqlTypeName.BIGINT))
                .add("element", _types.createSqlType(SqlTypeName.VARCHAR))
                .build();

            bool ResolvesOverUnnest(RelCollation collation) =>
                CosmosSort.TryResolveSortKeys(collation, UnnestFields, new[] { CosmosReading.Json, CosmosReading.Typed, CosmosReading.Typed }, Array.Empty<CosmosOrdering>(), UnnestRowType(), "c", Declared(), out _, out _, out _);

            /// <remarks>
            /// Refused however few keys there are: the service rejects ordering by an element alias
            /// outright, so there is no arity at which it becomes legal.
            /// </remarks>
            [Fact]
            public void SingleKeyOnAnUnnestAliasIsRefused()
            {
                ResolvesOverUnnest(Collation((1, RelFieldCollation.Direction.ASCENDING))).Should().BeTrue("the control");
                ResolvesOverUnnest(Collation((2, RelFieldCollation.Direction.ASCENDING))).Should().BeFalse();
            }

            /// <remarks>
            /// Its index-addressability cannot be established, so the sort must not be pushed down.
            /// </remarks>
            [Fact]
            public void MultiKeyInvolvingAnUnnestAliasIsRefused()
            {
                ResolvesOverUnnest(Collation((1, RelFieldCollation.Direction.ASCENDING), (2, RelFieldCollation.Direction.ASCENDING))).Should().BeFalse();
            }

        }

    }

}
