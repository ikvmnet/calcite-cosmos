using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;

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
    /// What a declared schema changes about a plan, end to end: from the model's operand to the
    /// statement the service is sent.
    /// </summary>
    /// <remarks>
    /// The point of the whole exercise is that nothing downstream of one rewrite had to change. A
    /// comparison against a <c>UUID</c> has no Cosmos form and reads the container whole; once the
    /// declared stored form lowers it to a string equality, the statement carries it, the partition
    /// key is recovered from it, and the plan stops carrying an in-process filter.
    /// </remarks>
    [TestClass]
    public class CosmosDeclaredFactPlanningTests
    {

        const string Canonical = "123e4567-e89b-12d3-a456-426614174000";

        const string Schema = """
        {
          "oneOf": [
            { "properties": { "kind": { "const": "A" } } },
            { "properties": { "kind": { "const": "B" },
                              "ref": { "type": "string",
                                       "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }
          ]
        }
        """;

        static CosmosContainerMetadata Declared(bool declares) =>
            declares
                ? new CosmosContainerMetadata("items", new[] { "/ref" })
                    .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(Schema)))
                : new CosmosContainerMetadata("items", new[] { "/ref" });

        const string Ref = """CAST(JSON_VALUE(c."DOC", '$.ref') AS UUID)""";
        const string Kind = """JSON_VALUE(c."DOC", '$.kind')""";

        static RelNode PlanToCosmos(string sql, CosmosContainerMetadata container, out CosmosTable table)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            table = new CosmosTable(container);

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

            // To the CLR convention rather than to the Cosmos one, because the whole comparison here
            // is whether a conjunct reaches the service — and the case where one does not has to be
            // plannable rather than a failure.
            foreach (var rule in Apache.Calcite.Extensions.Adapter.Enumerable.ClrEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(Apache.Calcite.Extensions.Adapter.Enumerable.ClrEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// The deepest node still in the Cosmos convention, which is the statement's root.
        /// </summary>
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

        static CosmosQuery Query(RelNode rel, CosmosContainerMetadata container)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), container);
            implementor.Visit(rel);
            return implementor.Build();
        }

        static string PlanText(RelNode rel) => RelOptUtil.toString(rel).Trim().Replace("\r\n", "\n");

        [TestMethod]
        public void WithNothingDeclaredTheComparisonStaysInProcess()
        {
            var container = Declared(false);
            var best = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE {Kind} = 'B' AND {Ref} = UUID'{Canonical}'""", container, out _);

            var query = Query(FindCosmos(best), container);

            // The baseline is not that nothing pushes. The split rule already weakens the comparison
            // to the one thing it implies without knowing the stored form — that the path is there —
            // and rechecks the comparison itself above.
            query.Sql.Should().Contain("IS_DEFINED(c.ref)");
            query.Sql.Should().NotContain("c.ref = @", "nothing says what the stored form is, so the comparison itself has no Cosmos form");
            query.PartitionKeyValues.Should().BeNull("and with no value pinned there is no partition to route to");

            PlanText(best).Should().Contain("ClrEnumerableFilter", "the comparison is still Calcite's to make: " + PlanText(best));
        }

        [TestMethod]
        public void ADeclaredStoredFormPutsTheComparisonInTheStatement()
        {
            var container = Declared(true);
            var best = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE {Kind} = 'B' AND {Ref} = UUID'{Canonical}'""", container, out _);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("c.ref = @", "the cast lowered to the equality the service can evaluate");
            query.Parameters.Should().Contain(p => (p.Value as string) == Canonical, "written in the stored spelling");

            PlanText(best).Should().NotContain("ClrEnumerableFilter", "and nothing is left for the runtime: " + PlanText(best));
        }

        [TestMethod]
        public void TheLoweredComparisonRoutesToItsPartition()
        {
            var container = Declared(true);
            var best = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE {Kind} = 'B' AND {Ref} = UUID'{Canonical}'""", container, out _);

            Query(FindCosmos(best), container).PartitionKeyValues.Should().ContainSingle().Which.Should().Be(Canonical,
                "which is the largest single cost lever there is, and it needed no change of its own");
        }

        [TestMethod]
        public void WithoutTheDiscriminatorNothingIsProvenAndNothingLowers()
        {
            var container = Declared(true);
            var best = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE {Ref} = UUID'{Canonical}'""", container, out _);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().NotContain("c.ref = @",
                "a document of the other kind may not carry the path at all, so the fact is not usable");
            query.PartitionKeyValues.Should().BeNull();
        }

        /// <summary>
        /// The half that the split rule has to get right: a predicate carrying something with no
        /// Cosmos form at all still pushes the part the declaration licensed.
        /// </summary>
        [TestMethod]
        public void APredicateThatOnlyPartlyTranslatesStillPushesTheLoweredHalf()
        {
            var container = Declared(true);
            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE {Kind} = 'B' AND {Ref} = UUID'{Canonical}' AND CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER) = 3""",
                container, out _);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("c.ref = @", "the lowered conjunct is pushable even though its neighbour is not");
            PlanText(best).Should().Contain("ClrEnumerableFilter", "and the conjunct with no form is still rechecked above: " + PlanText(best));
        }

    }

}
