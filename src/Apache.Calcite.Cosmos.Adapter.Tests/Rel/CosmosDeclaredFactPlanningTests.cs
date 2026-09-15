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
        /// The other spelling of a typed view column: <c>RETURNING VARCHAR</c> answers a string, and
        /// the cast over it converts. <c>RETURNING UUID</c> is not a third — <c>RETURNING</c> asserts
        /// the extracted type rather than converting to it, and JSON has no UUID, so it validates and
        /// then throws at run time whatever the document holds.
        /// </summary>
        [TestMethod]
        public void TheReturningVarcharSpellingLowersToo()
        {
            var container = Declared(true);
            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE {Kind} = 'B' AND CAST(JSON_VALUE(c."DOC", '$.ref' RETURNING VARCHAR) AS UUID) = UUID'{Canonical}'""",
                container, out _);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("c.ref = @", "the path underneath is the same path");
            query.PartitionKeyValues.Should().ContainSingle().Which.Should().Be(Canonical);
        }

        /// <summary>
        /// The chain the issue is written around, end to end, now that a residual no longer costs the
        /// read: a comparison with no Cosmos form lowers to a string equality, the equality pins the
        /// partition key, and the discriminator that licensed it is held back rather than pushed.
        /// </summary>
        /// <remarks>
        /// The guard is what used to end the chain one step early. A point read applies no predicate,
        /// so <c>TryExtractPointRead</c> refuses any conjunct that is not an <c>id</c> or partition-key
        /// equality — and the discriminator conjunct is exactly such a conjunct. What changed is that
        /// <c>CosmosPointReadSplitRule</c> partitions the conjunction instead of relaxing the standard.
        /// </remarks>
        [TestMethod]
        public void ALoweredComparisonUnderAGuardStillReachesAPointRead()
        {
            var container = Declared(true);
            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE {Kind} = 'B' AND JSON_VALUE(c."DOC", '$.id') = 'x' AND {Ref} = UUID'{Canonical}'""",
                container, out _);

            var query = Query(FindCosmos(best), container);

            query.PartitionKeyValues.Should().ContainSingle().Which.Should().Be(Canonical,
                "the lowered equality is what pins the key");

            query.PointReadId.Should().Be("x",
                "and the guard that licensed the lowering is held back rather than disqualifying the read");
        }

        /// <summary>
        /// A declared type closes the gap an ordering comparison over the accessor has: the refusal
        /// rests on the service ordering raw values across JSON types while Calcite orders their
        /// renderings, and where the container says the path holds a string there is no second type
        /// for them to disagree over.
        /// </summary>
        [TestMethod]
        public void ADeclaredTypeMakesAnOrderingComparisonExactRatherThanWeakened()
        {
            const string Typed = """
            { "properties": { "at": { "type": "string",
                                      "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$" } } }
            """;

            var container = new CosmosContainerMetadata("items", new[] { "/ref" })
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(Typed)));

            var best = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.at') >= '2024-01-01T00:00:00Z'""", container, out _);
            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("c.at >= @");
            query.Sql.Should().NotContain("IS_STRING", "the guard admitted the types the schema says are not there");

            PlanText(best).Should().NotContain("ClrEnumerableFilter",
                "and with nothing left to recheck the filter is wholly pushed: " + PlanText(best));
        }

        /// <summary>
        /// And without the declaration it is weakened exactly as it was.
        /// </summary>
        [TestMethod]
        public void WithoutADeclaredTypeAnOrderingComparisonIsStillWeakened()
        {
            var container = Declared(false);
            var best = PlanToCosmos($"""SELECT c."DOC" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.at') >= '2024-01-01T00:00:00Z'""", container, out _);
            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("NOT IS_STRING(c.at)");
            PlanText(best).Should().Contain("ClrEnumerableFilter", "the comparison is still Calcite's to make: " + PlanText(best));
        }

        /// <summary>
        /// A guard exists to admit what a fact would have excluded, so a fact deletes it.
        /// </summary>
        /// <remarks>
        /// The numeric bound is still a bound with the type known -- the cast converts, so the window
        /// stays and the recheck with it. What goes is the disjunct admitting non-numbers, which is
        /// every document the service would otherwise return for the recheck to throw away.
        /// </remarks>
        [TestMethod]
        public void ADeclaredTypeDeletesTheGuardThatAdmittedTheOtherTypes()
        {
            const string Numeric = """{ "properties": { "n": { "type": "integer" } } }""";

            var declared = new CosmosContainerMetadata("items", new[] { "/ref" })
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(Numeric)));

            const string Sql = """SELECT c."DOC" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER) = 3""";

            var with = Query(FindCosmos(PlanToCosmos(Sql, declared, out _)), declared);
            with.Sql.Should().Contain("c.n > @").And.Contain("c.n < @", "the bound is still a bound: the cast converts");
            with.Sql.Should().NotContain("IS_NUMBER", "and nothing at that path is not a number");

            var without = Query(FindCosmos(PlanToCosmos(Sql, Declared(false), out _)), Declared(false));
            without.Sql.Should().Contain("NOT IS_NUMBER(c.n)", "where nothing says so, the guard stays");
        }

        /// <summary>
        /// And a declared presence deletes the definedness test, which is there only to stop the type
        /// guard admitting every document that lacks the path.
        /// </summary>
        [TestMethod]
        public void ADeclaredPresenceDeletesTheDefinednessTest()
        {
            // required without a type: presence known, kind not, so the type guard stays and the
            // definedness test that exists for its sake does not.
            const string Present = """{ "required": ["n"] }""";

            var declared = new CosmosContainerMetadata("items", new[] { "/ref" })
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(Present)));

            const string Sql = """SELECT c."DOC" FROM items AS c WHERE CAST(JSON_VALUE(c."DOC", '$.n') AS INTEGER) = 3""";

            var with = Query(FindCosmos(PlanToCosmos(Sql, declared, out _)), declared);
            with.Sql.Should().Contain("NOT IS_NUMBER(c.n)", "the kind is still unknown");
            with.Sql.Should().NotContain("IS_DEFINED(c.n)", "but nothing lacks the path");

            var without = Query(FindCosmos(PlanToCosmos(Sql, Declared(false), out _)), Declared(false));
            without.Sql.Should().Contain("IS_DEFINED(c.n)");
        }

        /// <summary>
        /// And the whole of it pays on a container that declares nothing, because the service's own
        /// guarantees are facts too.
        /// </summary>
        [TestMethod]
        public void TheServiceGuaranteesPayWithNoSchemaAtAll()
        {
            var container = Declared(false);
            var best = PlanToCosmos("""SELECT c."DOC" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.id') >= 'x'""", container, out _);
            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("c.id >= @");
            query.Sql.Should().NotContain("IS_STRING", "id is a string, and the service says so");
            query.Sql.Should().NotContain("IS_DEFINED", "and is always there");

            PlanText(best).Should().NotContain("ClrEnumerableFilter",
                "so the comparison is exact and nothing is left above: " + PlanText(best));
        }

        /// <summary>
        /// A sort over a document path is refused for null placement rather than for anything about
        /// the ordering, and a container that says the path is always a scalar settles it.
        /// </summary>
        /// <remarks>
        /// Cosmos orders nulls first ascending where Calcite's default is last, so a nullable key is
        /// refused however well the order is otherwise understood. A query removing the nulls itself
        /// has always satisfied that; this is the container doing it instead, once and for every
        /// query rather than one predicate at a time.
        /// </remarks>
        [TestMethod]
        public void ADeclaredScalarThatIsAlwaysThereMakesAPathSortable()
        {
            const string Always = """
            { "type": "object", "required": ["at"],
              "properties": { "at": { "type": "string" } } }
            """;

            var declared = new CosmosContainerMetadata("items", new[] { "/ref" })
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(Always)));

            const string Sql = """SELECT JSON_VALUE(c."DOC", '$.at') AS "at" FROM items AS c ORDER BY 1""";

            var with = PlanToCosmos(Sql, declared, out _);
            Query(FindCosmos(with), declared).Sql.Should().Contain("ORDER BY c.at",
                "the key cannot be null, so the two sides have nothing to disagree about");

            var without = PlanToCosmos(Sql, Declared(false), out _);
            Query(FindCosmos(without), Declared(false)).Sql.Should().NotContain("ORDER BY",
                "where nothing says so, the placement rule refuses it as it always has");
        }

        /// <summary>
        /// Both claims are needed, and a nullable type is not one of them.
        /// </summary>
        [TestMethod]
        public void PresenceAloneAndANullableTypeAreBothTooWeakToSort()
        {
            const string Sql = """SELECT JSON_VALUE(c."DOC", '$.at') AS "at" FROM items AS c ORDER BY 1""";

            foreach (var schema in new[]
            {
                // There, but of no stated type -- the accessor answers null for an object or an array.
                """{ "type": "object", "required": ["at"] }""",
                // A scalar, but not necessarily there.
                """{ "type": "object", "properties": { "at": { "type": "string" } } }""",
                // There and a scalar, but a null is admitted beside it.
                """{ "type": "object", "required": ["at"], "properties": { "at": { "type": ["string","null"] } } }""",
            })
            {
                var container = new CosmosContainerMetadata("items", new[] { "/ref" })
                    .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(schema)));

                Query(FindCosmos(PlanToCosmos(Sql, container, out _)), container).Sql
                    .Should().NotContain("ORDER BY", "for " + schema);
            }
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
