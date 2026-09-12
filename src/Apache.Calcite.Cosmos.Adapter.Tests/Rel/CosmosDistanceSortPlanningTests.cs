using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using Apache.Calcite.Extensions.Adapter.Enumerable;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

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

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
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
    [TestClass]
    public class CosmosDistanceSortPlanningTests
    {

        const string Here = """{"type":"Point","coordinates":[-122.33,47.61]}""";

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        /// <summary>
        /// Plans a statement, taking the root either as a connection does or as a host does.
        /// </summary>
        /// <param name="sql">The statement.</param>
        /// <param name="asConnection"><c>true</c> to plan <c>RelRoot.rel</c>, as <c>Prepare</c> does.</param>
        /// <returns>The best plan.</returns>
        static RelNode Plan(string sql, bool asConnection)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            var table = new CosmosTable(Products);
            rootSchema.add("products", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            // Cosmos places nulls where LOW says, and a sort whose placement it cannot honour is
            // declined -- the same setting the README tells a caller to use, and a distance is
            // nullable like any other computed value.
            properties.setProperty("defaultNullCollation", "LOW");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(),
                Apache.Calcite.Geography.Sql.GeographyOperatorTable.Instance());

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT.withDefaultNullCollation(NullCollation.LOW));

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
            ORDER BY ST_GEOG_DISTANCE(
                ST_GEOG_GEOMFROMGEOJSON(CAST(c."DOC" AS VARCHAR)),
                ST_GEOG_GEOMFROMGEOJSON('{"type":"Point","coordinates":[-122.33,47.61]}'))
            """;

        /// <summary>
        /// The sort pushes when the plan is taken the way a host takes it.
        /// </summary>
        [TestMethod]
        public void ADistanceSortPushesForAHost()
        {
            ContainsSort(Plan(Ordered, asConnection: false)).Should().BeTrue();
        }

        /// <summary>
        /// And when it is taken the way a connection takes it, which is where the rank clause is lost.
        /// </summary>
        /// <remarks>
        /// The two differ by a projection above the sort rather than below it. A distance may be
        /// projected, so the statement carries the column and the calc merely drops it; a full text
        /// score may not, which is why that case cannot be recovered the same way.
        /// </remarks>
        [TestMethod]
        public void ADistanceSortPushesForAConnection()
        {
            ContainsSort(Plan(Ordered, asConnection: true)).Should().BeTrue();
        }

    }

}
