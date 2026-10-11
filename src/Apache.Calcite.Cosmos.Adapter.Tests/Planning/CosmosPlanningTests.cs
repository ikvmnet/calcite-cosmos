using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;

using Apache.Calcite.Data;

using FluentAssertions;

using org.apache.calcite.plan;
using org.apache.calcite.schema.impl;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning
{

    /// <summary>
    /// How much planning a federation's queries takes, through the host's own prepare and Calcite's own
    /// rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No service: the containers are built from metadata and the views registered on the root, so the plan
    /// is the one a connection over a model would choose, and nothing is read. The views and the schema are
    /// <see cref="CosmosSelfJoinTests"/>'s, where the rows are compared.
    /// </para>
    /// <para>
    /// <b>What is measured is the number of expressions the planner registers, not the time it takes.</b>
    /// The count is what the time follows — each registration is costed and matched against every rule —
    /// and unlike a clock it is the same on every machine and every run. A self-join merge that the planner
    /// explored before it fired registered seven times what these do, and took a hundred and ninety seconds
    /// to plan the first of them (#201).
    /// </para>
    /// </remarks>
    public class CosmosPlanningTests
    {

        const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

        static CosmosContainerMetadata Links(CosmosContainerStatistics? statistics = null) =>
            new CosmosContainerMetadata("links", new[] { "/linkId" })
                .WithFacts(CosmosSchemaRecognition.ReadFrom(CosmosSelfJoinTests.Schema()))
                .WithConstraints(new[] { CosmosConstraint.Parse("UNIQUE (JSON_VALUE(DOC, '$.data.guid'))", CosmosConstraintSource.Declared) })
                .WithStatistics(statistics);

        /// <summary>
        /// A second container, of maps, which a link names by key.
        /// </summary>
        static CosmosContainerMetadata Maps() =>
            new CosmosContainerMetadata("maps", new[] { "/guid" })
                .WithFacts(CosmosSchemaRecognition.ReadFrom(new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["guid"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                        ["name"] = new JsonObject { ["type"] = "string" },
                    },
                }));

        static IEnumerable<(string Name, string Sql)> Views()
        {
            foreach (var view in CosmosSelfJoinTests.Views("links"))
                yield return view;

            yield return ("Map", """
                SELECT CAST(JSON_VALUE(m."DOC", '$.guid' RETURNING VARCHAR) AS UUID) AS "Id",
                       JSON_VALUE(m."DOC", '$.name' RETURNING VARCHAR) AS "Name"
                FROM "COSMOS"."maps" AS m
                """);
        }

        /// <summary>
        /// Opens a connection whose root holds the containers and, in <c>V</c>, the views over them.
        /// </summary>
        static DbConnection Open(CosmosContainerStatistics? statistics = null)
        {
            var connectionString = new CalciteConnectionStringBuilder { CaseSensitive = true }.ConnectionString + ";conformance=LENIENT";

            var connection = new CalciteDataSourceBuilder(connectionString)
                .ConfigureRootSchema(root =>
                {
                    Apache.Calcite.Geography.Schema.GeographySchema.AddTo(root);

                    var cosmos = root.add("COSMOS", new AbstractSchema());
                    cosmos.add("links", new CosmosTable(Links(statistics)));
                    cosmos.add("maps", new CosmosTable(Maps()));

                    var views = root.add("V", new AbstractSchema());
                    foreach (var (name, sql) in Views())
                        views.add(name, ViewTable.viewMacro(views, sql, java.util.Collections.emptyList(), java.util.Arrays.asList("V", name), java.lang.Boolean.FALSE));
                })
                .Build()
                .CreateConnection();

            connection.Open();
            return connection;
        }

        /// <summary>
        /// Plans a statement, and counts the expressions the planner registered doing it.
        /// </summary>
        static (string Plan, int Registered) Explain(string sql, params object[] parameters) =>
            Explain(null, sql, parameters);

        /// <summary>
        /// Plans a statement over a links container of the given size, and counts the expressions the
        /// planner registered doing it.
        /// </summary>
        static (string Plan, int Registered) Explain(CosmosContainerStatistics? statistics, string sql, params object[] parameters)
        {
            using var connection = Open(statistics);
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql;

            foreach (var value in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            var counter = new Counter();
            using var hook = org.apache.calcite.runtime.Hook.PLANNER.addThread(new Listen(counter));

            var plan = ((string?)command.ExecuteScalar() ?? "").Replace("\r\n", "\n");
            return (plan, counter.Registered);
        }

        sealed class Listen : java.util.function.Consumer
        {

            readonly Counter _counter;

            public Listen(Counter counter) => _counter = counter;

            public void accept(object o)
            {
                if (o is RelOptPlanner planner)
                    planner.addListener(_counter);
            }

            public java.util.function.Consumer andThen(java.util.function.Consumer after) => throw new NotSupportedException();

        }

        sealed class Counter : RelOptListener
        {

            public int Registered { get; private set; }

            public void relEquivalenceFound(RelOptListener.RelEquivalenceEvent e)
            {
                if (e.isPhysical() == false)
                    Registered++;
            }

            public void ruleAttempted(RelOptListener.RuleAttemptedEvent e) { }

            public void ruleProductionSucceeded(RelOptListener.RuleProductionEvent e) { }

            public void relDiscarded(RelOptListener.RelDiscardedEvent e) { }

            public void relChosen(RelOptListener.RelChosenEvent e) { }

        }

        static int Scans(string plan, string container) => Regex.Matches(plan, $@"CosmosTableScan\(table=\[\[COSMOS, {container}\]\]\)").Count;

        /// <summary>
        /// The links of one kind, as Entity Framework writes it over a table-per-type hierarchy: the link
        /// joined to its body, the body to each subtype, and the subtype told by which one matched.
        /// </summary>
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

        /// <summary>
        /// The same, ordered by the name of the map each link is on — a join to a second container — and
        /// paged: the query #201 measured at six and a half minutes.
        /// </summary>
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

        /// <summary>
        /// Five views of one container merge into one read before the planner explores the joins between
        /// them. #201.
        /// </summary>
        /// <remarks>
        /// The merge was an ordinary rule, queued behind every other rule the joins matched, and the
        /// planner explored each join — and every spelling of every filter pushed into one — before the
        /// merge removed it: 8,250 expressions for this query, and three minutes to plan it. A substitution
        /// rule fires first and prunes the join it replaces.
        /// </remarks>
        [Fact]
        public void AHierarchyMergesBeforeItsJoinsAreExplored()
        {
            var (plan, registered) = Explain(MapLinks);

            Scans(plan, "links").Should().Be(1, "the five views are one read:\n" + plan);
            plan.Should().NotContain("Join", "and there is nothing left to join:\n" + plan);
            registered.Should().BeLessThan(2000, "the merge comes before the joins are explored, which registered 8,250");
        }

        /// <summary>
        /// A join to a second container leaves the hierarchy merged and the second container looked up by
        /// key. #201.
        /// </summary>
        [Fact]
        public void AHierarchyJoinedToAnotherContainerStillMergesFirst()
        {
            var (plan, registered) = Explain(MapLinksByMapName, 10);

            Scans(plan, "links").Should().Be(1, "the five views are one read:\n" + plan);
            Scans(plan, "maps").Should().Be(1, plan);
            plan.Should().Contain("CosmosLookupJoin", "the maps are fetched by the keys the links name:\n" + plan);
            registered.Should().BeLessThan(2000, "the merge comes before the joins are explored, which registered 8,397");
        }

        /// <summary>
        /// A page of links with their bodies, the spot's location among them, paged by parameters as an
        /// ORM pages.
        /// </summary>
        const string PageOfLinksWithLocations = """
            SELECT "l1"."Id", "l1"."Label", "s0"."ParkId", "s0"."MapId", "s0"."Location"
            FROM (
                SELECT "l"."Id", "l"."Label"
                FROM "V"."Link" AS "l"
                ORDER BY "l"."Label" NULLS FIRST
                OFFSET ? ROWS FETCH NEXT ? ROWS ONLY
            ) AS "l1"
            LEFT JOIN (
                SELECT "l0"."Id", "p"."ParkId", "m"."MapId", "s"."Location"
                FROM "V"."LinkBody" AS "l0"
                LEFT JOIN "V"."ParkLinkBody" AS "p" ON "l0"."Id" = "p"."Id"
                LEFT JOIN "V"."MapLinkBody" AS "m" ON "l0"."Id" = "m"."Id"
                LEFT JOIN "V"."SpotLinkBody" AS "s" ON "l0"."Id" = "s"."Id"
            ) AS "s0" ON "l1"."Id" = "s0"."Id"
            ORDER BY "l1"."Label" NULLS FIRST
            """;

        /// <summary>
        /// A container that reports its size still merges its hierarchy, and a page of it is taken at the
        /// service.
        /// </summary>
        /// <remarks>
        /// Statistics attached after the container's keys used to drop the keys, so with any document count
        /// the self-join merge had no unique key to read and every view was a read of its own. Without a
        /// count, as on the emulator, the planner has no sense of scale and pages in process, which is why
        /// this is planned against one.
        /// </remarks>
        [Fact]
        public void APageBoundByParametersIsTakenAtTheService()
        {
            var (plan, _) = Explain(new CosmosContainerStatistics(10_000, 20_000_000, 1), PageOfLinksWithLocations, 0, 25);

            Scans(plan, "links").Should().Be(1, "the page and its bodies are one read:\n" + plan);
            plan.Should().MatchRegex(@"CosmosSort\([^\n]*fetch=", "the page is taken at the service:\n" + plan);
            plan.Should().NotContain("ClrCursorSort", "and not again in process:\n" + plan);
        }

    }

}
