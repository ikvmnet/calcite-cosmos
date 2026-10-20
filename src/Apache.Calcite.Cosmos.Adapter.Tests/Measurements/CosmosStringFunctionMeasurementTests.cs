using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts.Saturation;
using Apache.Calcite.Data;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the service's string functions compute over characters outside ASCII, measured: the facts the rule for each
    /// pushed string function rests on (<c>CosmosStringFunctions</c>), each a row of <c>CosmosAxioms</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Azure's facts, and the emulator's answers detected.</b> The emulator is a reimplementation, and on these
    /// functions it differs from Azure: its <c>SUBSTRING</c>, <c>LEFT</c>, <c>RIGHT</c> and <c>INDEX_OF</c> count code
    /// points where Azure's count UTF-16 units, its case mapping maps three characters Azure's leaves, and its trim removes
    /// five fewer. Each test asserts what both answer alike, then detects the emulator's answer and skips, rather than
    /// asking which endpoint answered, so that an emulator that starts behaving as Azure does makes it assert. Measured on
    /// the probe account on 2026-10-19 (<c>reports/verify-strings.md</c>; the trim set over the whole basic plane, plane 1
    /// and U+E0000 to U+E01EF).
    /// </para>
    /// <para>
    /// <b>Calcite's side is measured in process</b> (<see cref="CalciteStringFunctionMeasurementTests"/>): it counts
    /// UTF-16 units, maps case by Java's full mapping, and trims spaces alone.
    /// </para>
    /// <para>
    /// One partition and one container in a database of 400 RU/s, deleted when the class ends.
    /// </para>
    /// </remarks>
    public class CosmosStringFunctionMeasurementTests : IClassFixture<CosmosStringFunctionMeasurementTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public ValueTask DisposeAsync() { Cleanup(); return default; }

        }

        static readonly string DatabaseName = "calcite_cosmos_strfn_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        const string Emoji = "\U0001F600";

        /// <summary>
        /// The strings the counts and the case mappings are asked of, by id: a supplementary character between two ASCII
        /// letters; ASCII; and the characters Java's full case mapping and the services' part over.
        /// </summary>
        static readonly (string Id, string Text)[] Strings =
        [
            ("sup", "a" + Emoji + "b"),
            ("ascii", "aBc"),
            ("sharp", "aßb"),
            ("lig", "aﬀb"),
            ("idot", "aİb"),
            ("kelvin", "aKb"),
            ("longs", "aſb"),
        ];

        /// <summary>
        /// The characters a trim is asked of, each first and last in a string of its own: every C0 control and U+0020,
        /// DEL and every C1 control, U+00A0 and the soft hyphen, every Unicode White_Space character, the format
        /// characters a trim might take for space, and U+FFFD.
        /// </summary>
        static readonly int[] TrimCandidates = Enumerable.Range(0, 0x21)
            .Append(0x7F)
            .Concat(Enumerable.Range(0x80, 0x21))
            .Concat([0xAD, 0x1680, 0x180E])
            .Concat(Enumerable.Range(0x2000, 0x10))
            .Concat([0x2028, 0x2029, 0x202F, 0x205F, 0x2060, 0x3000, 0xFEFF, 0xFFFD])
            .ToArray();

        /// <summary>
        /// What Azure's <c>TRIM</c>, <c>LTRIM</c> and <c>RTRIM</c> were measured to remove: written out here, apart from
        /// the theory's table, which the test holds to it.
        /// </summary>
        static readonly int[] AzureTrims =
        [
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x180E,
            0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
            0x2028, 0x2029, 0x202F, 0x205F, 0x3000,
        ];

        static CosmosClient? _client;
        static Database? _database;
        static string? _unavailable;

        /// <summary>
        /// The status the service refused a document with, or <c>null</c> where it stored them all.
        /// </summary>
        static HttpStatusCode? _refused;

        static async Task Initialize()
        {
            // A model names its schema factory by type name, resolved through IKVM, and only finds it if the assembly is
            // loaded.
            _ = new CosmosSchemaFactory();

            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 5 : 30),
            };

            if (CosmosEmulator.IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                if (CosmosEmulator.Endpoint is not string endpoint || CosmosEmulator.Key is not string key)
                {
                    _unavailable = "No account is reachable.";
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CosmosEmulator.IsEmulator ? 10 : 120));
                _client = new CosmosClient(endpoint, key, options);

                try
                {
                    _database = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, 400, cancellationToken: cts.Token)).Database;
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.BadRequest)
                {
                    // A serverless account takes no throughput.
                    _database = (await _client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;
                }

                try { await _database.GetContainer("strings").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                var strings = (await _database.CreateContainerIfNotExistsAsync(new ContainerProperties("strings", "/pk"), cancellationToken: cts.Token)).Container;

                var documents = Strings.Select(s => (s.Id, s.Text))
                    .Concat(TrimCandidates.Select(c => ("t" + c.ToString("X4"), Char(c) + "a" + Char(c))));

                foreach (var (id, text) in documents)
                    if (await Write(strings, id, text, cts.Token) == false)
                        break;
            }
            catch (Exception e)
            {
                _unavailable = e.Message;
                _database = null;
            }
        }

        static string Char(int point) => char.ConvertFromUtf32(point);

        /// <summary>
        /// Writes a document holding a string at <c>s</c>, written raw where JSON allows and escaped where it requires, and
        /// answers whether the service stored it.
        /// </summary>
        static async Task<bool> Write(Container container, string id, string text, CancellationToken cancellation)
        {
            var json = new StringBuilder("{\"id\":\"").Append(id).Append("\",\"pk\":\"a\",\"s\":\"");
            foreach (var unit in text)
            {
                if (unit is '"' or '\\')
                    json.Append('\\').Append(unit);
                else if (unit < 0x20)
                    json.Append("\\u").Append(((int)unit).ToString("x4"));
                else
                    json.Append(unit);
            }

            json.Append("\"}");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToString()));
            using var response = await container.CreateItemStreamAsync(stream, new PartitionKey("a"), cancellationToken: cancellation);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                _refused = response.StatusCode;
                return false;
            }

            response.EnsureSuccessStatusCode();
            return true;
        }

        static void Cleanup()
        {
            try { _database?.DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }
            _client?.Dispose();
            _client = null;
            _database = null;
        }

        static Container Strings_()
        {
            if (_database is null)
                Assert.Skip("These need a service. " + _unavailable);

            if (_refused is HttpStatusCode refused)
                Assert.Skip($"This service refused a document of strings ({(int)refused}), as an emulator may.");

            return _database!.GetContainer("strings");
        }

        /// <summary>
        /// Runs a statement and answers the values it returned, by the id each row names first.
        /// </summary>
        static async Task<Dictionary<string, JsonElement>> Values(Container container, string sql, params (string Name, object Value)[] parameters)
        {
            var query = new QueryDefinition(sql);
            foreach (var (name, value) in parameters)
                query = query.WithParameter(name, value);

            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            using var iterator = container.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("a") });
            while (iterator.HasMoreResults)
            {
                using var response = await iterator.ReadNextAsync(TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();

                using var page = await JsonDocument.ParseAsync(response.Content, cancellationToken: TestContext.Current.CancellationToken);
                foreach (var row in page.RootElement.GetProperty("Documents").EnumerateArray())
                    values[row[0].GetString()!] = row[1].Clone();
            }

            return values;
        }

        static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : "<" + value.ValueKind + ">";

        /// <summary>
        /// The container as a caller names it: <c>s</c> declared a string, which lets a function over it reach the
        /// service where its characters allow, and nothing said of its characters.
        /// </summary>
        static string Model => $$"""
        {
          "version": "1.0",
          "defaultSchema": "COSMOS",
          "schemas": [
            {
              "name": "COSMOS",
              "type": "custom",
              "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
              "operand": {
                "endpoint": "{{CosmosEmulator.Endpoint}}",
                "key": "{{CosmosEmulator.Key}}",
                "database": "{{DatabaseName}}",
                "containers": [ { "name": "strings", "schema": { "type": "object", "properties": { "s": { "type": "string" } } } } ],
                "connectionMode": "gateway"
              }
            }
          ]
        }
        """;

        /// <summary>
        /// Runs a statement through a connection and answers its rows, each column as text and the row's columns joined
        /// by a bar, sorted.
        /// </summary>
        static async Task<List<string>> QueryAsync(string sql)
        {
            await using var connection = new CalciteConnection(new CalciteConnectionStringBuilder
            {
                Model = "inline:" + Model,
                CaseSensitive = true,
            }.ConnectionString);

            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            var rows = new List<string>();

            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i =>
                    reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));

            rows.Sort(StringComparer.Ordinal);
            return rows;
        }

        // ── Counting ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>LENGTH</c> counts code points, on Azure and on the emulator alike: <c>a</c>U+1F600<c>b</c> is 3, where
        /// Calcite's <c>CHAR_LENGTH</c> counts 4 UTF-16 units.
        /// </summary>
        [Fact]
        public async Task LengthCountsCodePoints()
        {
            var lengths = await Values(Strings_(), "SELECT VALUE [c.id, LENGTH(c.s)] FROM c WHERE c.id IN (\"sup\", \"ascii\", \"sharp\")");

            lengths["sup"].GetInt32().Should().Be(3, "a supplementary character is one code point and two units");
            lengths["ascii"].GetInt32().Should().Be(3);
            lengths["sharp"].GetInt32().Should().Be(3, "a character of the basic plane is one of each");
        }

        /// <summary>
        /// On Azure <c>SUBSTRING</c>, <c>LEFT</c> and <c>RIGHT</c> count UTF-16 units, and a window whose edge splits a
        /// pair sends the half as U+FFFD, where Calcite's answers a lone surrogate; over a string with no supplementary
        /// character they answer as Calcite's. The emulator counts code points, and there the test skips.
        /// </summary>
        [Fact]
        public async Task SubstringLeftAndRightCountUnitsAndSendASplitPairAsReplacement()
        {
            var strings = Strings_();

            var ascii = await Values(strings, "SELECT VALUE [c.id, [SUBSTRING(c.s, 1, 1), LEFT(c.s, 2), RIGHT(c.s, 2)]] FROM c WHERE c.id = \"ascii\"");
            ascii["ascii"].EnumerateArray().Select(Text).Should().Equal("B", "aB", "Bc");

            var windows = await Values(strings, "SELECT VALUE [c.id, [SUBSTRING(c.s, 1, 1), SUBSTRING(c.s, 2, 1), SUBSTRING(c.s, 1, 2), LEFT(c.s, 2), RIGHT(c.s, 2)]] FROM c WHERE c.id = \"sup\"");
            var answered = windows["sup"].EnumerateArray().Select(Text).ToList();

            if (answered[0] == Emoji)
                Assert.Skip($"This service counts code points in SUBSTRING, LEFT and RIGHT ({string.Join(", ", answered.Select(Spell))}), as the emulator does; Azure counts UTF-16 units.");

            answered.Should().Equal("�", "�", Emoji, "a�", "�b");
        }

        /// <summary>
        /// On Azure <c>INDEX_OF</c> counts UTF-16 units, as Calcite's <c>POSITION</c> does: <c>b</c> in
        /// <c>a</c>U+1F600<c>b</c> is at 3, so <c>INDEX_OF + 1</c> is Calcite's 4. The emulator counts code points and
        /// answers 2, which is why <c>POSITION</c> differs on the emulator alone; there the test skips.
        /// </summary>
        [Fact]
        public async Task IndexOfCountsUnits()
        {
            var positions = await Values(Strings_(), "SELECT VALUE [c.id, [INDEX_OF(c.s, \"b\"), INDEX_OF(c.s, \"c\"), INDEX_OF(c.s, \"z\")]] FROM c WHERE c.id IN (\"sup\", \"ascii\")");

            positions["ascii"].EnumerateArray().Select(v => v.GetInt32()).Should().Equal(new[] { -1, 2, -1 }, "the search is case-sensitive, and -1 is no match");

            var position = positions["sup"][0].GetInt32();
            if (position == 2)
                Assert.Skip("This service counts code points in INDEX_OF (2 for b in a, U+1F600, b), as the emulator does; Azure counts UTF-16 units (3).");

            position.Should().Be(3);
        }

        // ── Case ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The service maps case by a simple mapping, and its case-insensitive match folds the same way: over ASCII it
        /// agrees with Java's full mapping, and outside it ß and U+FB00 stay as they are where Calcite writes <c>SS</c>
        /// and <c>FF</c>. Azure also leaves U+017F, U+0130 and U+212A, which the emulator maps; there the test skips,
        /// having asserted what both share.
        /// </summary>
        [Fact]
        public async Task CaseMappingIsSimpleAndAgreesWithCalcitesOverAscii()
        {
            var strings = Strings_();

            var mapped = await Values(strings, "SELECT VALUE [c.id, [UPPER(c.s), LOWER(c.s)]] FROM c WHERE c.id IN (\"ascii\", \"sharp\", \"lig\", \"longs\", \"idot\", \"kelvin\")");
            var matched = await Values(strings, "SELECT VALUE [c.id, [CONTAINS(c.s, \"SS\", true), STARTSWITH(c.s, \"ASS\", true), CONTAINS(c.s, \"ff\", true), ENDSWITH(c.s, \"KB\", true), CONTAINS(c.s, \"aB\", true)]] FROM c WHERE c.id IN (\"ascii\", \"sharp\", \"lig\", \"kelvin\")");

            mapped["ascii"].EnumerateArray().Select(Text).Should().Equal("ABC", "abc");
            matched["ascii"][4].GetBoolean().Should().BeTrue("ASCII folds alike at both ends");

            mapped["sharp"][0].GetString().Should().Be("AßB", "Java maps ß to SS, and the service leaves it");
            mapped["lig"][0].GetString().Should().Be("AﬀB", "Java maps U+FB00 to FF, and the service leaves it");
            matched["sharp"][0].GetBoolean().Should().BeFalse("UPPER(s) LIKE '%SS%' keeps aßb in Calcite, and the case-insensitive match does not");
            matched["sharp"][1].GetBoolean().Should().BeFalse();
            matched["lig"][2].GetBoolean().Should().BeFalse();

            var longs = mapped["longs"][0].GetString();
            if (longs == "ASB")
                Assert.Skip($"This service maps U+017F to S, U+0130 to {Spell(mapped["idot"][1].GetString()!)} and U+212A to {Spell(mapped["kelvin"][1].GetString()!)}, as the emulator does; Azure leaves all three.");

            longs.Should().Be("AſB", "Azure leaves U+017F, which Java and the emulator map to S");
            mapped["idot"][1].GetString().Should().Be("aİb", "Azure leaves U+0130, which Java maps to i and U+0307");
            mapped["kelvin"][1].GetString().Should().Be("aKb", "Azure leaves U+212A, which Java maps to k");
            matched["kelvin"][3].GetBoolean().Should().BeFalse("nor does its case-insensitive match fold it");
        }

        // ── Trimming ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>TRIM</c>, <c>LTRIM</c> and <c>RTRIM</c> remove white space, not spaces alone: on Azure exactly U+0020 and the
        /// characters the theory's trimmed class names (<c>JsonValues.IsTrimmed</c>), the three functions alike, at either
        /// end. The emulator removes a subset of them, not U+0085, U+00A0, U+180E, U+2007 or U+202F; there the test skips,
        /// having asserted that it removes nothing Azure keeps.
        /// </summary>
        [Fact]
        public async Task TrimRemovesWhiteSpace()
        {
            var trims = await Values(Strings_(), "SELECT VALUE [c.id, [LTRIM(c.s), RTRIM(c.s), TRIM(c.s)]] FROM c WHERE STARTSWITH(c.id, \"t\")");

            trims.Should().HaveCount(TrimCandidates.Length);

            var removed = new List<int>();
            foreach (var candidate in TrimCandidates)
            {
                var c = Char(candidate);
                var answers = trims["t" + candidate.ToString("X4")].EnumerateArray().Select(Text).ToList();

                if (answers.SequenceEqual([c + "a" + c, c + "a" + c, c + "a" + c]))
                    continue;

                answers.Should().Equal(["a" + c, c + "a", "a"], $"U+{candidate:X4} is removed at the start by LTRIM, at the end by RTRIM and at both by TRIM, or by none");
                removed.Add(candidate);
            }

            removed.Should().BeSubsetOf(AzureTrims, "nothing is removed that Azure keeps");
            removed.Should().Contain(new[] { 0x09, 0x20, 0x3000 }, "a tab, a space and U+3000 are removed by both");

            if (removed.Count < AzureTrims.Length)
                Assert.Skip($"This service removes {removed.Count} of the characters Azure removes, not {string.Join(", ", AzureTrims.Except(removed).Select(c => $"U+{c:X4}"))}, as the emulator does.");

            removed.Should().Equal(AzureTrims);
            TrimCandidates.Where(c => c != 0x20 && JsonValues.IsTrimmed(c)).Should().Equal(AzureTrims.Where(c => c != 0x20),
                "the theory's trimmed class is what Azure removes beside a space");
        }

        // ── What the adapter answers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Through a connection, over a path declared a string and nothing more, each function answers Calcite's value:
        /// what the service would compute otherwise is computed in process (<c>CosmosStringFunctions</c>). Where these
        /// pushed, Azure answered 3 for the length, <c>AßB</c> for the case, the trimmed string for the trim, and the
        /// case-insensitive match lost <c>aßb</c>.
        /// </summary>
        /// <remarks>
        /// <c>POSITION</c> is pushed over any string, Azure's <c>INDEX_OF</c> counting units as Calcite does; the
        /// emulator counts code points, so its answer there is asserted only where the service counts as Azure does.
        /// </remarks>
        [Fact]
        public async Task TheAdapterAnswersCalcitesStringsWhereTheServicePartsFromThem()
        {
            Strings_();

            const string S = """JSON_VALUE(c."DOC", '$.s')""";

            (await QueryAsync($"""SELECT c."id", CHAR_LENGTH({S}) FROM "strings" AS c WHERE c."id" IN ('sup', 'sharp')"""))
                .Should().Equal("sharp|3", "sup|4");
            (await QueryAsync($"""SELECT c."id", UPPER({S}), LOWER({S}) FROM "strings" AS c WHERE c."id" IN ('sharp', 'kelvin')"""))
                .Should().Equal("kelvin|A\u212AB|akb", "sharp|ASSB|a\u00DFb");
            (await QueryAsync($"""SELECT c."id", SUBSTRING({S} FROM 2 FOR 1) FROM "strings" AS c WHERE c."id" = 'sup'"""))
                .Should().Equal("sup|\uD83D");
            (await QueryAsync($"""SELECT c."id", TRIM({S}) FROM "strings" AS c WHERE c."id" IN ('t0009', 't3000', 't0020')"""))
                .Should().Equal("t0009|\ta\t", "t0020|a", "t3000|\u3000a\u3000");
            (await QueryAsync($"""SELECT c."id" FROM "strings" AS c WHERE UPPER({S}) LIKE '%SS%'"""))
                .Should().Equal("sharp");

            var position = await QueryAsync($"""SELECT c."id", POSITION('b' IN {S}) FROM "strings" AS c WHERE c."id" = 'sup'""");
            if (position.SequenceEqual(["sup|3"]))
                Assert.Skip("This service counts code points in INDEX_OF, as the emulator does, so POSITION pushed answers 3 where Calcite answers 4; Azure counts units.");

            position.Should().Equal("sup|4");
        }

        // ── What the client sends ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A parameter holding a lone surrogate is never sent: the SDK's UTF-8 encoder refuses it while it writes the
        /// statement, so the statement fails on either target where Calcite answers it.
        /// </summary>
        [Fact]
        public async Task ALoneSurrogateParameterFailsBeforeItIsSent()
        {
            var strings = Strings_();

            var thrown = await FluentActions.Awaiting(() => Values(strings, "SELECT VALUE [c.id, INDEX_OF(c.s, @p)] FROM c", ("@p", "\uD83D")))
                .Should().ThrowAsync<Exception>();

            var chain = new List<Exception>();
            for (Exception? e = thrown.Which; e is not null; e = e.InnerException)
                chain.Add(e);

            chain.Should().Contain(e => e is EncoderFallbackException, "the encoder refuses the unpaired unit before anything is sent");
        }

        static string Spell(string text) => string.Concat(text.EnumerateRunes().Select(r => r.Value is >= 0x21 and <= 0x7E ? ((char)r.Value).ToString() : $"<U+{r.Value:X4}>"));

    }

}
