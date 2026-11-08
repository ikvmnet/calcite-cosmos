using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingRexBuilderTests;

namespace Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd
{

    public partial class CosmosDifferentialTests
    {

        // ── A CASE over each partition predicate, and the window's base planted (spec h13) ──────────────────────────────

        /// <summary>
        /// Line 9c: over each predicate the partitions ask, <c>CASE WHEN φ THEN 'T' WHEN NOT φ THEN 'F' ELSE 'U' END</c>
        /// projected and pushed labels every row as Calcite's φ is true, false or unknown there: a CASE whose arms the
        /// service took by another truth than Calcite's shows as a row in the wrong partition. The count of statements
        /// sending the ternary is recorded, never held to, the projection's push being Volcano's.
        /// </summary>
        [Fact]
        public async Task EveryCaseTakesTheArmItsConditionChooses()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var predicates = PartitionedPredicatesOf(PartitionedPredicates)
                .DistinctBy(p => p.Vocabulary.Table + " " + p.Predicate.Sql).ToList();
            var results = new (string? Failure, bool Judged, bool Ternary)[predicates.Count];

            await Parallel.ForEachAsync(Enumerable.Range(0, predicates.Count), new ParallelOptions { MaxDegreeOfParallelism = PartitionParallelism }, async (i, _) =>
            {
                var (v, predicate) = predicates[i];
                var oracle = await Attempt($"SELECT c.\"id\", {predicate.Sql} FROM {v.Table} AS c", pushdown: false);
                if (oracle.Raised is not null)
                {
                    results[i] = (null, false, false);
                    return;
                }

                // Each oracle row is ("id", true|false|null): the label the CASE must give it.
                var expected = oracle.Rows!.Select(row => Regex.Match(row, "^\\((\"[^\"]*\"), (true|false|null)\\)$"))
                    .Select(m => m.Success ? $"({m.Groups[1].Value}, \"{(m.Groups[2].Value == "true" ? "T" : m.Groups[2].Value == "false" ? "F" : "U")}\")"
                        : throw new InvalidOperationException("The oracle answered a row that is not an id and a truth value: " + m.Value))
                    .ToList();

                var sql = $"SELECT c.\"id\", CASE WHEN {predicate.Sql} THEN 'T' WHEN NOT ({predicate.Sql}) THEN 'F' ELSE 'U' END FROM {v.Table} AS c";
                var sent = new List<string>();
                var pushed = await Attempt(sql, pushdown: true, sent);
                var failure = Refines(sql, false, new Outcome(expected, null), pushed);
                results[i] = (failure is not null ? $"over {v.Table}: {failure}\n  sent: {string.Join(" || ", sent)}" : null, true, sent.Any(s => s.Contains(" ? ", StringComparison.Ordinal)));
            });

            var summary = $"{predicates.Count} distinct predicates, {results.Count(r => r.Judged == false)} not judged because Calcite raised, "
                + $"{results.Count(r => r.Ternary)} sending the ternary, {results.Count(r => r.Failure is not null)} failing";
            TestContext.Current.TestOutputHelper?.WriteLine(summary);
            Log("## EveryCaseTakesTheArmItsConditionChooses: " + summary);
            results.Select(r => r.Failure).OfType<string>().Should().BeEmpty($"each row takes the arm Calcite's condition chooses ({summary})");
        }

        /// <summary>
        /// The documents of <c>planted_text</c> whose <c>t</c>, ASCII without U+0000 and so pushed under either set (D8), has
        /// <c>b</c> second: <c>x2</c>'s <c>ab</c> alone, where counting from 0 keeps <c>x8</c>'s <c>a0b</c>.
        /// </summary>
        const string WindowStatement = "SELECT c.\"id\" FROM planted_text AS c WHERE SUBSTRING(JSON_VALUE(c.\"DOC\", '$.t') FROM 2 FOR 1) = 'b'";

        /// <summary>
        /// M30 (line 17), a window's start sent as the service's base: the differential parts on the statement the
        /// translator sends, and the place form S's printer writes, run as C6 runs a form, keeps other rows than Calcite's.
        /// Unplanted, both agree with Calcite and both send the window.
        /// </summary>
        [Fact]
        public async Task APlantedWindowBaseIsCaught()
        {
            if (_container is null)
                Assert.Skip("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var containers = await PlantedContainers.Value;
            var oracle = await Attempt(WindowStatement, pushdown: false, containers: containers);
            oracle.Rows.Should().Equal(["\"x2\""], "Calcite counts from 1");
            var text = containers.Single(c => c.Metadata.Name == PlantedText.Name).Container;

            async Task<(string? Parted, IReadOnlyList<string> Sent, IReadOnlyList<string> Printed)> Ask()
            {
                var sent = new List<string>();
                var parted = Refines(WindowStatement, false, oracle, await Attempt(WindowStatement, pushdown: true, sent, containers: containers));
                return (parted, sent, await PrintedWindowKeeps(text));
            }

            var unplanted = await Ask();
            (string? Parted, IReadOnlyList<string> Sent, IReadOnlyList<string> Printed) planted;
            using (RenderingPrinterFaults.Plant(RenderingPrinterFault.M30))
                planted = await Ask();

            unplanted.Parted.Should().BeNull();
            unplanted.Sent.Should().Contain(s => s.Contains("SUBSTRING(", StringComparison.Ordinal), "the window is pushed, so the plant has something to move");
            unplanted.Printed.Should().Equal(["x2"]);
            planted.Parted.Should().NotBeNull("the differential catches the shifted base");
            planted.Printed.Should().NotEqual(["x2"], "the printed place, run as C6 runs it, keeps another row");
        }

        /// <summary>
        /// The ids the service keeps under the window's place form as S's printer writes it: the loader's read of the
        /// window, its place compared with <c>b</c>, printed with the place's call and run as <c>WHERE</c>.
        /// </summary>
        static async Task<IReadOnlyList<string>> PrintedWindowKeeps(Container container)
        {
            var t = JsonValue("t", SqlTypeName.VARCHAR);
            var window = Rex.makeCall(SqlStdOperatorTable.EQUALS,
                Rex.makeCall(SqlStdOperatorTable.SUBSTRING, t, Rex.makeExactLiteral(new java.math.BigDecimal(2)), Rex.makeExactLiteral(new java.math.BigDecimal(1))),
                Rex.makeLiteral("b"));
            var read = new CosmosPredicateLoader.Loading(Fields, Readings, CosmosImplementor.DefaultRootAlias, places: true).Read(window);
            var place = read.Computed.Last();
            var parameters = new CosmosParameterList();
            var text = RenderingPrinter.Print(ServiceForm.CompareOf(CosmosServiceTest.Equal, place.Spelled!, JsonValues.Of("b")), read.Places.ToDictionary(SqlValue.Spell), "c", parameters,
                computed: new Dictionary<string, SqlValue.Call> { [place.Spelled!] = place }, spellings: Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ChosenSpellings.Of(read, RenderingSet.Emulator));

            var query = parameters.Parameters.Aggregate(new QueryDefinition($"SELECT VALUE c.id FROM c WHERE {text}"), (q, p) => q.WithParameter(p.Name, p.Value));
            var ids = new List<string>();
            using var feed = container.GetItemQueryStreamIterator(query);
            while (feed.HasMoreResults)
            {
                using var response = (await feed.ReadNextAsync()).EnsureSuccessStatusCode();
                using var json = await JsonDocument.ParseAsync(response.Content);
                ids.AddRange(json.RootElement.GetProperty("Documents").EnumerateArray().Select(e => e.GetString()!));
            }

            return [.. ids.OrderBy(i => i, StringComparer.Ordinal)];
        }

    }

}
