using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using Apache.Calcite.Extensions.Adapter.Enumerable;

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


namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel.Convert
{

    /// <summary>
    /// Whether a distance-ordered query still pushes its sort once the plan arrives in the shape a
    /// connection presents rather than the shape a host builds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the failure that stranded <c>ORDER BY RANK</c>. <c>Prepare</c> plans
    /// <c>RelRoot.rel</c> and applies the root's field mapping afterwards by wrapping the finished
    /// plan in a calc, so a rule that needs to see the projection which discards a column never does
    /// — recorded in <c>TODO.md</c> and in
    /// <a href="https://github.com/ikvmnet/calcite-cosmos/issues/46">#46</a>. A host that plans
    /// <c>RelRoot.project()</c> sees a different tree and the rule fires.
    /// </para>
    /// <para>
    /// The distance sort does not care, and these say so. Its rule reads the projection <em>beneath</em>
    /// the sort rather than one above it, and a distance may be projected where a score may not — so the
    /// statement carries the column and the calc above merely drops it.
    /// </para>
    /// <para>
    /// <b>What it does depend on is the null placement</b>, and that is worth knowing before writing one.
    /// A distance is computed and therefore nullable, so a sort over it is refused unless the plan's
    /// default null collation is <c>LOW</c> — where <c>id</c>, being non-nullable, needs nothing. The
    /// harness below sets it for the same reason the README tells a caller to.
    /// </para>
    /// </remarks>
    public class CosmosSortRuleTests
    {

        const string Here = """{"type":"Point","coordinates":[-122.33,47.61]}""";

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        /// <summary>
        /// Plans a statement, taking the root either as a connection does or as a host does.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="asConnection"><c>true</c> to plan <c>RelRoot.rel</c>, as <c>Prepare</c> does.</param>
        /// <returns>The best plan.</returns>
        static RelNode Plan(string sql, bool asConnection) => Plan(sql, asConnection, NullCollation.LOW, null);

        /// <inheritdoc cref="Plan(string, bool)" />
        /// <param name="collation">The plan's default null placement.</param>
        /// <param name="schema">What the container declares, or <c>null</c> where it declares nothing.</param>
        static RelNode Plan(string sql, bool asConnection, NullCollation collation, string? schema)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            var metadata = schema is null
                ? Products
                : Products.WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(schema)));

            var table = new CosmosTable(metadata);
            rootSchema.add("products", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            // Cosmos places nulls where LOW says, and a sort whose placement it cannot honour is
            // declined -- the same setting the README tells a caller to use, and a distance is
            // nullable like any other computed value.
            properties.setProperty("defaultNullCollation", collation == NullCollation.LOW ? "LOW" : "HIGH");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(),
                Apache.Calcite.Geography.Sql.GeographyOperatorTable.Instance());

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT.withDefaultNullCollation(collation));

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            var root = converter.convertQuery(validator.validate(parsed), false, true);
            var logical = asConnection ? root.rel : root.project();

            foreach (var rule in CosmosRules.GetRules(table.Convention))
                planner.addRule(rule);

            // A projection the statement cannot carry is left above the sort and evaluated in process,
            // so the plan needs the rules that implement one in the CLR convention as well.
            foreach (var rule in ClrEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        static bool ContainsSort(RelNode node)
        {
            if (node is CosmosSort)
                return true;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (ContainsSort((RelNode)inputs.get(i)))
                    return true;

            return false;
        }

        const string Ordered = """
            SELECT c."id" FROM products AS c
            ORDER BY CLR_ST_GEOG_DISTANCE(
                CLR_ST_GEOG_GEOMFROMGEOJSON(CAST(c."DOC" AS VARCHAR)),
                CLR_ST_GEOG_GEOMFROMGEOJSON('{"type":"Point","coordinates":[-122.33,47.61]}'))
            """;

        /// <summary>
        /// The sort pushes when the plan is taken the way a host takes it.
        /// </summary>
        [Fact]
        public void ADistanceSortPushesForAHost()
        {
            ContainsSort(Plan(Ordered, asConnection: false)).Should().BeTrue();
        }

        /// <summary>
        /// <summary>
        /// A container declaring the shape carries the sort under Calcite's own placement, which is
        /// the one an ORM writes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Everything above this rests on <c>LOW</c>, and a generated query cannot ask for it.</b>
        /// Cosmos orders undefined first ascending and offers no control; Calcite's default is last.
        /// A caller who sets <c>defaultNullCollation=LOW</c> asks for the order the service already
        /// produces and every sort here pushes — which is what the README says to do. A caller going
        /// through EF Core or OData writes a bare <c>ORDER BY</c> and gets <c>HIGH</c>, and the sort
        /// was declined for disagreeing with a placement nobody chose.
        /// </para>
        /// <para>
        /// A key that can be neither null nor undefined has no placement to disagree about, and
        /// <c>Point</c> schema with bounded ordinates is the declaration that gives one. Nothing is added to the statement
        /// to make it true — no guard, no filter, no rewrite; the rows are the rows.
        /// </para>
        /// </remarks>
        [Fact]
        public void ADeclaredGeographyCarriesTheSortUnderTheDefaultPlacement()
        {
            var best = Plan(OrderedByPath, asConnection: true, NullCollation.HIGH, Geography);

            ContainsSort(best).Should().BeTrue("a distance over a declared shape is neither null nor undefined");
        }

        /// <summary>
        /// And an object type alone does not, which is the distinction the whole claim exists for.
        /// </summary>
        /// <remarks>
        /// An object that is not a shape is a perfectly good object, and measured against an account
        /// a distance over one answers <em>undefined</em> — as does a structurally perfect point whose
        /// coordinates are out of range. So a claim about the type closes the null case and leaves the
        /// undefined one, which is a key that still sorts at the wrong end. See
        /// <c>CosmosGeographyValidityMeasurementTests</c>.
        /// </remarks>
        [Fact]
        public void AnObjectTypeAloneDoesNot()
        {
            var best = Plan(OrderedByPath, asConnection: true, NullCollation.HIGH, ObjectOnly);

            ContainsSort(best).Should().BeFalse("the type says nothing about whether the service can measure it");
        }

        /// <remarks>
        /// The control: without a declaration the placement is all there is, and under <c>HIGH</c> it
        /// disagrees. This is the behaviour the two above are a departure from.
        /// </remarks>
        [Fact]
        public void WithoutADeclarationTheDefaultPlacementStillDeclines()
        {
            var best = Plan(OrderedByPath, asConnection: true, NullCollation.HIGH, null);

            ContainsSort(best).Should().BeFalse();
        }

        /// <summary>
        /// A container declaring the shape declares it for <c>LOW</c> too, which changes nothing.
        /// </summary>
        /// <remarks>
        /// Worth pinning because the declaration must not start rewriting a statement that already
        /// worked: under <c>LOW</c> the sort pushed before this claim existed and pushes the same way
        /// after it.
        /// </remarks>
        [Fact]
        public void ADeclarationChangesNothingUnderLow()
        {
            var best = Plan(OrderedByPath, asConnection: true, NullCollation.LOW, Geography);

            ContainsSort(best).Should().BeTrue();
        }

        /// <summary>
        /// The same ordering, over a path rather than the whole document, since a declaration
        /// is about a path. <c>Ordered</c> casts <c>DOC</c> itself, which resolves to the root.
        /// </summary>
        const string OrderedByPath = """
            SELECT c."id" FROM products AS c
            ORDER BY CLR_ST_GEOG_DISTANCE(
                CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location')),
                CLR_ST_GEOG_GEOMFROMGEOJSON('{"type":"Point","coordinates":[-122.33,47.61]}'))
            """;

        /// <summary>
        /// What a container declares to say a path holds a shape.
        /// </summary>
        const string Geography = """
        { "type": "object",
          "properties": { "location": { "type": "object", "required": ["type", "coordinates"],
                        "properties": {
                          "type": { "const": "Point" },
                          "coordinates": { "type": "array", "minItems": 2, "maxItems": 3,
                            "prefixItems": [ { "type": "number", "minimum": -180, "maximum": 180 },
                                             { "type": "number", "minimum": -90, "maximum": 90 } ] } } } } }
        """;

        /// <summary>
        /// The same without the format, which declares the type and nothing the service can use.
        /// </summary>
        const string ObjectOnly = """
        { "type": "object",
          "required": ["location"],
          "properties": { "location": { "type": "object" } } }
        """;

        /// And when it is taken the way a connection takes it, which is where the rank clause is lost.
        /// </summary>
        /// <remarks>
        /// The two differ by a projection above the sort rather than below it. A distance may be
        /// projected, so the statement carries the column and the calc merely drops it; a full text
        /// score may not, which is why that case cannot be recovered the same way.
        /// </remarks>
        [Fact]
        public void ADistanceSortPushesForAConnection()
        {
            ContainsSort(Plan(Ordered, asConnection: true)).Should().BeTrue();
        }

    }

}
