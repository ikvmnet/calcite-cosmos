using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Extensions.Adapter.Cursor;

using FluentAssertions;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// A distance to a point bound as a parameter pushes as the distance to a literal point does (#187).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CAST(? AS GEOMETRY)</c> is how a parameter is given the type, and it is what Entity Framework
    /// writes for every spatial argument. The validator types <c>?0</c> from it and keeps the cast, so
    /// the plan carries <c>CAST(?0):GEOMETRY</c> over a parameter already typed <c>GEOMETRY</c> — a cast
    /// that converts nothing, where the translator took a cast over a literal only. So the comparison
    /// and the ordering were left above the statement and every document crossed the wire to be
    /// measured.
    /// </para>
    /// <para>
    /// The harness parses with <c>LENIENT</c> conformance, which is what admits the <c>GEOMETRY</c> type
    /// at all — the parser refuses it otherwise, and a connection a spatial caller uses has it set.
    /// </para>
    /// </remarks>
    public class CosmosGeographyParameterPlanningTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        const string Location = """CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location'))""";

        /// <summary>
        /// A view over the container exposing the stored shape, which is how the issue reached it.
        /// </summary>
        const string View = $"""(SELECT c."id" AS "Id", {Location} AS "Location" FROM products AS c) AS s""";

        /// <summary>
        /// What a container declares to say a path always holds a point.
        /// </summary>
        const string Point = """
        { "type": "object",
          "properties": { "location": { "type": "object", "required": ["type", "coordinates"],
                        "properties": {
                          "type": { "const": "Point" },
                          "coordinates": { "type": "array", "minItems": 2, "maxItems": 3,
                            "prefixItems": [ { "type": "number", "minimum": -180, "maximum": 180 },
                                             { "type": "number", "minimum": -90, "maximum": 90 } ] } } } } }
        """;

        static RelNode Plan(string sql, NullCollation collation, string? schema)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var metadata = schema is null
                ? Products
                : Products.WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(schema)));

            var table = new CosmosTable(metadata);
            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");
            properties.setProperty("defaultNullCollation", collation == NullCollation.LOW ? "LOW" : "HIGH");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));

            // LENIENT is what admits GEOMETRY as a type name; the parser refuses it otherwise.
            var conformance = SqlConformanceEnum.LENIENT;
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED).withConformance(conformance)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(),
                Apache.Calcite.Geography.Sql.GeographyOperatorTable.Instance());

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory,
                SqlValidator.Config.DEFAULT.withDefaultNullCollation(collation).withConformance(conformance));

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var rule in CosmosRules.GetRules(table.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            planner.setRoot(planner.changeTraits(logical, logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify()));
            return planner.findBestExp();
        }

        static RelNode Plan(string sql) => Plan(sql, NullCollation.LOW, null);

        static RelNode? FindCosmos(RelNode node)
        {
            if (node is CosmosRel)
                return node;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (FindCosmos((RelNode)inputs.get(i)) is RelNode found)
                    return found;

            return null;
        }

        static CosmosQuery Query(RelNode best)
        {
            var implementor = new CosmosImplementor(best.getCluster().getRexBuilder(), Products);
            implementor.Visit(FindCosmos(best)!);
            return implementor.Build();
        }

        static string Text(RelNode best) => RelOptUtil.toString(best).Trim().Replace("\r\n", "\n");

        /// <summary>
        /// A comparison of the distance with a number is the statement's filter, the point bound
        /// rather than written.
        /// </summary>
        [Fact]
        public void ADistanceToABoundPointIsFiltered()
        {
            var best = Plan($"""SELECT c."id" FROM products AS c WHERE CLR_ST_GEOG_DISTANCE({Location}, CAST(? AS GEOMETRY)) < 50000.0""");
            var query = Query(best);

            query.Sql.Should().MatchRegex(@"ST_DISTANCE\(c\.location, @p\d+\) < @p\d+", "the distance is the filter: " + query.Sql);
            query.Parameters.Should().ContainSingle(p => p.Value is CosmosDynamicValue, "and the point is the execution's to supply");
            Text(best).Should().NotContain("ClrCursorFilter", "so nothing is measured in process: " + Text(best));
        }

        /// <summary>
        /// And through a view's column, which is the shape the issue reported.
        /// </summary>
        [Fact]
        public void ADistanceFromAViewsColumnToABoundPointIsFiltered()
        {
            var best = Plan($"""SELECT s."Id" FROM {View} WHERE CLR_ST_GEOG_DISTANCE(s."Location", CAST(? AS GEOMETRY)) < 50000.0""");

            Query(best).Sql.Should().Contain("ST_DISTANCE(c.location, @p");
            Text(best).Should().NotContain("ClrCursorFilter", "nothing is measured in process: " + Text(best));
        }

        /// <summary>
        /// An ordering by the distance is the statement's sort, read directly or through the view.
        /// </summary>
        [Fact]
        public void AnOrderingByTheDistanceToABoundPointIsSorted()
        {
            foreach (var sql in new[]
            {
                $"""SELECT c."id" FROM products AS c ORDER BY CLR_ST_GEOG_DISTANCE({Location}, CAST(? AS GEOMETRY)) FETCH FIRST 5 ROWS ONLY""",
                $"""SELECT s."Id" FROM {View} ORDER BY CLR_ST_GEOG_DISTANCE(s."Location", CAST(? AS GEOMETRY)) FETCH FIRST 5 ROWS ONLY""",
            })
            {
                var best = Plan(sql);
                var query = Query(best);

                query.Sql.Should().MatchRegex(@"ORDER BY ST_DISTANCE\(c\.location, @p\d+\)", "the distance is the sort: " + query.Sql);
                Text(best).Should().NotContain("ClrCursorSort", "and nothing is sorted in process: " + Text(best));
            }
        }

        /// <summary>
        /// Under the default placement too, where the container declares the path a point — the same
        /// licence a literal point gets.
        /// </summary>
        /// <remarks>
        /// A parameter can be null, or a shape the service will not measure, where a literal written
        /// into the statement cannot. Either makes the distance null or undefined in every row at once,
        /// so no key is placed against another and the placements have nothing to disagree about. What
        /// the declaration has to close is the stored operand, as it does beside a literal.
        /// </remarks>
        [Fact]
        public void AnOrderingByTheDistanceToABoundPointIsSortedUnderTheDefaultPlacementWhereThePathIsDeclared()
        {
            var sql = $"""SELECT c."id" FROM products AS c ORDER BY CLR_ST_GEOG_DISTANCE({Location}, CAST(? AS GEOMETRY)) FETCH FIRST 5 ROWS ONLY""";

            Text(Plan(sql, NullCollation.HIGH, Point)).Should().NotContain("ClrCursorSort");
            Text(Plan(sql, NullCollation.HIGH, null)).Should().Contain("ClrCursorSort",
                "and without the declaration a stored operand may be null, so the placement still decides");
        }

        /// <summary>
        /// A cast into a geography from anything but one is a parse, and stays in process.
        /// </summary>
        [Fact]
        public void ACastIntoAGeographyFromTextIsNotTheParameter()
        {
            var best = Plan($"""SELECT c."id" FROM products AS c WHERE CLR_ST_GEOG_DISTANCE({Location}, CAST(CAST(? AS VARCHAR) AS GEOMETRY)) < 50000.0""");

            Text(best).Should().Contain("ClrCursorFilter", "the parse is the engine's: " + Text(best));
        }

    }

}
