using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;

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

namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning
{

    /// <summary>
    /// What each statement pushes, written out: the planner corpus, and probes over a discriminated container, each
    /// statement's plan and the Cosmos SQL it sends. Explicit, and asserting nothing — it is run on two commits and the
    /// two files compared, which is how a change to what the planner reads is shown to push more, or less, and where.
    /// </summary>
    /// <remarks>
    /// Run it with <c>-explicit only</c>, <c>COSMOS_PROBE_OUT</c> naming the file to write.
    /// </remarks>
    public class CosmosPushdownProbeTests
    {

        const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";

        /// <summary>
        /// A container of three kinds: <c>label</c> a string in kind <c>a</c> and an integer in kind <c>b</c>, a UUID
        /// <c>ref</c> in kinds <c>a</c> and <c>c</c>, and <c>kind</c> required and one of the three.
        /// </summary>
        internal const string KindsSchema = $$"""
        {
          "type": "object",
          "required": ["kind"],
          "properties": { "kind": { "enum": ["a", "b", "c"] }, "n": { "type": "integer" } },
          "oneOf": [
            { "properties": { "kind": { "const": "a" }, "label": { "type": "string" }, "ref": { "type": "string", "pattern": "{{Uuid}}" } } },
            { "properties": { "kind": { "const": "b" }, "label": { "type": "integer" } } },
            { "properties": { "kind": { "const": "c" }, "ref": { "type": "string", "pattern": "{{Uuid}}" } } }
          ]
        }
        """;

        const string Kind = """JSON_VALUE(c."DOC", '$.kind')""";
        const string Label = """JSON_VALUE(c."DOC", '$.label')""";
        const string Ref = """CAST(JSON_VALUE(c."DOC", '$.ref') AS UUID)""";
        const string N = """JSON_VALUE(c."DOC", '$.n')""";
        const string Canonical = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// The probes over the kinds: what a sibling proves, what the declaration guarantees, what nothing satisfies.
        /// </summary>
        internal static readonly (string Name, string Where)[] KindProbes =
        [
            ("sibling proves a string, equality", $"{Kind} = 'a' AND {Label} = '30'"),
            ("sibling proves a string, ordering", $"{Kind} = 'a' AND {Label} > 'm'"),
            ("disequalities leave one kind", $"{Kind} <> 'b' AND {Kind} <> 'c' AND {Label} = '30'"),
            ("a required enum covered", $"{Kind} IN ('a', 'b', 'c')"),
            ("a required enum covered, beside a conjunct", $"{Kind} IN ('a', 'b', 'c') AND {Label} = 'x'"),
            ("a disequality outside the enum", $"{Kind} <> 'z' AND {Label} = 'x'"),
            ("a kind the declaration has not", $"{Kind} = 'd'"),
            ("two kinds with the form", $"{Kind} IN ('a', 'c') AND {Ref} = UUID '{Canonical}'"),
            ("each kind spelled per arm", $"(({Kind} = 'a' AND {Label} = 'x') OR ({Kind} = 'c' AND {N} = '1')) AND {Ref} = UUID '{Canonical}'"),
            ("a disjunction its sibling implies", $"{Kind} = 'a' AND ({Ref} = UUID '{Canonical}' OR {Kind} = 'a')"),
            ("a rendering of an integer", $"{Kind} = 'b' AND {Label} = '30'"),
            ("a text inequality says the path holds a scalar", $"{Label} <> 'x' AND {Kind} = 'a' AND {Label} > 'm'"),
            ("a definedness test", $"IS_DEFINED({Label}) AND {Kind} = 'a'"),
            ("a guarded form under one kind", $"{Kind} = 'c' AND {Ref} > UUID '{Canonical}'"),
            ("a guarded form under the other", $"{Kind} = 'b' AND {Ref} = UUID '{Canonical}'"),
        ];

        [Fact(Explicit = true)]
        public void WriteWhatEachStatementPushes()
        {
            var output = new StringBuilder();

            var harness = new PlannerHarness();
            foreach (var query in PlannerQueries.All)
            {
                output.Append("## corpus ").AppendLine(query.ToString());
                try
                {
                    output.AppendLine(Describe(harness.PlanToAsync(query.Sql)));
                }
                catch (Exception e)
                {
                    output.AppendLine("  fails: " + e.GetType().Name);
                }
            }

            var kinds = new CosmosContainerMetadata("kinds", new[] { "/kind" })
                .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(KindsSchema)));

            foreach (var (name, where) in KindProbes)
            {
                output.Append("## kinds ").AppendLine(name);
                try
                {
                    output.AppendLine(Describe(Plan($"""SELECT c."id" FROM kinds AS c WHERE {where}""", kinds)));
                }
                catch (Exception e)
                {
                    output.AppendLine("  fails: " + e.GetType().Name + ": " + e.Message);
                }
            }

            File.WriteAllText(Environment.GetEnvironmentVariable("COSMOS_PROBE_OUT") ?? Path.Combine(Path.GetTempPath(), "cosmos-probes.txt"), output.ToString());
        }

        /// <summary>
        /// The plan, and every statement its Cosmos parts send.
        /// </summary>
        static string Describe(RelNode planned)
        {
            var text = new StringBuilder();
            text.AppendLine(PlannerHarness.Explain(planned));

            foreach (var cosmos in Cosmos(planned))
            {
                if (PlannerHarness.TableOf(cosmos) is not CosmosTable table)
                    continue;

                try
                {
                    var query = PlannerHarness.Implement(cosmos, table.Container);
                    text.Append("  sends: ").AppendLine(query.Sql);
                }
                catch (Exception e)
                {
                    text.Append("  implements: ").AppendLine(e.GetType().Name + ": " + e.Message);
                }
            }

            return text.ToString();
        }

        static IEnumerable<RelNode> Cosmos(RelNode node)
        {
            if (node is Apache.Calcite.Cosmos.Adapter.Rel.CosmosRel)
            {
                yield return node;
                yield break;
            }

            for (var i = 0; i < node.getInputs().size(); i++)
                foreach (var found in Cosmos((RelNode)node.getInputs().get(i)))
                    yield return found;
        }

        /// <summary>
        /// Plans a statement over one container to the CLR convention, the Cosmos rules beside the way out.
        /// </summary>
        internal static RelNode Plan(string sql, CosmosContainerMetadata container)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(container);

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add(container.Name, table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Adapter.Sql.CosmosOperators.Instance);
            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var rule in CosmosRules.GetRules(table.Convention))
                planner.addRule(rule);

            foreach (var rule in Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(Apache.Calcite.Extensions.Adapter.Cursor.ClrCursorConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

    }

}
