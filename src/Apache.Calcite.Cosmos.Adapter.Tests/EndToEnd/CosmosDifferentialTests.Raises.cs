using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    public partial class CosmosDifferentialTests
    {

        // ── What Calcite fails on fails pushed too ──────────────────────────────────────────────────────────────────────

        /// <summary><c>kind</c> is never <c>C</c> or <c>Z</c>, <c>body</c> is any string, and <c>n</c> is 1 or 2.</summary>
        const string RaisesSchema = """{ "type": "object", "required": ["kind", "body", "n"], "properties": { "kind": { "enum": ["A", "B"] }, "body": { "type": "string" }, "n": { "enum": [1, 2] } } }""";

        static readonly CosmosContainerMetadata RaisesListed = new CosmosContainerMetadata("raises_listed", ["/category"])
            .WithFacts(CosmosSchemaRecognition.ReadFrom(System.Text.Json.Nodes.JsonNode.Parse(RaisesSchema)!));

        /// <summary>One <c>body</c> that is no number, which a cast to <c>INTEGER</c> fails on, beside two that are.</summary>
        static readonly string[] RaisesDocuments =
        [
            """{ "id": "r1", "category": "a", "kind": "A", "body": "abc", "n": 1 }""",
            """{ "id": "r2", "category": "a", "kind": "B", "body": "1", "n": 2 }""",
            """{ "id": "r3", "category": "b", "kind": "A", "body": "1", "n": 2 }""",
        ];

        static readonly Lazy<Task<IReadOnlyList<(CosmosContainerMetadata Metadata, Container Container)>>> RaisesContainers = new(() => SeededOnce(async (database, token) =>
        [
            (RaisesListed, await Seed(database, RaisesListed.Name, RaisesDocuments, token)),
        ]));

        /// <summary>
        /// Each statement casts the text <c>body</c> to <c>INTEGER</c> where Calcite evaluates it on every document, so
        /// Calcite fails on <c>"abc"</c>, and beside it something the declaration contradicts: a projected <c>CASE</c>'s
        /// conditions compared with the cast above (the plan holds the <c>CASE</c> as written, and only the text sent
        /// answers them), a filter over the filter that casts, a window ordered or summed by the cast under a contradicted
        /// filter, and a projected deletion beside a never-read arm. Each fails pushed too, never answering no rows, and the
        /// controls, where nothing raises or where Calcite's simplifier reads a stack of filters over the cast as
        /// <c>FALSE</c> before evaluating any of it, answer as Calcite does. A Calcite error refines to anything in
        /// <see cref="Refines"/>, so this asks the failure itself. Its container is its own, outside the corpus.
        /// </summary>
        [Fact]
        public async Task WhatCalciteFailsOnFailsPushedToo()
        {
            if (_client is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var containers = await RaisesContainers.Value;
            const string Kind = "JSON_VALUE(c.\"DOC\", '$.kind')";
            const string Body = "CAST(JSON_VALUE(c.\"DOC\", '$.body') AS INTEGER)";
            const string N = "CAST(JSON_VALUE(c.\"DOC\", '$.n') AS INTEGER)";
            var never = $"CASE WHEN {Kind} = 'C' THEN {N} = 1 ELSE FALSE END";
            var arms = $"SELECT c.\"id\", CASE WHEN {Kind} = 'C' THEN 1 WHEN {Kind} = 'Z' THEN 2 END AS v, {Body} AS w FROM raises_listed AS c";

            var failing = new[]
            {
                $"SELECT x.\"id\" FROM ({arms}) AS x WHERE x.w = x.v",
                $"SELECT COUNT(*) AS n FROM ({arms}) AS x WHERE x.w = x.v",
                $"SELECT x.\"id\" FROM (SELECT c.\"id\", CASE WHEN {Kind} = 'C' THEN 1 WHEN {Kind} = 'Z' THEN 2 ELSE NULL END AS v, {Body} AS w FROM raises_listed AS c) AS x WHERE x.w > x.v",
                $"SELECT x.\"id\" FROM (SELECT c.\"id\", CASE WHEN {Kind} = 'C' THEN 1 END AS v, {Body} AS w FROM raises_listed AS c) AS x WHERE x.v IS NULL AND x.w = 1",
                $"SELECT x.\"id\" FROM (SELECT c.\"id\", c.\"DOC\" FROM raises_listed AS c WHERE {Body} = 1) AS x WHERE JSON_VALUE(x.\"DOC\", '$.kind') = 'C'",
                $"SELECT COUNT(*) AS n FROM (SELECT c.\"id\", c.\"DOC\" FROM raises_listed AS c WHERE {Body} = 1) AS x WHERE JSON_VALUE(x.\"DOC\", '$.kind') = 'C'",
                $"SELECT x.\"id\", x.r FROM (SELECT c.\"id\", {Kind} AS k, ROW_NUMBER() OVER (ORDER BY {Body}) AS r FROM raises_listed AS c) AS x WHERE x.k = 'C'",
                $"SELECT x.\"id\", x.s FROM (SELECT c.\"id\", {Kind} AS k, SUM({Body}) OVER () AS s FROM raises_listed AS c) AS x WHERE x.k = 'C'",
                $"SELECT c.\"id\", CASE WHEN NOT ({Body} = 1 AND {Kind} = 'C') AND {never} THEN 1 ELSE 0 END AS v FROM raises_listed AS c",
            };

            var answering = new[]
            {
                $"SELECT c.\"id\", CASE WHEN {Kind} = 'C' THEN 2 ELSE 1 END AS v FROM raises_listed AS c",
                $"SELECT x.\"id\" FROM (SELECT c.\"id\", CASE WHEN {Kind} = 'C' THEN 1 WHEN {Kind} = 'Z' THEN 2 END AS v, {N} AS w FROM raises_listed AS c) AS x WHERE x.w = x.v",
                $"SELECT x.\"id\" FROM (SELECT c.\"id\", c.\"DOC\" FROM raises_listed AS c WHERE JSON_VALUE(c.\"DOC\", '$.body') = '1') AS x WHERE JSON_VALUE(x.\"DOC\", '$.kind') = 'C'",
                $"SELECT c.\"id\", CASE WHEN {Kind} = 'A' AND {never} THEN 1 ELSE 0 END AS v FROM raises_listed AS c",
                $"SELECT y.\"id\" FROM (SELECT x.\"id\", x.\"DOC\" FROM (SELECT c.\"id\", c.\"DOC\" FROM raises_listed AS c WHERE {Body} = 1) AS x WHERE JSON_VALUE(x.\"DOC\", '$.kind') IN ('A', 'B')) AS y WHERE JSON_VALUE(y.\"DOC\", '$.kind') = 'C'",
                $"SELECT COUNT(*) AS n FROM (SELECT x.\"id\", x.\"DOC\" FROM (SELECT c.\"id\", c.\"DOC\" FROM raises_listed AS c WHERE {Body} = 1) AS x WHERE JSON_VALUE(x.\"DOC\", '$.kind') IN ('A', 'B')) AS y WHERE JSON_VALUE(y.\"DOC\", '$.kind') = 'C'",
            };

            var wrong = new List<string>();
            foreach (var sql in failing)
            {
                var oracle = await Attempt(sql, pushdown: false, containers: containers);
                var sent = new List<string>();
                var pushed = await Attempt(sql, pushdown: true, sent, containers: containers);
                if (oracle.Raised is null)
                    wrong.Add($"{sql}\n  Calcite answered [{string.Join("; ", oracle.Rows!)}], so this asks nothing");
                else if (pushed.Raised is null)
                    wrong.Add($"{sql}\n  pushed: [{string.Join("; ", pushed.Rows!)}] where Calcite fails: {oracle.Raised.Message.Split('\n')[0]}\n  sent: {string.Join(" || ", sent)}");
            }

            foreach (var sql in answering)
            {
                var oracle = await Attempt(sql, pushdown: false, containers: containers);
                if (oracle.Raised is Exception e)
                {
                    wrong.Add($"{sql}\n  Calcite failed, so this compares nothing: {e.Message.Split('\n')[0]}");
                    continue;
                }

                var sent = new List<string>();
                if (Refines(sql, false, oracle, await Attempt(sql, pushdown: true, sent, containers: containers)) is string failure)
                    wrong.Add($"{failure}\n  sent: {string.Join(" || ", sent)}");
            }

            wrong.Should().BeEmpty();
        }

    }

}
