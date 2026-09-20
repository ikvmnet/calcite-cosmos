using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using org.apache.calcite.tools;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// The cluster, table and expression builder the Cosmos node tests are driven against.
    /// </summary>
    /// <remarks>
    /// A catalog reader resolves a <c>CosmosTable</c> into a <see cref="RelOptTable"/> without opening
    /// the internal Calcite connection <c>RelBuilder.create</c> would, which needs the JDBC driver. Each
    /// node's tests derive from this rather than restating it, so the fixture is one thing to change.
    /// </remarks>
    public abstract class CosmosRelNodeFixture
    {

        /// <remarks>
        /// The composite index is declared over <c>/id</c> and <c>/_ts</c> because collations
        /// address fields by ordinal, so only promoted columns can be sorted on. An unpromoted
        /// document path would require an accessor call, which a collation cannot express.
        /// </remarks>
        protected static readonly CosmosContainerMetadata Products = new(
            "products",
            new[] { "/category" },
            new[]
            {
                new CosmosCompositeIndex(new[]
                {
                    new CosmosCompositeIndexPath("/id", false),
                    new CosmosCompositeIndexPath("/_ts", false),
                }),
            });

        protected RelOptCluster _cluster = null!;
        protected RelOptTable _table = null!;
        protected RexBuilder _rex = null!;
        protected CosmosConvention _convention = null!;

        protected CosmosRelNodeFixture()
        {
            // A catalog reader resolves a Table into a RelOptTable without opening the internal
            // Calcite connection that RelBuilder.create would, which needs the JDBC driver.
            var typeFactory = new org.apache.calcite.jdbc.JavaTypeFactoryImpl();
            var rootSchema = org.apache.calcite.jdbc.CalciteSchema.createRootSchema(false);
            rootSchema.add("products", new CosmosTable(Products));

            var reader = new org.apache.calcite.prepare.CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new org.apache.calcite.config.CalciteConnectionConfigImpl(new java.util.Properties()));

            _table = reader.getTable(java.util.Collections.singletonList("products"));

            var planner = new org.apache.calcite.plan.volcano.VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            _rex = new RexBuilder(typeFactory);
            _cluster = RelOptCluster.create(planner, _rex);
            _convention = CosmosConvention.Create(Products);
        }

        protected CosmosImplementor Implementor() => new(_rex, Products);

        protected RelTraitSet Traits() => _cluster.traitSetOf(_convention);

        protected CosmosTableScan Scan() => new(_cluster, Traits(), _table);

        protected RexNode Ref(int index) => _rex.makeInputRef(_table.getRowType().getFieldList().size() > index
            ? ((org.apache.calcite.rel.type.RelDataTypeField)_table.getRowType().getFieldList().get(index)).getType()
            : _cluster.getTypeFactory().createSqlType(SqlTypeName.ANY), index);

        /// <summary>
        /// <c>JSON_VALUE(&lt;doc&gt;, '$.a.b')</c>, which is how a document path is addressed now that the
        /// row model carries the document as text.
        /// </summary>
        /// <remarks>
        /// Built with an explicit return type rather than through the operator's inference: two
        /// operands is what the translator reads — the document and the path — and the full SQL form
        /// carries four more flags describing the ON EMPTY and ON ERROR behaviour, which nothing here
        /// is about.
        /// </remarks>
        protected static java.util.List Nodes(params RexNode[] nodes)
        {
            var list = new java.util.ArrayList();
            foreach (var node in nodes)
                list.add(node);
            return list;
        }

        protected RexNode Doc(string path, RexNode? document = null) => _rex.makeCall(
            _cluster.getTypeFactory().createSqlType(SqlTypeName.VARCHAR),
            SqlStdOperatorTable.JSON_VALUE,
            Nodes(document ?? Ref(0), Str("$." + path)));

        /// <summary>
        /// The same, for an array: <c>JSON_QUERY</c> wrapped so that <c>UNNEST</c> will take it.
        /// </summary>
        protected RexNode DocArray(string path, RexNode? document = null) => _rex.makeCall(
            _cluster.getTypeFactory().createSqlType(SqlTypeName.ANY),
            Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.StringToArray,
            Nodes(_rex.makeCall(
                _cluster.getTypeFactory().createSqlType(SqlTypeName.VARCHAR),
                SqlStdOperatorTable.JSON_QUERY,
                Nodes(document ?? Ref(0), Str("$." + path)))));

        protected const string Here = """{"type":"Point","coordinates":[-122.33,47.61]}""";

        /// <summary>
        /// <c>CLR_ST_GEOG_DISTANCE(c.location, &lt;a literal point&gt;)</c>, the one computed expression the
        /// service will order by.
        /// </summary>
        protected RexNode Distance() => _rex.makeCall(
            Apache.Calcite.Geography.Sql.GeographyOperatorTable.ClrStGeogDistance,
            Doc("location"),
            _rex.makeCall(
                Apache.Calcite.Geography.Sql.GeographyOperatorTable.ClrStGeogGeomFromGeoJson,
                _rex.makeLiteral(Here, _cluster.getTypeFactory().createSqlType(SqlTypeName.VARCHAR, Here.Length))));

        protected RexNode Str(string value) => _rex.makeLiteral(value, _cluster.getTypeFactory().createSqlType(SqlTypeName.VARCHAR, value.Length));

        protected RexNode Num(int value) => _rex.makeExactLiteral(new java.math.BigDecimal(value));

        protected CosmosProject ProjectOver(RelNode input, (string Name, RexNode Expression)[] projections)
        {
            var projects = new java.util.ArrayList();
            var builder = _cluster.getTypeFactory().builder();

            foreach (var (name, expression) in projections)
            {
                projects.add(expression);
                builder.add(name, expression.getType());
            }

            return new CosmosProject(_cluster, Traits(), input, projects, builder.build());
        }

        protected org.apache.calcite.rel.type.RelDataType UnnestRowType(RelNode input, string name)
        {
            var builder = _cluster.getTypeFactory().builder();
            builder.addAll(input.getRowType().getFieldList());
            builder.add(name, _cluster.getTypeFactory().createSqlType(SqlTypeName.ANY));
            return builder.build();
        }

        protected CosmosUnnest UnnestOver(RelNode input, RexNode array, string name = "t", org.apache.calcite.rel.core.CorrelationId? correlationId = null)
            => new(_cluster, Traits(), input, array, UnnestRowType(input, name), correlationId ?? _cluster.createCorrel());

        protected RexNode MapItem(string property) => Doc(property);

        /// <remarks>
        /// Null placement is stated explicitly as UNSPECIFIED. Calcite's defaults conflict with
        /// Cosmos's ordering; that interaction is covered in <c>CosmosSortTests.Resolution</c>.
        /// </remarks>
        protected static RelCollation Collation(params (int Index, RelFieldCollation.Direction Direction)[] keys)
        {
            var list = new java.util.ArrayList();
            foreach (var (index, direction) in keys)
                list.add(new RelFieldCollation(index, direction, RelFieldCollation.NullDirection.UNSPECIFIED));

            return RelCollations.of(list);
        }

        protected CosmosSort SortOver(RelNode input, RelCollation collation, RexNode? offset = null, RexNode? fetch = null) =>
            new(_cluster, Traits(), input, collation, offset, fetch);

        protected static string Sql(CosmosRel rel, CosmosImplementor implementor)
        {
            rel.Implement(implementor);
            return implementor.Build().Sql;
        }

    }

}
