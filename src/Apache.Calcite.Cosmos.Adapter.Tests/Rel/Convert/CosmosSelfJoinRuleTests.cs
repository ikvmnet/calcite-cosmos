using System.Collections.Generic;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;

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

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel.Convert
{

    /// <summary>
    /// When a join of a container to itself is read as one read of it, and when it is not.
    /// </summary>
    /// <remarks>
    /// Planned with a bare Volcano planner and no service, so each case is one question about the rule.
    /// <c>CosmosSelfJoinTests</c> is where the rows are compared, against the emulator. Every declining
    /// case here is one that would merge if the thing it lacks were present — which is what makes it a
    /// test of that thing rather than of something else that happened to stop the rule.
    /// </remarks>
    public class CosmosSelfJoinRuleTests
    {

        const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

        /// <summary>
        /// A schema giving <c>guid</c> a canonical UUID form, <c>linkId</c> and <c>code</c> one type, and
        /// <c>loose</c> none.
        /// </summary>
        static readonly string Schema = $$"""
            { "type": "object",
              "properties": {
                "type": { "type": "string" },
                "kind": { "type": "string" },
                "linkId": { "type": "integer" },
                "code": { "type": "string" },
                "guid": { "type": "string", "pattern": "{{Uuid}}" },
                "ref": { "type": "string" } } }
            """;

        static CosmosContainerMetadata Container(string name = "items", string partitionKey = "/linkId", string? schema = null, IReadOnlyList<IReadOnlyList<string>>? uniqueKeys = null) =>
            new CosmosContainerMetadata(name, new[] { partitionKey }, uniqueKeys: uniqueKeys)
                .WithFacts(CosmosSchemaFacts.ReadFrom(new com.fasterxml.jackson.databind.ObjectMapper().readTree(schema ?? Schema)));

        static CosmosConstraint Unique(string path, string? filterPath = null, object? filterValue = null) =>
            CosmosConstraint.Unique.Of(
                new[] { path },
                filterPath is null ? null : new[] { new CosmosFact(CosmosConstraint.PathOf(filterPath)!, new CosmosClaim.EqualTo(filterValue)) },
                CosmosConstraintSource.Declared)!;

        static RelNode Plan(string sql, params CosmosContainerMetadata[] containers)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var rootSchema = CalciteSchema.createRootSchema(false);

            var tables = new List<CosmosTable>();
            foreach (var container in containers)
            {
                var table = new CosmosTable(container);
                tables.Add(table);
                rootSchema.add(container.Name, table);
            }

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Adapter.Sql.CosmosOperators.Instance);
            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(org.apache.calcite.rel.RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var table in tables)
                foreach (var rule in CosmosRules.GetRules(table.Convention))
                    planner.addRule(rule);

            foreach (var rule in ClrCursorRules.Rules())
                planner.addRule(rule);

            planner.setRoot(planner.changeTraits(logical, logical.getTraitSet().replace(ClrCursorConvention.Instance).simplify()));
            return planner.findBestExp();
        }

        static string Text(RelNode rel) => RelOptUtil.toString(rel).Replace("\r\n", "\n");

        static int Scans(RelNode rel) => Regex.Matches(Text(rel), "CosmosTableScan").Count;

        /// <summary>
        /// Two views of the documents of one kind, each keyed by the guid, the way a federation writes them.
        /// </summary>
        static string Views(string join, string key = """CAST(JSON_VALUE(c."DOC", '$.guid') AS UUID)""", string on = """a."K" = b."K" """, string left = "'A'", string right = "'A'", string table = "items", string otherTable = "items") => $"""
            SELECT a."K", a."R", b."C"
            FROM (SELECT {key} AS "K", JSON_VALUE(c."DOC", '$.ref') AS "R" FROM {table} AS c WHERE JSON_VALUE(c."DOC", '$.type') = {left}) AS a
            {join} JOIN (SELECT {key} AS "K", JSON_VALUE(c."DOC", '$.code') AS "C" FROM {otherTable} AS c WHERE JSON_VALUE(c."DOC", '$.type') = {right}) AS b
            ON {on}
            """;

        [Fact]
        public void AnInnerJoinOnADeclaredKeyIsOneRead()
        {
            var plan = Plan(Views("INNER"), Container().WithConstraints(new[] { Unique("/guid") }));

            Scans(plan).Should().Be(1, Text(plan));
            Text(plan).Should().NotContain("Join");
        }

        [Fact]
        public void ALeftJoinOnADeclaredKeyIsOneRead()
        {
            var plan = Plan(Views("LEFT", right: "'B'"), Container().WithConstraints(new[] { Unique("/guid") }));

            Scans(plan).Should().Be(1, Text(plan));
            Text(plan).Should().Contain("CASE", "the right side's columns are its own only where its filter holds:\n" + Text(plan));
        }

        [Fact]
        public void ARightJoinOnADeclaredKeyIsOneRead()
        {
            var plan = Plan(Views("RIGHT", left: "'B'"), Container().WithConstraints(new[] { Unique("/guid") }));

            Scans(plan).Should().Be(1, Text(plan));
        }

        [Fact]
        public void AnUndeclaredKeyStaysAJoin()
        {
            var plan = Plan(Views("INNER"), Container());

            Scans(plan).Should().Be(2, Text(plan));
        }

        /// <summary>
        /// A document with no key is one row of a full join from each side, and would be one row merged.
        /// </summary>
        [Fact]
        public void AFullJoinStaysAJoin()
        {
            var plan = Plan(Views("FULL"), Container().WithConstraints(new[] { Unique("/guid") }));

            Scans(plan).Should().Be(2, Text(plan));
        }

        /// <summary>
        /// <c>IS NOT DISTINCT FROM</c> pairs a null with a null, and a key says nothing about documents
        /// holding none.
        /// </summary>
        [Fact]
        public void IsNotDistinctFromIsNotAKeyEquality()
        {
            var plan = Plan(Views("INNER", on: """a."K" IS NOT DISTINCT FROM b."K" """), Container().WithConstraints(new[] { Unique("/guid") }));

            Scans(plan).Should().Be(2, Text(plan));
        }

        /// <summary>
        /// Two containers are not one container, whatever they declare.
        /// </summary>
        [Fact]
        public void TwoContainersStayAJoin()
        {
            var plan = Plan(Views("INNER", otherTable: "others"),
                Container().WithConstraints(new[] { Unique("/guid") }),
                Container("others").WithConstraints(new[] { Unique("/guid") }));

            Scans(plan).Should().Be(2, Text(plan));
        }

        /// <summary>
        /// A <c>UUID</c> cast reads <c>ABC…</c> and <c>abc…</c> alike, so without a canonical form a unique
        /// string is not a unique <c>UUID</c>.
        /// </summary>
        [Fact]
        public void AUuidKeyWithoutACanonicalFormStaysAJoin()
        {
            var plan = Plan(Views("INNER", key: """CAST(JSON_VALUE(c."DOC", '$.ref') AS UUID)"""), Container().WithConstraints(new[] { Unique("/ref") }));

            Scans(plan).Should().Be(2, Text(plan));
        }

        /// <summary>
        /// Text reads the number <c>1</c> and the string <c>"1"</c> alike, so a text key needs the path to
        /// hold one type — and merges where it does.
        /// </summary>
        [Fact]
        public void ATextKeyNeedsOneType()
        {
            var untyped = Plan(Views("INNER", key: """JSON_VALUE(c."DOC", '$.loose')"""), Container().WithConstraints(new[] { Unique("/loose") }));
            Scans(untyped).Should().Be(2, Text(untyped));

            var typed = Plan(Views("INNER", key: """JSON_VALUE(c."DOC", '$.code')"""), Container().WithConstraints(new[] { Unique("/code") }));
            Scans(typed).Should().Be(1, Text(typed));
        }

        /// <summary>
        /// A constraint scoped to one kind of document is used where both sides are proved to be of it.
        /// </summary>
        [Fact]
        public void AScopedConstraintNeedsBothSidesToProveItsFilter()
        {
            var container = Container().WithConstraints(new[] { Unique("/guid", "/type", "A") });

            var both = Plan(Views("INNER"), container);
            Scans(both).Should().Be(1, Text(both));

            // Unique among the A documents says nothing about an A and a B sharing a guid.
            var one = Plan(Views("LEFT", right: "'B'"), container);
            Scans(one).Should().Be(2, Text(one));
        }

        /// <summary>
        /// <c>UNIQUE(pk, id)</c> is the service's, and needs nothing declared; <c>id</c> alone is not unique.
        /// </summary>
        [Fact]
        public void ThePartitionKeyWithIdIsUniqueAndIdAloneIsNot()
        {
            const string both = """
                SELECT a."id", b."R"
                FROM (SELECT JSON_VALUE(c."DOC", '$.linkId') AS "L", c."id" AS "id" FROM items AS c) AS a
                INNER JOIN (SELECT JSON_VALUE(c."DOC", '$.linkId') AS "L", c."id" AS "id", JSON_VALUE(c."DOC", '$.ref') AS "R" FROM items AS c) AS b
                ON a."L" = b."L" AND a."id" = b."id"
                """;

            var merged = Plan(both, Container());
            Scans(merged).Should().Be(1, Text(merged));

            var idOnly = Plan(both.Replace("""a."L" = b."L" AND """, ""), Container());
            Scans(idOnly).Should().Be(2, Text(idOnly));
        }

        /// <summary>
        /// Where the partition key is <c>/id</c>, <c>UNIQUE(pk, id)</c> is <c>UNIQUE(id)</c>.
        /// </summary>
        [Fact]
        public void IdAloneIsUniqueWhereItIsThePartitionKey()
        {
            const string sql = """
                SELECT a."id", b."R"
                FROM (SELECT c."id" AS "id" FROM items AS c) AS a
                INNER JOIN (SELECT c."id" AS "id", JSON_VALUE(c."DOC", '$.ref') AS "R" FROM items AS c) AS b
                ON a."id" = b."id"
                """;

            var plan = Plan(sql, Container(partitionKey: "/id"));
            Scans(plan).Should().Be(1, Text(plan));
        }

        /// <summary>
        /// A unique key policy is unique within a partition: with the partition key it is a constraint of
        /// the container, and without it it is not.
        /// </summary>
        [Fact]
        public void AUniqueKeyPolicyNeedsThePartitionKey()
        {
            var container = Container(uniqueKeys: new[] { new[] { "/code" } });

            const string sql = """
                SELECT a."C", b."R"
                FROM (SELECT JSON_VALUE(c."DOC", '$.linkId') AS "L", JSON_VALUE(c."DOC", '$.code') AS "C" FROM items AS c) AS a
                INNER JOIN (SELECT JSON_VALUE(c."DOC", '$.linkId') AS "L", JSON_VALUE(c."DOC", '$.code') AS "C", JSON_VALUE(c."DOC", '$.ref') AS "R" FROM items AS c) AS b
                ON a."L" = b."L" AND a."C" = b."C"
                """;

            var merged = Plan(sql, container);
            Scans(merged).Should().Be(1, Text(merged));

            var codeOnly = Plan(sql.Replace("""a."L" = b."L" AND """, ""), container);
            Scans(codeOnly).Should().Be(2, Text(codeOnly));
        }

        /// <summary>
        /// The merge closes over itself, so a chain of views on one key is one read however long it is.
        /// </summary>
        [Fact]
        public void AChainOfJoinsIsOneRead()
        {
            const string sql = """
                SELECT a."K", b."R", c2."C"
                FROM (SELECT CAST(JSON_VALUE(c."DOC", '$.guid') AS UUID) AS "K" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.type') = 'A') AS a
                INNER JOIN (SELECT CAST(JSON_VALUE(c."DOC", '$.guid') AS UUID) AS "K", JSON_VALUE(c."DOC", '$.ref') AS "R" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.type') = 'A') AS b ON a."K" = b."K"
                LEFT JOIN (SELECT CAST(JSON_VALUE(c."DOC", '$.guid') AS UUID) AS "K", JSON_VALUE(c."DOC", '$.code') AS "C" FROM items AS c WHERE JSON_VALUE(c."DOC", '$.kind') = 'x') AS c2 ON b."K" = c2."K"
                """;

            var plan = Plan(sql, Container().WithConstraints(new[] { Unique("/guid") }));
            Scans(plan).Should().Be(1, Text(plan));
        }

        /// <summary>
        /// And the key's null test reaches the service: <c>k = k</c> over one document is
        /// <c>k IS NOT NULL</c>, which over a <c>UUID</c> cast is the same test over the text it casts.
        /// </summary>
        [Fact]
        public void TheKeysNullTestIsPushed()
        {
            var plan = Plan(Views("INNER"), Container().WithConstraints(new[] { Unique("/guid") }));

            Text(plan).Should().Contain("IS NOT NULL(JSON_VALUE($0, '$.guid'))", Text(plan));
            Text(plan).Should().NotContain("ClrCursorFilter", Text(plan));
        }

    }

}
