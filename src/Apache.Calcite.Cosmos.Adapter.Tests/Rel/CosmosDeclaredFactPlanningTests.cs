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
        /// A set of ids over a declared path reaches the service, and reaches the batch read. #99.
        /// </summary>
        /// <remarks>
        /// An <c>IN</c> arrives folded into a <c>SEARCH</c> over a <c>Sarg</c>, which is one node
        /// rather than the equalities it stands for — so the lowering that an <c>=</c> gets was
        /// passing it by, and the membership fell to a client-side recheck over a scan. Expanded it is
        /// a disjunction of equalities, each lowered the way a lone one is.
        /// </remarks>
        [TestMethod]
        public void ASetOfIdsOverADeclaredPathReachesTheService()
        {
            const string Ids = """
            { "type": "object",
              "properties": { "id": { "type": "string",
                "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }
            """;

            var container = new CosmosContainerMetadata("items", new[] { "/k" })
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(Ids)));

            const string Other = "123e4567-e89b-12d3-a456-426614174001";

            var best = PlanToCosmos(
                $"""SELECT c."DOC" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.k') = 'p' AND CAST(JSON_VALUE(c."DOC", '$.id') AS UUID) IN (UUID'{Canonical}', UUID'{Other}')""",
                container, out _);

            var query = Query(FindCosmos(best), container);

            query.Sql.Should().Contain("c.id = @", "each point of the set lowers the way a lone equality does");
            query.Parameters.Should().Contain(p => (p.Value as string) == Canonical);
            query.Parameters.Should().Contain(p => (p.Value as string) == Other);

            PlanText(best).Should().NotContain("ClrEnumerableFilter",
                "and the membership is not left to a client-side recheck: " + PlanText(best));

            query.PointReadIds.Should().BeEquivalentTo(new[] { Canonical, Other },
                "which is what makes the batch read reachable through a typed column at all");
        }

        /// <summary>
        /// The schema of a catalog row: an identifier stored as a UUID, and a name that is always
        /// there so that a sort on it is not refused for null placement.
        /// </summary>
        const string Catalog = """
        { "type": "object",
          "required": ["ref", "name"],
          "properties": {
            "ref": { "type": "string",
                     "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" },
            "name": { "type": "string" } } }
        """;

        /// <summary>
        /// The same, with the identifier left undeclared, so that the only difference between the two
        /// plans is whether the projected cast has a stored form to render as.
        /// </summary>
        const string CatalogWithoutTheIdentifier = """
        { "type": "object",
          "required": ["name"],
          "properties": { "name": { "type": "string" } } }
        """;

        /// <summary>
        /// A container over the given schema.
        /// </summary>
        /// <param name="schema">The declared JSON Schema.</param>
        /// <returns>The container metadata.</returns>
        static CosmosContainerMetadata Container(string schema) =>
            new CosmosContainerMetadata("items", new[] { "/ref" })
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(schema)));

        /// <summary>
        /// Selecting a declared UUID renders, so the sort and the page beneath it reach the service. #100.
        /// </summary>
        /// <remarks>
        /// The read side of the reasoning the filter already leans on. Until the cast rendered, a
        /// projection carrying one stayed in process, and a sort cannot be pushed through a projection
        /// that did not convert — so a query selecting an identifier and ordering by a name read the
        /// container whole and sorted it in memory, while the same query without the identifier pushed
        /// both. Every query that returns an entity selects its identifier.
        /// </remarks>
        [TestMethod]
        public void ProjectingADeclaredUuidLetsTheSortAndThePagePush()
        {
            const string Sql = """
            SELECT CAST(JSON_VALUE(c."DOC", '$.ref') AS UUID) AS "Id", JSON_VALUE(c."DOC", '$.name') AS "Name"
            FROM items AS c ORDER BY 2 FETCH NEXT 20 ROWS ONLY
            """;

            var declared = Container(Catalog);
            var with = PlanToCosmos(Sql, declared, out _);
            var query = Query(FindCosmos(with), declared);

            query.Sql.Should().Contain("IS_PRIMITIVE(c.ref) ? c.ref : null",
                "the path is sent under the accessor's own guard, the cast being put back by the reader");
            query.Sql.Should().Contain("ORDER BY c.name", "which is what the projection was blocking: " + query.Sql);
            query.MaxItemCount.Should().Be(20, "and the page rides on the sort");

            PlanText(with).Should().NotContain("ClrEnumerableSort",
                "so nothing sorts the container in memory: " + PlanText(with));
        }

        /// <summary>
        /// And it is the declaration doing it: the same query over a container that says nothing about
        /// the identifier keeps the cast, and with it the sort, in process.
        /// </summary>
        [TestMethod]
        public void WithoutTheDeclarationTheProjectedCastStillHoldsTheSortBack()
        {
            const string Sql = """
            SELECT CAST(JSON_VALUE(c."DOC", '$.ref') AS UUID) AS "Id", JSON_VALUE(c."DOC", '$.name') AS "Name"
            FROM items AS c ORDER BY 2 FETCH NEXT 20 ROWS ONLY
            """;

            var declared = Container(CatalogWithoutTheIdentifier);
            var best = PlanToCosmos(Sql, declared, out _);

            Query(FindCosmos(best), declared).Sql.Should().NotContain("ORDER BY",
                "nothing says what the stored text is, so the cast has no form to render as");

            PlanText(best).Should().Contain("ClrEnumerableSort",
                "and the sort stays above the projection that did not convert: " + PlanText(best));
        }

        /// <summary>
        /// Dropping the identifier from the select list is what used to be needed, and is the plan the
        /// declared one now matches.
        /// </summary>
        /// <remarks>
        /// The control for the pair above. Both halves of the query were always pushable on their own;
        /// what the issue was about is that selecting the identifier gave up the other half.
        /// </remarks>
        [TestMethod]
        public void TheSameQueryWithoutTheIdentifierAlwaysPushed()
        {
            const string Sql = """
            SELECT JSON_VALUE(c."DOC", '$.name') AS "Name" FROM items AS c ORDER BY 1 FETCH NEXT 20 ROWS ONLY
            """;

            var declared = Container(CatalogWithoutTheIdentifier);
            var query = Query(FindCosmos(PlanToCosmos(Sql, declared, out _)), declared);

            query.Sql.Should().Contain("ORDER BY c.name");
            query.MaxItemCount.Should().Be(20);
        }

        /// <summary>
        /// An uppercase container is addressable on the same terms, the projection asking only that
        /// the stored text be a UUID rather than which spelling it is.
        /// </summary>
        [TestMethod]
        public void AnUppercaseContainerProjectsOnTheSameTerms()
        {
            const string Upper = """
            { "type": "object",
              "required": ["ref", "name"],
              "properties": {
                "ref": { "type": "string",
                         "pattern": "^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$" },
                "name": { "type": "string" } } }
            """;

            const string Sql = """
            SELECT CAST(JSON_VALUE(c."DOC", '$.ref') AS UUID) AS "Id", JSON_VALUE(c."DOC", '$.name') AS "Name"
            FROM items AS c ORDER BY 2
            """;

            var declared = Container(Upper);
            Query(FindCosmos(PlanToCosmos(Sql, declared, out _)), declared).Sql
                .Should().Contain("ORDER BY c.name");
        }

        /// <summary>
        /// A rendered identifier addresses nothing afterwards, so ordering <em>by</em> it is still
        /// refused.
        /// </summary>
        /// <remarks>
        /// The scope line. Equality is all the form was asked for; whether the lexical order of the
        /// stored strings is the order Calcite compares UUIDs in is a separate claim, and the guarded
        /// path is not the value either way. The column binding to no path is what keeps the question
        /// from being asked at all.
        /// </remarks>
        [TestMethod]
        public void OrderingByTheRenderedIdentifierIsStillRefused()
        {
            const string Sql = """
            SELECT CAST(JSON_VALUE(c."DOC", '$.ref') AS UUID) AS "Id" FROM items AS c ORDER BY 1
            """;

            var declared = Container(Catalog);
            Query(FindCosmos(PlanToCosmos(Sql, declared, out _)), declared).Sql
                .Should().NotContain("ORDER BY", "the projected column resolves to no path to order by");
        }

        /// <summary>
        /// A fact that holds only under a guard does not render a projection, even where the query
        /// discharges the guard.
        /// </summary>
        /// <remarks>
        /// Where the scope line falls, and it falls short of what is provable. A projection carries no
        /// predicate of its own, so what it may lean on is what the declaration states outright; the
        /// <c>kind = 'B'</c> beneath it does discharge the guard for every row that arrives, and
        /// reading that off the subtree is a further step this does not take. The filter still pushes,
        /// and the projection stays in process.
        /// </remarks>
        [TestMethod]
        public void AGuardedFormDoesNotRenderAProjection()
        {
            var container = Declared(true);

            var best = PlanToCosmos(
                $"""SELECT {Ref} AS "Id" FROM items AS c WHERE {Kind} = 'B'""",
                container, out _);

            Query(FindCosmos(best), container).Sql
                .Should().NotContain("IS_PRIMITIVE(c.ref)", "the form is declared only of the documents the guard admits");

            PlanText(best).Should().Contain("ClrEnumerableProject(Id=[CAST(JSON_VALUE(",
                "so the cast is still computed in process: " + PlanText(best));
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

        /// <summary>
        /// The schema a temporal sort turns on: the path is there, is a scalar, and — the part that
        /// decides the ordering — is or is not confined to one fixed ISO-8601 UTC shape.
        /// </summary>
        /// <param name="pattern">The declared <c>pattern</c>, or <c>null</c> for none.</param>
        /// <returns>The container.</returns>
        static CosmosContainerMetadata Instant(string? pattern)
        {
            var declared = pattern is null ? "" : $""", "pattern": "{pattern.Replace("\\", "\\\\")}" """;

            return new CosmosContainerMetadata("items", new[] { "/ref" })
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(
                    $$"""
                    { "type": "object", "required": ["at"],
                      "properties": { "at": { "type": "string"{{declared}} } } }
                    """)));
        }

        const string Instants = """SELECT JSON_VALUE(c."DOC", '$.at' RETURNING TIMESTAMP) AS "at" FROM items AS c ORDER BY 1""";

        /// <summary>
        /// A <c>TIMESTAMP</c> sort over a path whose shape is not confined must not push.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cosmos has no date type, so the path holds a string and <c>ORDER BY c.at</c> is a
        /// lexicographic string sort — which is the order the plan asked for only where every value
        /// shares one shape. The .NET SDK's default serializer trims trailing zeros from the fraction,
        /// so a container written without a converter holds <c>…:56Z</c> beside <c>…:56.5Z</c> and the
        /// two sort the wrong way round: <c>'Z'</c> is 0x5A and <c>'.'</c> is 0x2E.
        /// </para>
        /// <para>
        /// The presence and scalar claims are both made here, so null placement is settled and the
        /// ordering is the only thing left to refuse it. That is what makes this a test of the
        /// ordering rather than of the placement rule beside it.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void AnUnconfinedShapeWillNotCarryATemporalSort()
        {
            var container = Instant(null);

            Query(FindCosmos(PlanToCosmos(Instants, container, out _)), container).Sql
                .Should().NotContain("ORDER BY",
                    "nothing says the stored strings share a shape, so their lexical order is not the plan's order");
        }

        /// <summary>
        /// The same sort pushes once the declared pattern confines the shape.
        /// </summary>
        [TestMethod]
        public void AFixedIsoShapeCarriesATemporalSort()
        {
            foreach (var pattern in new[]
            {
                "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$",
                @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$",
            })
            {
                var container = Instant(pattern);

                Query(FindCosmos(PlanToCosmos(Instants, container, out _)), container).Sql
                    .Should().Contain("ORDER BY c.at",
                        "one fixed shape makes the lexical order chronological, for " + pattern);
            }
        }

        /// <summary>
        /// A range over an instant reaches the statement, in both spellings a query writes it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both shapes name the same path and differ only in where the temporal type comes from — a
        /// <c>CAST</c> over the text accessor, or the accessor's own <c>RETURNING</c> clause. The
        /// second carries no text operand to compare against, so the lowering rebuilds the accessor
        /// without the clause; that this row passes is what says it rebuilt the right one.
        /// </para>
        /// <para>
        /// The whole predicate leaves as one statement: an <c>ClrEnumerableFilter</c> above the
        /// converter would mean the comparison was rechecked in process, which is what happened before
        /// the form licensed it.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void ARangeOverAFixedShapeReachesTheStatement()
        {
            var confined = Instant(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$");

            foreach (var read in new[]
            {
                """JSON_VALUE(c."DOC", '$.at' RETURNING TIMESTAMP)""",
                """CAST(JSON_VALUE(c."DOC", '$.at') AS TIMESTAMP)""",
            })
            {
                var best = PlanToCosmos(
                    $"""SELECT c."DOC" FROM items AS c WHERE {read} > TIMESTAMP '2024-01-15 12:30:00'""",
                    confined, out _);

                Query(FindCosmos(best), confined).Sql.Should().Contain("c.at > @",
                    "the ordering lowers to a string comparison the service can serve, for " + read);

                PlanText(best).Should().NotContain("ClrEnumerableFilter",
                    "and nothing is left to recheck in process, for " + read);
            }
        }

        /// <summary>
        /// The same range over an unconfined path stays in process.
        /// </summary>
        [TestMethod]
        public void ARangeOverAnUnconfinedShapeStaysInProcess()
        {
            var container = Instant(null);

            var best = PlanToCosmos(
                """SELECT c."DOC" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.at' RETURNING TIMESTAMP) > TIMESTAMP '2024-01-15 12:30:00'""",
                container, out _);

            Query(FindCosmos(best), container).Sql.Should().NotContain("c.at > ",
                "nothing says the stored strings share a shape");

            PlanText(best).Should().Contain("ClrEnumerableFilter",
                "so the comparison is still applied where it always was");
        }


    }

}
