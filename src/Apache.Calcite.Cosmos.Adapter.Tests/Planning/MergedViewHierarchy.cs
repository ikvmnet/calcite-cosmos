using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json.Nodes;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Data;

using org.apache.calcite.schema.impl;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning
{

    /// <summary>
    /// A neutral merged-view hierarchy (spec §6.4, <c>TheHierarchyIsWithinItsLine</c>): one container of parts, each a
    /// base row and a body of one of three kinds under a discriminator, read through five views, beside a container of
    /// catalogs. Its two statements join the base to the bodies merged into one discriminated row, as an
    /// object-relational mapper writes table-per-type inheritance, and plan about two dozen <c>WHERE</c> statements of two
    /// or three conjuncts, with the components repeated.
    /// </summary>
    internal static class MergedViewHierarchy
    {

        const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

        const string Milliseconds = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$";

        /// <summary>The bodies merged into one row a part, discriminated by which body holds it, with columns given beside.</summary>
        static string Merged(string columns) => $"""
            LEFT JOIN (
                SELECT "b0"."Id", {columns}CASE
                    WHEN "s"."Id" IS NOT NULL THEN CAST('SpringPartBody' AS VARCHAR)
                    WHEN "g"."Id" IS NOT NULL THEN CAST('GearPartBody' AS VARCHAR)
                    WHEN "b"."Id" IS NOT NULL THEN CAST('BoltPartBody' AS VARCHAR)
                END AS "Discriminator"
                FROM "V"."PartBody" AS "b0"
                LEFT JOIN "V"."BoltPartBody" AS "b" ON "b0"."Id" = "b"."Id"
                LEFT JOIN "V"."GearPartBody" AS "g" ON "b0"."Id" = "g"."Id"
                LEFT JOIN "V"."SpringPartBody" AS "s" ON "b0"."Id" = "s"."Id"
            ) AS "d" ON "p"."Id" = "d"."Id"
            """;

        /// <summary>The parts whose body is a bolt: the base joined to the bodies merged and discriminated, kept where the body is one.</summary>
        public static readonly string BoltParts = $"""
            SELECT "p"."Id", "p"."Label"
            FROM "V"."Part" AS "p"
            {Merged("")}
            WHERE "d"."Discriminator" = CAST('BoltPartBody' AS VARCHAR)
            """;

        /// <summary>The bolts again, each beside its catalog's name, ordered by it and paged.</summary>
        public static readonly string BoltPartsByCatalogName = $"""
            SELECT "p"."Id", "p"."ChangeUtcTime", "p"."IntId", "p"."Label", "p"."Offline"
            FROM "V"."Part" AS "p"
            {Merged("\"b\".\"CatalogId\", ")}
            LEFT JOIN "V"."Catalog" AS "c0" ON "d"."CatalogId" = "c0"."Id"
            WHERE "d"."Discriminator" = CAST('BoltPartBody' AS VARCHAR)
            ORDER BY "c0"."Name" NULLS FIRST, "p"."Label" NULLS FIRST
            FETCH FIRST ? ROWS ONLY
            """;

        /// <summary>The two statements, each with its parameters.</summary>
        public static IReadOnlyList<(string Name, string Sql, object[] Parameters)> Statements { get; } =
            [("BoltParts", BoltParts, []), ("BoltPartsByCatalogName", BoltPartsByCatalogName, [10])];

        /// <summary>
        /// What every document of <c>parts</c> is: a part number and a type, and where it is a part, its body's kind, a
        /// canonical UUID, a label, whether it is offline, when it changed, and the assembly and catalog it belongs to.
        /// </summary>
        static JsonObject Schema() => new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("partId", "type"),
            ["properties"] = new JsonObject
            {
                ["partId"] = new JsonObject { ["type"] = "integer" },
                ["type"] = new JsonObject { ["type"] = "string" },
                ["data"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "integer" },
                        ["guid"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                        ["kind"] = new JsonObject { ["type"] = "string" },
                        ["label"] = new JsonObject { ["type"] = "string" },
                        ["offline"] = new JsonObject { ["type"] = "boolean" },
                        ["metadata"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["changeUtcTime"] = new JsonObject { ["type"] = "string", ["pattern"] = Milliseconds } },
                        },
                        ["data"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["assemblyId"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                                ["catalogId"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                                ["coil"] = new JsonObject { ["type"] = "number" },
                            },
                        },
                    },
                },
            },
        };

        static string Text(string path) => $"""JSON_VALUE(p."DOC", '{path}' RETURNING VARCHAR)""";

        static string Identifier(string path) => $"CAST({Text(path)} AS UUID)";

        const string IsPart = """JSON_VALUE(p."DOC", '$.type' RETURNING VARCHAR) = 'Part'""";

        /// <summary>The views over <c>parts</c> and <c>catalogs</c>: identifiers read as <c>UUID</c> casts of text, each body narrowed by its kind.</summary>
        static IEnumerable<(string Name, string Sql)> Views()
        {
            yield return ("Part", $"""
                SELECT {Identifier("$.data.guid")} AS "Id",
                       CAST({Text("$.data.id")} AS INTEGER) AS "IntId",
                       {Text("$.data.label")} AS "Label",
                       CAST({Text("$.data.offline")} AS BOOLEAN) AS "Offline",
                       CAST({Text("$.data.metadata.changeUtcTime")} AS TIMESTAMP(3) FORMAT 'YYYY-MM-DD''T''HH24:MI:SS.FF3''Z''') AS "ChangeUtcTime"
                FROM "COSMOS"."parts" AS p
                WHERE {IsPart}
                  AND {Text("$.data.kind")} IN ('bolt', 'gear', 'spring')
                """);

            yield return ("PartBody", $"""
                SELECT {Identifier("$.data.guid")} AS "Id"
                FROM "COSMOS"."parts" AS p
                WHERE {IsPart}
                  AND {Text("$.data.kind")} IN ('bolt', 'gear', 'spring')
                """);

            yield return ("BoltPartBody", $"""
                SELECT {Identifier("$.data.guid")} AS "Id",
                       {Identifier("$.data.data.assemblyId")} AS "AssemblyId",
                       {Identifier("$.data.data.catalogId")} AS "CatalogId"
                FROM "COSMOS"."parts" AS p
                WHERE {IsPart}
                  AND {Text("$.data.kind")} = 'bolt'
                """);

            yield return ("GearPartBody", $"""
                SELECT {Identifier("$.data.guid")} AS "Id",
                       {Identifier("$.data.data.assemblyId")} AS "AssemblyId"
                FROM "COSMOS"."parts" AS p
                WHERE {IsPart}
                  AND {Text("$.data.kind")} = 'gear'
                """);

            yield return ("SpringPartBody", $"""
                SELECT {Identifier("$.data.guid")} AS "Id",
                       JSON_VALUE(p."DOC", '$.data.data.coil' RETURNING DOUBLE) AS "Coil"
                FROM "COSMOS"."parts" AS p
                WHERE {IsPart}
                  AND {Text("$.data.kind")} = 'spring'
                """);

            yield return ("Catalog", """
                SELECT CAST(JSON_VALUE(c."DOC", '$.guid' RETURNING VARCHAR) AS UUID) AS "Id",
                       JSON_VALUE(c."DOC", '$.name' RETURNING VARCHAR) AS "Name"
                FROM "COSMOS"."catalogs" AS c
                """);
        }

        /// <summary>
        /// A connection over containers built for it, as a host that keeps none would plan, each reasoning in the harnesses'
        /// shared reasoning (<see cref="TestReasoning.Shared"/>), as they reasoned in the process's graphs.
        /// </summary>
        public static DbConnection Open()
        {
            var connectionString = new CalciteConnectionStringBuilder { CaseSensitive = true }.ConnectionString + ";conformance=LENIENT";
            var connection = new CalciteDataSourceBuilder(connectionString)
                .ConfigureRootSchema(root =>
                {
                    var cosmos = root.add("COSMOS", new AbstractSchema());
                    cosmos.add("parts", new CosmosTable(TestReasoning.Bind(new CosmosContainerMetadata("parts", ["/partId"])
                        .WithFacts(CosmosSchemaRecognition.ReadFrom(Schema()))
                        .WithConstraints([CosmosConstraint.Parse("UNIQUE (JSON_VALUE(DOC, '$.data.guid'))", CosmosConstraintSource.Declared)]))));
                    cosmos.add("catalogs", new CosmosTable(TestReasoning.Bind(new CosmosContainerMetadata("catalogs", ["/guid"])
                        .WithFacts(CosmosSchemaRecognition.ReadFrom(new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["guid"] = new JsonObject { ["type"] = "string", ["pattern"] = Uuid },
                                ["name"] = new JsonObject { ["type"] = "string" },
                            },
                        })))));

                    var views = root.add("V", new AbstractSchema());
                    foreach (var (name, sql) in Views())
                        views.add(name, ViewTable.viewMacro(views, sql, java.util.Collections.emptyList(), java.util.Arrays.asList("V", name), java.lang.Boolean.FALSE));
                })
                .Build()
                .CreateConnection();

            connection.Open();
            return connection;
        }

        /// <summary>Plans a statement through a connection, as <c>EXPLAIN PLAN FOR</c> does, binding its parameters.</summary>
        public static void Explain(DbConnection connection, string sql, IEnumerable<object> parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN PLAN FOR " + sql;
            foreach (var value in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            _ = command.ExecuteScalar();
        }

    }

}
