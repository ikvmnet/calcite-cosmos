using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using FluentAssertions;
using Xunit;

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

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    /// <summary>
    /// What a declared boolean does to a plan whose boolean column is a cast of the text accessor —
    /// the way a view gives a column the type. #181.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without a declaration the cast is a parse, and nothing about it reaches the service.</b>
    /// Calcite reads <c>"TRUE"</c> as true and the service reads it as a string, so the comparison was
    /// weakened to a definedness test and every document with the flag was read to test it in process.
    /// Where the schema says the path holds a boolean the cast is the stored value, and the cast, the
    /// comparisons against a boolean, <c>NOT</c> and the truth tests are each written over the path.
    /// </para>
    /// <para>
    /// Planned with a bare Volcano planner; <c>CosmosSelfJoinTests</c> asks the same questions of a
    /// connection and compares the rows.
    /// </para>
    /// </remarks>
    public class CosmosBooleanPlanningTests
    {

        const string Unconditional = """
        { "properties": { "flag": { "type": "boolean" }, "kind": { "type": "string" } } }
        """;

        const string Discriminated = """
        {
          "oneOf": [
            { "properties": { "kind": { "const": "A" } } },
            { "properties": { "kind": { "const": "B" }, "flag": { "type": "boolean" } } }
          ]
        }
        """;

        const string AsString = """
        { "properties": { "flag": { "type": "string" } } }
        """;

        const string Flag = """CAST(JSON_VALUE(c."DOC", '$.flag' RETURNING VARCHAR) AS BOOLEAN)""";

        const string Kind = """JSON_VALUE(c."DOC", '$.kind')""";

        static CosmosContainerMetadata Container(string? schema) =>
            schema is null
                ? new CosmosContainerMetadata("items", new[] { "/pk" })
                : new CosmosContainerMetadata("items", new[] { "/pk" })
                    .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(schema)));

        static RelNode PlanToCosmos(string sql, CosmosContainerMetadata container)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(container);

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("items", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(), Adapter.Sql.CosmosOperators.Instance);

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var rule in CosmosRules.GetRules(table.Convention))
                planner.addRule(rule);

            // To the CLR convention, so that a test the service cannot make is a plannable query
            // rather than a failure -- the case this class exists to tell apart.
            foreach (var rule in Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        static RelNode FindCosmos(RelNode node)
        {
            if (node is CosmosRel)
                return node;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (FindCosmos((RelNode)inputs.get(i)) is RelNode found)
                    return found;

            return null!;
        }

        static (CosmosQuery Query, string Plan) Plan(string predicate, string? schema, string view = Flag)
        {
            var container = Container(schema);
            var best = PlanToCosmos($"""SELECT v."id" FROM (SELECT c."id", {view} AS f, {Kind} AS k FROM items AS c) AS v WHERE {predicate}""", container);

            var implementor = new CosmosImplementor(best.getCluster().getRexBuilder(), container);
            implementor.Visit(FindCosmos(best));

            return (implementor.Build(), RelOptUtil.toString(best).Trim().Replace("\r\n", "\n"));
        }

        /// <summary>
        /// Every test the issue names, written over the path, and nothing left in process.
        /// </summary>
        /// <remarks>
        /// The positive tests compare the path with a bound boolean — <c>c.flag = true</c> — and the
        /// negative ones keep the absent path by name, since a comparison over one is undefined rather
        /// than false and <c>NOT</c> above it would drop a row SQL keeps. The bare column and
        /// <c>NOT</c> are the path itself, the service's logic over what is not a boolean being SQL's
        /// over unknown; <c>CosmosBooleanLogicMeasurementTests</c> measures both.
        /// </remarks>
        [Theory]
        [InlineData("v.f", "WHERE c.flag", null)]
        [InlineData("v.f IS TRUE", "(c.flag = @p0)", true)]
        [InlineData("v.f = TRUE", "(c.flag = @p0)", true)]
        [InlineData("v.f IS FALSE", "(c.flag = @p0)", false)]
        [InlineData("v.f = FALSE", "(c.flag = @p0)", false)]
        [InlineData("NOT v.f", "(NOT c.flag)", null)]
        [InlineData("v.f IS NOT TRUE", "(NOT IS_DEFINED(c.flag) OR (NOT (c.flag = @p0)))", true)]
        [InlineData("v.f IS NOT FALSE", "(NOT IS_DEFINED(c.flag) OR (NOT (c.flag = @p0)))", false)]
        [InlineData("v.f IS NULL", "(NOT IS_DEFINED(c.flag) OR IS_NULL(c.flag))", null)]
        public void ADeclaredBooleanIsTestedAtTheService(string predicate, string rendered, bool? bound)
        {
            var (query, plan) = Plan(predicate, Unconditional);

            query.Sql.Should().Contain(rendered, "the test is written over the stored boolean: " + query.Sql);
            plan.Should().NotContain("ClrCursorFilter", "and nothing is left to test in process: " + plan);

            if (bound is bool value)
                query.Parameters.Should().ContainSingle().Which.Value.Should().Be(value);
        }

        /// <summary>
        /// Under a <c>NOT</c> a positive truth test takes the absent path into account, where alone it
        /// need not.
        /// </summary>
        /// <remarks>
        /// <c>NOT (f IS TRUE AND k = 'x')</c> keeps a document with no flag — <c>IS TRUE</c> is false
        /// there, so the conjunction is — and the service's <c>NOT</c> over a bare comparison would be
        /// undefined there and keep nothing. Written inside a conjunction so that nothing folds the
        /// negation into the test before it reaches the translator.
        /// </remarks>
        [Fact]
        public void UnderANegationATruthTestStaysTwoValued()
        {
            var (query, plan) = Plan("NOT (v.f IS TRUE AND v.k = 'x')", Unconditional);

            query.Sql.Should().Contain("(IS_DEFINED(c.flag) AND (c.flag = @p", query.Sql);
            plan.Should().NotContain("ClrCursorFilter", plan);
        }

        /// <summary>
        /// Without the declaration the cast is a parse of the text, which is not the service's value, and
        /// the test stays in process.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData(AsString)]
        public void WithoutABooleanDeclarationTheTestStaysInProcess(string? schema)
        {
            foreach (var predicate in new[] { "v.f", "v.f IS TRUE", "v.f = TRUE", "NOT v.f", "v.f IS NOT TRUE" })
            {
                var (query, plan) = Plan(predicate, schema);

                query.Sql.Should().NotContain("c.flag = @", $"nothing says the path holds a boolean, for {predicate}: " + query.Sql);
                query.Sql.Should().NotContain("NOT c.flag", $"for {predicate}: " + query.Sql);
                plan.Should().Contain("ClrCursorFilter", $"so Calcite makes the test, for {predicate}: " + plan);
            }
        }

        /// <summary>
        /// A boolean declared only under a discriminator pushes beside the discriminator, and not without
        /// it.
        /// </summary>
        [Fact]
        public void AGuardedBooleanPushesBesideItsGuard()
        {
            var (guarded, guardedPlan) = Plan("v.k = 'B' AND v.f IS TRUE", Discriminated);

            guarded.Sql.Should().Contain("(c.flag = @", "the guard proves the type: " + guarded.Sql);
            guardedPlan.Should().NotContain("ClrCursorFilter", guardedPlan);

            var (bare, barePlan) = Plan("v.f IS TRUE", Discriminated);

            bare.Sql.Should().NotContain("c.flag = @", "with no guard nothing proves it: " + bare.Sql);
            barePlan.Should().Contain("ClrCursorFilter", barePlan);

            var (other, otherPlan) = Plan("v.k = 'A' AND v.f IS TRUE", Discriminated);

            other.Sql.Should().NotContain("c.flag = @", "and a guard proving another branch proves nothing about it: " + other.Sql);
            otherPlan.Should().Contain("ClrCursorFilter", otherPlan);
        }

        /// <summary>
        /// A behaviour clause substitutes a value the path does not hold, so a cast over one is not the
        /// stored value however the path is declared.
        /// </summary>
        [Fact]
        public void ABehaviourClauseIsNotTheStoredValue()
        {
            var (query, plan) = Plan("v.f IS TRUE", Unconditional,
                view: """CAST(JSON_VALUE(c."DOC", '$.flag' DEFAULT 'true' ON EMPTY) AS BOOLEAN)""");

            query.Sql.Should().NotContain("c.flag = @", query.Sql);
            plan.Should().Contain("ClrCursorFilter", plan);
        }

        /// <summary>
        /// Projected, the column is the stored boolean too, read back as one.
        /// </summary>
        /// <remarks>
        /// Guarded as the accessor is wherever it sits inside an expression, so a document holding an
        /// object at the path reads null, which is what the accessor — and so the cast — answers there.
        /// </remarks>
        [Fact]
        public void AProjectedBooleanIsTheStoredBoolean()
        {
            var container = Container(Unconditional);
            var best = PlanToCosmos($"""SELECT {Flag} AS f FROM items AS c""", container);

            var implementor = new CosmosImplementor(best.getCluster().getRexBuilder(), container);
            implementor.Visit(FindCosmos(best));

            implementor.Build().Sql.Should().Contain("(IS_PRIMITIVE(c.flag) ? c.flag : null)");
            RelOptUtil.toString(best).Should().NotContain("ClrCursorProject", "the cast is not left in process")
                .And.NotContain("ClrCursorCalc");
        }

    }

}
