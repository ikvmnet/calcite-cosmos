using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd.Corpus;

using Apache.Calcite.Data;

using org.apache.calcite.rel;
using org.apache.calcite.schema.impl;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning
{

    /// <summary>
    /// How long planning takes, in seconds: the planner corpus and the self-join hierarchy, timed. Explicit, and
    /// printed rather than asserted — a clock is the machine's — so that a change to what planning reads can be put
    /// beside the commit before it, run for run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The corpus</b> through <see cref="PlannerHarness"/>, whose containers persist across statements as a deployed
    /// host's do: each statement planned, and the Cosmos part implemented where it pushes, once new and then five times
    /// more. The first is what a statement costs that nothing has seen; the median of the rest is a statement planned
    /// again.
    /// </para>
    /// <para>
    /// <b>The hierarchy</b> through a connection, as <see cref="CosmosPlanningTests"/> plans it: once over containers
    /// built for the plan, which compile their declarations again; and over one connection kept open, whose containers
    /// a host would keep.
    /// </para>
    /// <para>
    /// Run it with <c>-explicit only -showLiveOutput -class Apache.Calcite.Cosmos.Adapter.Tests.Planning.CosmosPlanningTimeTests</c>:
    /// the figures are the test's output, which the runner shows live and otherwise only for a failure.
    /// </para>
    /// </remarks>
    public class CosmosPlanningTimeTests
    {

        const int Rounds = 5;

        const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

        const string MapLinks = """
            SELECT "l"."Id", "l"."Label"
            FROM "V"."Link" AS "l"
            LEFT JOIN (
                SELECT "l0"."Id", CASE
                    WHEN "s"."Id" IS NOT NULL THEN CAST('SpotLinkBody' AS VARCHAR)
                    WHEN "p"."Id" IS NOT NULL THEN CAST('ParkLinkBody' AS VARCHAR)
                    WHEN "m"."Id" IS NOT NULL THEN CAST('MapLinkBody' AS VARCHAR)
                END AS "Discriminator"
                FROM "V"."LinkBody" AS "l0"
                LEFT JOIN "V"."MapLinkBody" AS "m" ON "l0"."Id" = "m"."Id"
                LEFT JOIN "V"."ParkLinkBody" AS "p" ON "l0"."Id" = "p"."Id"
                LEFT JOIN "V"."SpotLinkBody" AS "s" ON "l0"."Id" = "s"."Id"
            ) AS "s0" ON "l"."Id" = "s0"."Id"
            WHERE "s0"."Discriminator" = CAST('MapLinkBody' AS VARCHAR)
            """;

        const string MapLinksByMapName = """
            SELECT "l"."Id", "l"."ChangeUtcTime", "l"."IntId", "l"."Label", "l"."Offline"
            FROM "V"."Link" AS "l"
            LEFT JOIN (
                SELECT "l0"."Id", "m"."MapId", CASE
                    WHEN "s"."Id" IS NOT NULL THEN CAST('SpotLinkBody' AS VARCHAR)
                    WHEN "p"."Id" IS NOT NULL THEN CAST('ParkLinkBody' AS VARCHAR)
                    WHEN "m"."Id" IS NOT NULL THEN CAST('MapLinkBody' AS VARCHAR)
                END AS "Discriminator"
                FROM "V"."LinkBody" AS "l0"
                LEFT JOIN "V"."MapLinkBody" AS "m" ON "l0"."Id" = "m"."Id"
                LEFT JOIN "V"."ParkLinkBody" AS "p" ON "l0"."Id" = "p"."Id"
                LEFT JOIN "V"."SpotLinkBody" AS "s" ON "l0"."Id" = "s"."Id"
            ) AS "s0" ON "l"."Id" = "s0"."Id"
            LEFT JOIN "V"."Map" AS "m0" ON "s0"."MapId" = "m0"."Id"
            WHERE "s0"."Discriminator" = CAST('MapLinkBody' AS VARCHAR)
            ORDER BY "m0"."Name" NULLS FIRST, "l"."Label" NULLS FIRST
            FETCH FIRST ? ROWS ONLY
            """;

        [Fact(Explicit = true)]
        public void HowLongTheCorpusTakesToPlan()
        {
            var harness = new PlannerHarness();
            _ = harness.PlanToAsync("""SELECT c."id" FROM products AS c WHERE c."$.category" = 'x'""");

            var first = new List<double>();
            var again = new List<double>();
            var named = new List<(string Name, double First)>();

            foreach (var query in PlannerQueries.All)
            {
                double Once()
                {
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        var planned = harness.PlanToAsync(query.Sql);
                        Implement(planned);
                    }
                    catch (Exception)
                    {
                        // A statement that does not plan is timed to its failure, alike on both sides.
                    }

                    return clock.Elapsed.TotalMilliseconds;
                }

                first.Add(Once());
                again.Add(Enumerable.Range(0, Rounds).Select(_ => Once()).OrderBy(t => t).ElementAt(Rounds / 2));
                named.Add((query.Name, first[^1]));
            }

            Report("corpus, each statement new", first);
            Report("corpus, each statement again (median of 5)", again);

            // Where a change's cost lands: a total moved by one statement reads differently from one moved by all.
            foreach (var (name, time) in named.OrderByDescending(n => n.First).Take(10))
                TestContext.Current.TestOutputHelper?.WriteLine($"  new: {time,9:N1} ms  {name}");
        }

        [Fact(Explicit = true)]
        public void HowLongTheHierarchyTakesToPlan()
        {
            foreach (var (name, sql, parameters) in new[] { ("MapLinks", MapLinks, Array.Empty<object>()), ("MapLinksByMapName", MapLinksByMapName, new object[] { 10 }) })
            {
                // Warm Calcite's own initialisers apart from the figures.
                using (var warm = Open())
                    _ = Explain(warm, sql, parameters);

                var cold = new List<double>();
                for (var i = 0; i < Rounds; i++)
                {
                    using var connection = Open();
                    cold.Add(Explain(connection, sql, parameters));
                }

                var kept = new List<double>();
                using (var connection = Open())
                    for (var i = 0; i < Rounds + 1; i++)
                        kept.Add(Explain(connection, sql, parameters));

                Report(name + ", containers built for each plan", cold);
                Report(name + ", one connection's containers, first plan", kept.Take(1).ToList());
                Report(name + ", one connection's containers, again", kept.Skip(1).ToList());
            }
        }

        static void Implement(RelNode planned)
        {
            foreach (var cosmos in Cosmos(planned))
                if (PlannerHarness.TableOf(cosmos) is CosmosTable table)
                    _ = PlannerHarness.Implement(cosmos, table.Container);
        }

        static IEnumerable<RelNode> Cosmos(RelNode node)
        {
            if (PlannerHarness.ConventionOf(node) is not null)
            {
                yield return node;
                yield break;
            }

            for (var i = 0; i < node.getInputs().size(); i++)
                foreach (var found in Cosmos((RelNode)node.getInputs().get(i)))
                    yield return found;
        }

        static double Explain(DbConnection connection, string sql, object[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql;
            foreach (var value in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            var clock = Stopwatch.StartNew();
            _ = command.ExecuteScalar();
            return clock.Elapsed.TotalMilliseconds;
        }

        static void Report(string what, IReadOnlyList<double> times)
        {
            var sorted = times.OrderBy(t => t).ToArray();
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{what}: {sorted.Sum() / 1000:N3} s in all over {sorted.Length}; p50 {sorted[sorted.Length / 2]:N1} ms, p90 {sorted[Math.Min(sorted.Length - 1, sorted.Length * 9 / 10)]:N1} ms, max {sorted[^1]:N1} ms");
        }

        static DbConnection Open()
        {
            var connectionString = new CalciteConnectionStringBuilder { CaseSensitive = true }.ConnectionString + ";conformance=LENIENT";

            var connection = new CalciteDataSourceBuilder(connectionString)
                .ConfigureRootSchema(root =>
                {
                    Apache.Calcite.Geography.Schema.GeographySchema.AddTo(root);

                    var cosmos = root.add("COSMOS", new AbstractSchema());
                    cosmos.add("links", new CosmosTable(new CosmosContainerMetadata("links", new[] { "/linkId" })
                        .WithFacts(CosmosSchemaRecognition.ReadFrom(CosmosSelfJoinTests.Schema()))
                        .WithConstraints(new[] { CosmosConstraint.Parse("UNIQUE (JSON_VALUE(DOC, '$.data.guid'))", CosmosConstraintSource.Declared) })));
                    cosmos.add("maps", new CosmosTable(new CosmosContainerMetadata("maps", new[] { "/guid" })
                        .WithFacts(CosmosSchemaRecognition.ReadFrom(new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["guid"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                                ["name"] = new JsonObject { ["type"] = "string" },
                            },
                        }))));

                    var views = root.add("V", new AbstractSchema());
                    foreach (var (name, sql) in CosmosSelfJoinTests.Views("links").Append(("Map", """
                        SELECT CAST(JSON_VALUE(m."DOC", '$.guid' RETURNING VARCHAR) AS UUID) AS "Id",
                               JSON_VALUE(m."DOC", '$.name' RETURNING VARCHAR) AS "Name"
                        FROM "COSMOS"."maps" AS m
                        """)))
                        views.add(name, ViewTable.viewMacro(views, sql, java.util.Collections.emptyList(), java.util.Arrays.asList("V", name), java.lang.Boolean.FALSE));
                })
                .Build()
                .CreateConnection();

            connection.Open();
            return connection;
        }

    }

}
