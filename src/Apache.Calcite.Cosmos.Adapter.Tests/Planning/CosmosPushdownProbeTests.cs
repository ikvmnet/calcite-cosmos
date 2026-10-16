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

        /// <summary>
        /// A container declaring what a sort asks: <c>t</c> a string or null, <c>s</c> and <c>u</c> strings, <c>k</c> an
        /// integer or null, <c>b</c> a boolean; none of them required.
        /// </summary>
        internal const string OrderingSchema = """
        { "type": "object", "properties": { "t": { "type": ["string", "null"] }, "s": { "type": "string" }, "u": { "type": "string" }, "k": { "type": ["integer", "null"] }, "b": { "type": "boolean" } } }
        """;

        const string T = """JSON_VALUE(c."DOC", '$.t')""";
        const string K = """JSON_VALUE(c."DOC", '$.k' RETURNING INTEGER)""";

        /// <summary>
        /// The probes of the clauses after a filter, over the kinds and over <see cref="OrderingSchema"/>: what the rows
        /// the filter kept let a projection, a sort and a grouping lean on, and a control for each that leans on nothing
        /// the filter proves.
        /// </summary>
        internal static readonly (string Name, string Container, string Sql, bool Ordered)[] ClauseProbes =
        [
            ("a projection of a form the filter proves", "kinds", $"""SELECT {Ref} AS "Id" FROM kinds AS c WHERE {Kind} = 'a'""", false),
            ("a projection of a form a kind lacks", "kinds", $"""SELECT {Ref} AS "Id" FROM kinds AS c WHERE {Kind} = 'b'""", false),
            ("a sort of a string the filter proves present", "kinds", $"""SELECT {Label} FROM kinds AS c WHERE {Kind} = 'a' AND {Label} IS NOT NULL ORDER BY 1""", true),
            ("a sort of a string an ordering proves present", "kinds", $"""SELECT {Label} FROM kinds AS c WHERE {Kind} = 'a' AND {Label} > 'm' ORDER BY 1""", true),
            ("a sort of a string that may be absent", "kinds", $"""SELECT {Label} FROM kinds AS c WHERE {Kind} = 'a' ORDER BY 1""", true),
            ("a grouping by a string the filter proves", "kinds", $"""SELECT {Label}, COUNT(*) FROM kinds AS c WHERE {Kind} = 'a' GROUP BY {Label}""", false),
            ("a grouping by an integer's rendering", "kinds", $"""SELECT {Label}, COUNT(*) FROM kinds AS c WHERE {Kind} = 'b' GROUP BY {Label}""", false),
            ("a distinct of a string the filter proves", "kinds", $"""SELECT DISTINCT {Label} FROM kinds AS c WHERE {Kind} = 'a'""", false),
            ("a distinct of a string the filter proves present", "kinds", $"""SELECT DISTINCT {Label} FROM kinds AS c WHERE {Kind} = 'a' AND {Label} IS NOT NULL""", false),
            ("a grouping by the required kind", "kinds", $"""SELECT {Kind}, COUNT(*) FROM kinds AS c GROUP BY {Kind}""", false),
            ("a sort of a declared string the filter proves present", "ordering", $"""SELECT {T} FROM ordering AS c WHERE {T} IS NOT NULL ORDER BY 1""", true),
            ("the same, descending", "ordering", $"""SELECT {T} FROM ordering AS c WHERE {T} IS NOT NULL ORDER BY 1 DESC""", true),
            ("the same, paged", "ordering", $"""SELECT {T} FROM ordering AS c WHERE {T} IS NOT NULL ORDER BY 1 FETCH NEXT 3 ROWS ONLY""", true),
            ("a sort of a declared string a comparison proves present", "ordering", $"""SELECT {T} FROM ordering AS c WHERE {T} > 'A' ORDER BY 1""", true),
            ("a sort of a declared integer the filter proves present", "ordering", $"""SELECT {K} FROM ordering AS c WHERE {K} IS NOT NULL ORDER BY 1""", true),
            ("a sort beside a filter over another path", "ordering", $"""SELECT {T} FROM ordering AS c WHERE c."id" IS NOT NULL ORDER BY 1""", true),
            ("a distinct of a declared string the filter proves present", "ordering", $"""SELECT DISTINCT {T} FROM ordering AS c WHERE {T} IS NOT NULL""", false),
            ("a grouping by a declared string the filter proves present", "ordering", $"""SELECT {T}, COUNT(*) FROM ordering AS c WHERE {T} IS NOT NULL GROUP BY {T}""", false),
            ("a sort over a distinct the filter proves present", "ordering", $"""SELECT DISTINCT {T} FROM ordering AS c WHERE {T} IS NOT NULL ORDER BY 1""", true),
        ];

        const string Category = """JSON_VALUE(c."DOC", '$.category')""";

        /// <summary>
        /// The probes of routing and of the operations that apply no predicate: what a statement pins, over the kinds,
        /// partitioned on <c>kind</c>, and over a container partitioned on a <c>category</c> it declares nothing of,
        /// whose partitions are <c>a</c>, <c>b</c> and the string <c>30</c> — the differential suite's <c>typed</c>.
        /// </summary>
        /// <remarks>
        /// Routing changes no row, and a wrong one loses rows: a stored <c>30</c> and a stored <c>"30"</c> are two
        /// partitions and one text. The ids are the differential's documents, so that a read reaches one.
        /// </remarks>
        internal static readonly (string Name, string Container, string Sql, bool Ordered)[] RoutingProbes =
        [
            ("a point read", "kinds", $"""SELECT c."id" FROM kinds AS c WHERE {Kind} = 'a' AND c."id" = '1'""", false),
            ("a point read of a document in another partition", "kinds", $"""SELECT c."id" FROM kinds AS c WHERE {Kind} = 'a' AND c."id" = '4'""", false),
            ("a point read beside a conjunct the pins do not make true", "kinds", $"""SELECT c."id" FROM kinds AS c WHERE {Kind} = 'a' AND c."id" = '1' AND {Label} = '30'""", false),
            ("a batch of point reads", "kinds", $"""SELECT c."id" FROM kinds AS c WHERE {Kind} = 'a' AND c."id" IN ('1', '2', '4')""", false),
            ("routing on a key the filter pins", "kinds", $"""SELECT c."id" FROM kinds AS c WHERE {Kind} = 'b' AND {Label} = '30'""", false),
            ("routing on a key the declaration leaves one value", "kinds", $"""SELECT c."id" FROM kinds AS c WHERE {Kind} <> 'b' AND {Kind} <> 'c'""", false),
            ("routing on text only a string renders as", "typed", $"""SELECT c."id" FROM typed AS c WHERE {Category} = 'a'""", false),
            ("routing on text a number renders as", "typed", $"""SELECT c."id" FROM typed AS c WHERE {Category} = '30'""", false),
            ("a point read on text a number renders as", "typed", $"""SELECT c."id" FROM typed AS c WHERE {Category} = '30' AND c."id" = '8'""", false),
            ("a point read on text only a string renders as", "typed", $"""SELECT c."id" FROM typed AS c WHERE {Category} = 'a' AND c."id" = '1'""", false),
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

            var ordering = new CosmosContainerMetadata("ordering", new[] { "/category" })
                .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(OrderingSchema)));

            foreach (var (name, container, sql, _) in ClauseProbes)
            {
                output.Append("## clauses ").AppendLine(name);
                try
                {
                    output.AppendLine(Describe(Plan(sql, container == "kinds" ? kinds : ordering)));
                }
                catch (Exception e)
                {
                    output.AppendLine("  fails: " + e.GetType().Name + ": " + e.Message);
                }
            }

            var typed = new CosmosContainerMetadata("typed", new[] { "/category" });

            foreach (var (name, container, sql, _) in RoutingProbes)
            {
                output.Append("## routing ").AppendLine(name);
                try
                {
                    output.AppendLine(Describe(Plan(sql, container == "kinds" ? kinds : typed)));
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
        internal static string Describe(RelNode planned)
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

                    // Where it is sent, which changes no row and is what a key is for: the partition, a point read's id,
                    // a batch's ids. Each value with its type, a stored 30 and a stored "30" being two partitions.
                    if (query.PartitionKeyValues is { } key)
                        text.Append("  routes: ").Append(string.Join(", ", key.Select(v => v is null ? "null" : v.GetType().Name + " " + v)))
                            .AppendLine(query.PartitionKeyIsComplete ? "" : " (a prefix)");
                    if (query.PointReadId is { } id)
                        text.Append("  reads: ").AppendLine(id);
                    if (query.PointReadIds is { Count: > 0 } ids)
                        text.Append("  reads many: ").AppendLine(string.Join(", ", ids));
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
        /// Plans a statement over its containers to the CLR convention, every container's Cosmos rules beside the way
        /// out, as a host registering several does.
        /// </summary>
        internal static RelNode Plan(string sql, params CosmosContainerMetadata[] containers)
        {
            var typeFactory = new JavaTypeFactoryImpl();
            var tables = containers.Select(container => new CosmosTable(container)).ToArray();

            var rootSchema = CalciteSchema.createRootSchema(false);
            foreach (var table in tables)
                rootSchema.add(table.Container.Name, table);

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

            foreach (var table in tables)
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
