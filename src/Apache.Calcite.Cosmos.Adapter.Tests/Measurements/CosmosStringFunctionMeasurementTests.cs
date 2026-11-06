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

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
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
    /// <b>The emulator's own facts, apart.</b> The emulator stores U+0000 and U+FDD0 escaped (<c>EmulatorStoredText</c>),
    /// and counts, cuts, compares and returns what it stores; the tests of its spelling run over a container of their own
    /// and skip where U+0000 is stored as written, as on Azure. Measured on both images on 2026-10-31.
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
        /// The strings the counts, the case mappings and the searches are asked of, by id: a supplementary character
        /// between two ASCII letters; ASCII; the characters Java's full case mapping and the services' part over; and
        /// U+0000 within a string and leading one.
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
            ("nul", "a\0b"),
            ("nullead", "\0ab"),
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
        static CosmosRequestCharges? _charges;

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
                CustomHandlers = { (_charges = new(nameof(CosmosStringFunctionMeasurementTests))) },
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
            if (_client is not null)
                _charges?.Write();
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

        /// <summary>
        /// Runs a statement and answers its rows as they came: an array the service built leaves out each member that was
        /// undefined, which is how an undefined value is seen.
        /// </summary>
        static async Task<List<JsonElement>> Rows(Container container, string sql, params (string Name, object Value)[] parameters)
        {
            var query = new QueryDefinition(sql);
            foreach (var (name, value) in parameters)
                query = query.WithParameter(name, value);

            var rows = new List<JsonElement>();

            using var iterator = container.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("a") });
            while (iterator.HasMoreResults)
            {
                using var response = await iterator.ReadNextAsync(TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();

                using var page = await JsonDocument.ParseAsync(response.Content, cancellationToken: TestContext.Current.CancellationToken);
                foreach (var row in page.RootElement.GetProperty("Documents").EnumerateArray())
                    rows.Add(row.Clone());
            }

            return rows;
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

        // ── Searching ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// On Azure <c>REPLACE</c> and <c>CONTAINS</c>, both modes, read a string only up to its first U+0000, and so does
        /// a <c>LIKE</c> whose pattern is one run of text between two <c>%</c>: <c>b</c> is not found in
        /// <c>a</c>U+0000<c>b</c>, nor <c>a</c> in U+0000<c>ab</c>. A search holding U+0000 matches every string, and
        /// replaces every byte. <c>INDEX_OF</c>, <c>STARTSWITH</c>, <c>ENDSWITH</c>, <c>LENGTH</c>, a <c>LIKE</c> of any other
        /// shape and a replacement holding U+0000 read past it. The emulator stores U+0000 as U+FDD0 followed by
        /// <c>0</c>, and there the test skips.
        /// </summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.NulEndsWhatReplaceAndContainsRead")]
        public async Task NulEndsWhatReplaceAndContainsRead()
        {
            var strings = Strings_();

            var lengths = await Values(strings, "SELECT VALUE [c.id, LENGTH(c.s)] FROM c WHERE c.id IN (\"nul\", \"nullead\")");
            if (lengths["nul"].GetInt32() != 3)
                Assert.Skip($"This service stores U+0000 as more than one character (LENGTH of a, U+0000, b is {lengths["nul"].GetInt32()}), as the emulator stores it as U+FDD0 and 0; Azure stores it as written.");

            lengths["nullead"].GetInt32().Should().Be(3);

            var read = await Values(strings,
                "SELECT VALUE [c.id, [REPLACE(c.s, \"b\", \"!\"), CONTAINS(c.s, \"b\"), CONTAINS(c.s, \"B\", true), c.s LIKE \"%b%\", " +
                "INDEX_OF(c.s, \"b\"), STARTSWITH(c.s, \"A\", true), ENDSWITH(c.s, \"B\", true), c.s LIKE \"%a%b%\", c.s LIKE \"%b\", REPLACE(c.s, \"b\", @nul)]] " +
                "FROM c WHERE c.id IN (\"nul\", \"nullead\")", ("@nul", "\0"));

            var nul = read["nul"].EnumerateArray().ToList();
            Text(nul[0]).Should().Be("a\0b", "REPLACE leaves the b after U+0000, which Calcite replaces");
            nul[1].GetBoolean().Should().BeFalse("CONTAINS finds no b after U+0000");
            nul[2].GetBoolean().Should().BeFalse("nor does the case-insensitive one");
            nul[3].GetBoolean().Should().BeFalse("nor LIKE '%b%', which the service answers as a CONTAINS");
            nul[4].GetInt32().Should().Be(2, "INDEX_OF reads past U+0000");
            nul[5].GetBoolean().Should().BeTrue();
            nul[6].GetBoolean().Should().BeTrue("ENDSWITH reads past U+0000");
            nul[7].GetBoolean().Should().BeTrue("a LIKE of two runs reads past it");
            nul[8].GetBoolean().Should().BeTrue();
            Text(nul[9]).Should().Be("a\0b", "a replacement holding U+0000 is written as it is, the subject read as far as before");

            var lead = read["nullead"].EnumerateArray().ToList();
            Text(lead[0]).Should().Be("\0ab");
            lead[1].GetBoolean().Should().BeFalse();
            lead[3].GetBoolean().Should().BeFalse();
            lead[4].GetInt32().Should().Be(2);
            lead[7].GetBoolean().Should().BeTrue();

            var sought = await Values(strings, "SELECT VALUE [c.id, [CONTAINS(c.s, @nul), REPLACE(c.s, @nul, \"!\"), INDEX_OF(c.s, @nul), REPLACE(c.s, \"B\", @nul)]] FROM c WHERE c.id = \"ascii\"", ("@nul", "\0"));
            var ascii = sought["ascii"].EnumerateArray().ToList();
            ascii[0].GetBoolean().Should().BeTrue("a search holding U+0000 matches a string that holds none");
            Text(ascii[1]).Should().Be("!!!", "and REPLACE of one replaces every byte");
            ascii[2].GetInt32().Should().Be(-1, "where INDEX_OF finds none");
            Text(ascii[3]).Should().Be("a\0c", "a replacement holding U+0000 is written as it is");
        }

        /// <summary>
        /// <c>REPLACE</c> of an empty search is undefined for every string, on Azure and on the emulator, where Calcite's is
        /// the subject.
        /// </summary>
        [Fact]
        public async Task AReplacementOfAnEmptySearchIsUndefined()
        {
            var replaced = await Rows(Strings_(), "SELECT VALUE [c.id, REPLACE(c.s, @empty, \"-\"), REPLACE(c.s, \"\", \"-\")] FROM c WHERE c.id IN (\"ascii\", \"sup\")", ("@empty", ""));

            replaced.Should().HaveCount(2);
            replaced.Should().OnlyContain(row => row.GetArrayLength() == 1, "neither form answers a value, so the array holds the id alone");
        }

        /// <summary>
        /// A <c>SUBSTRING</c> whose start is below 0 answers the empty string projected, on Azure and on the emulator,
        /// where Calcite's clips the window to the string; one starting at 0 of length 0 is the empty string, projected and
        /// in a <c>WHERE</c>, which is what the clip is written as.
        /// </summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.SubstringOfANegativeStartIsEmpty")]
        public async Task AWindowStartingBeforeTheStringIsEmpty()
        {
            var strings = Strings_();

            var windows = await Values(strings, "SELECT VALUE [c.id, [SUBSTRING(c.s, -1, 2), SUBSTRING(c.s, -2, 4), SUBSTRING(c.s, 0, 0), SUBSTRING(c.s, 0, 1)]] FROM c WHERE c.id = \"ascii\"");
            windows["ascii"].EnumerateArray().Select(Text).Should().Equal("", "", "", "a");

            var kept = await Values(strings, "SELECT VALUE [c.id, c.id] FROM c WHERE SUBSTRING(c.s, 0, 0) = \"\" AND c.id IN (\"ascii\", \"sup\")");
            kept.Keys.Should().BeEquivalentTo(["ascii", "sup"]);
        }

        /// <summary>
        /// <c>REPLICATE</c> is undefined for a negative count, on Azure and on the emulator, where Calcite's <c>REPEAT</c>
        /// is the empty string; a count of 0 is the empty string at both. On Azure it is undefined past 10,000 code points,
        /// and defined up to them: <c>ab</c> 5,000 times is, 5,001 times is not, and a, U+1F600, b 3,333 times, 13,332
        /// UTF-16 units, is. The emulator has no such limit, and there the test skips.
        /// </summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.ReplicateStopsAtTenThousandCodePoints")]
        public async Task ARepetitionIsUndefinedForANegativeCountAndPastTenThousandCodePoints()
        {
            var strings = Strings_();

            var repeated = await Rows(strings, "SELECT VALUE [REPLICATE(c.s, 0), REPLICATE(c.s, 2), REPLICATE(c.s, -1)] FROM c WHERE c.id = \"ascii\"");
            repeated.Single().EnumerateArray().Select(Text).Should().Equal(["", "aBcaBc"], "a negative count answers nothing, so the array ends before it");

            var limits = await Rows(strings, "SELECT VALUE [LENGTH(REPLICATE(\"ab\", 5000)), LENGTH(REPLICATE(c.s, 3333)), LENGTH(REPLICATE(\"ab\", 5001)), LENGTH(REPLICATE(c.s, 3334))] FROM c WHERE c.id = \"sup\"");
            var sup = limits.Single().EnumerateArray().ToList();
            sup[0].GetInt32().Should().Be(10000);
            sup[1].GetInt32().Should().Be(9999, "a, U+1F600, b is three code points and four units");

            if (sup.Count > 2)
                Assert.Skip($"This service repeats past 10,000 code points ({sup[2].GetInt32()}), as the emulator does; Azure answers nothing there.");

            sup.Should().HaveCount(2, "ab 5,001 times is 10,002 code points, and a, U+1F600, b 3,334 times is too, though 3,333 times is 13,332 units");
        }

        // ── The emulator's spelling ───────────────────────────────────────────────────────────

        /// <summary>
        /// The strings the emulator's spelling is read over, each its own document in a container of its own: U+0000 and
        /// U+FDD0 alone, doubled, mixed, around ASCII and before <c>0</c> and <c>1</c>, beside the units either side of the
        /// escape. The emulator probe's 28 of 2026-10-31, on both images.
        /// </summary>
        static readonly string[] Spellings =
        [
            "", "a", "b", "z", "0", "1", "abc", "\0", "\uFDD0", "\0\0", "\uFDD0\uFDD0", "\0\uFDD0", "\uFDD0\0",
            "a\0", "\0a", "a\0b", "a\uFDD0", "\uFDD0a", "a\uFDD0b", "a\0\uFDD0b", "\uFDD00", "\uFDD01", "a\uFDD00b",
            "\uE000", "\uFFFF", "A\0B", "\0b", "\uFDD0b",
        ];

        /// <summary>The values compared with them: every kind of escape, ASCII, and the digits an escape's second unit is.</summary>
        static readonly string[] Sent =
        [
            "", "\0", "\uFDD0", "a\0", "\0a", "a\0b", "\uFDD0a", "a\uFDD0", "a\uFDD0b", "\uFDD00", "\uFDD01",
            "\0\uFDD0", "0", "1", "a", "b", "z", "\uE000", "a\0\uFDD0b", "\0b",
        ];

        static Task<Container>? _spelling;

        /// <summary>
        /// The container of <see cref="Spellings"/>, written once the first test asks, so that a service refusing one leaves
        /// the class's other tests standing; skipped where the service stores U+0000 as written, as Azure does.
        /// </summary>
        static async Task<Container> Spelling()
        {
            Strings_();

            var container = await (_spelling ??= Create());
            var lengths = await Values(container, "SELECT VALUE [c.id, LENGTH(c.s)] FROM c WHERE c.id IN (\"e07\", \"e08\")");
            if (lengths["e07"].GetInt32() == 1)
                Assert.Skip("This service stores U+0000 as written (LENGTH 1), as Azure does; the emulator stores it as U+FDD0 and 0.");

            return container;

            static async Task<Container> Create()
            {
                try { await _database!.GetContainer("spelling").DeleteContainerAsync(); } catch (CosmosException) { }
                var spelling = (await _database!.CreateContainerIfNotExistsAsync(new ContainerProperties("spelling", "/pk"))).Container;
                for (var i = 0; i < Spellings.Length; i++)
                    (await Write(spelling, $"e{i:D2}", Spellings[i], CancellationToken.None)).Should().BeTrue("the emulator stores every one");

                return spelling;
            }
        }

        /// <summary>The ids of the documents a statement kept, in order.</summary>
        static async Task<List<string>> Kept(Container container, string where, params (string Name, object Value)[] parameters) =>
            [.. (await Values(container, $"SELECT VALUE [c.id, c.id] FROM c WHERE {where}", parameters)).Keys.Order(StringComparer.Ordinal)];

        /// <summary>The ids of the documents Calcite keeps for a test over the stored strings.</summary>
        static List<string> Calcite(Func<string, bool> test) =>
            [.. Enumerable.Range(0, Spellings.Length).Where(i => test(Spellings[i])).Select(i => $"e{i:D2}")];

        /// <summary>
        /// The emulator compares a value as it is sent with the string as it stores it (<see cref="EmulatorStoredText"/>):
        /// sent in its stored spelling, as a parameter or a literal, a value is kept exactly where Calcite keeps it under
        /// <c>=</c>, <c>IN</c>, <c>!=</c> and <c>STARTSWITH</c>, and so is one compared with a function of the path; under
        /// <c>ENDSWITH</c> and <c>CONTAINS</c>, both ways, unless it begins with <c>0</c> or <c>1</c>, which an escape's
        /// second unit matches. Sent as written, a value holding either character is kept nowhere, so <c>=</c> drops the row
        /// and <c>!=</c> keeps it. Measured on both images on 2026-10-31. The row's rewrites, the two characters, are read as
        /// data (G1).
        /// </summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.EmulatorComparesTheSpellingItIsSent")]
        public async Task TheEmulatorComparesWhatItIsSentWithWhatItStores()
        {
            var spelling = await Spelling();

            var exact = new (string Where, Func<string, string, bool> Calcite)[]
            {
                ("c.s = @p", (s, v) => s == v),
                ("c.s IN (@p, \"zz-none\")", (s, v) => s == v),
                ("c.s != @p", (s, v) => s != v),
                ("STARTSWITH(c.s, @p)", (s, v) => s.StartsWith(v, StringComparison.Ordinal)),
                ("CONCAT(c.s, \"\") = @p", (s, v) => s == v),
            };
            var unlessADigitLeads = new (string Where, Func<string, string, bool> Calcite)[]
            {
                ("ENDSWITH(c.s, @p)", (s, v) => s.EndsWith(v, StringComparison.Ordinal)),
                ("CONTAINS(c.s, @p)", (s, v) => s.Contains(v, StringComparison.Ordinal)),
                ("NOT CONTAINS(c.s, @p)", (s, v) => s.Contains(v, StringComparison.Ordinal) == false),
            };

            foreach (var v in Sent)
            {
                foreach (var (where, calcite) in exact.Concat(unlessADigitLeads.Where(_ => v is not ['0' or '1', ..])))
                    (await Kept(spelling, where, ("@p", EmulatorStoredText.Of(v)))).Should().Equal(Calcite(s => calcite(s, v)), $"{where} with {Spell(v)} sent as stored");

                (await Kept(spelling, "c.s = " + Literal(EmulatorStoredText.Of(v)))).Should().Equal(Calcite(s => s == v), $"= {Spell(v)} written as stored");
            }

            (await Kept(spelling, "ENDSWITH(c.s, @p)", ("@p", "0"))).Should().Contain("e07", "a stored U+0000 ends in 0");
            (await Kept(spelling, "c.s = @p", ("@p", "\0"))).Should().BeEmpty("U+0000 sent as written is no string the emulator stores");
            (await Kept(spelling, "c.s != @p", ("@p", "\uFDD0"))).Should().Contain("e08", "nor is U+FDD0, so != keeps the row equal to it");
        }

        /// <summary>
        /// The emulator counts, cuts, reverses and searches a path's string as it stores it, U+0000 and U+FDD0 two units
        /// each, and returns whole escape pairs decoded: so <c>LENGTH</c> of U+0000 is 2, <c>LEFT(s, 1)</c> of it a lone
        /// U+FDD0, <c>REVERSE</c> of it <c>0</c> then U+FDD0, and <c>INDEX_OF</c> the stored index, one more for each
        /// escape before it. Without either character each answers as on Azure. Measured on both images on 2026-10-31.
        /// </summary>
        [Fact]
        public async Task TheEmulatorCountsAndCutsTheStringItStores()
        {
            var spelling = await Spelling();

            var functions = new (string Sql, Func<string, object> OfStored)[]
            {
                ("LENGTH(c.s)", s => s.Length),
                ("SUBSTRING(c.s, 1, 2)", s => s.Length > 1 ? s.Substring(1, Math.Min(2, s.Length - 1)) : ""),
                ("LEFT(c.s, 1)", s => s[..Math.Min(1, s.Length)]),
                ("RIGHT(c.s, 2)", s => s[^Math.Min(2, s.Length)..]),
                ("REVERSE(c.s)", s => new string([.. s.Reverse()])),
                ("INDEX_OF(c.s, @b)", s => s.IndexOf('b', StringComparison.Ordinal)),
                ("INDEX_OF(c.s, @nul)", s => s.IndexOf(EmulatorStoredText.Of("\0"), StringComparison.Ordinal)),
            };

            foreach (var (sql, ofStored) in functions)
            {
                var answered = await Values(spelling, $"SELECT VALUE [c.id, {sql}] FROM c", ("@b", "b"), ("@nul", EmulatorStoredText.Of("\0")));
                for (var i = 0; i < Spellings.Length; i++)
                {
                    var expected = ofStored(EmulatorStoredText.Of(Spellings[i]));
                    var value = answered[$"e{i:D2}"];
                    if (expected is int count)
                        value.GetInt32().Should().Be(count, $"{sql} of {Spell(Spellings[i])}");
                    else
                        value.GetString().Should().Be(Decoded((string)expected), $"{sql} of {Spell(Spellings[i])}");
                }
            }
        }

        /// <summary>What the emulator returns of a stored spelling: each whole escape pair decoded, a lone U+FDD0 as itself.</summary>
        static string Decoded(string stored) => Regex.Replace(stored, "﷐([01])", m => m.Groups[1].Value == "0" ? "\0" : "﷐");

        /// <summary>
        /// <c>CONTAINS(p, U+FDD0 0)</c> is true exactly of the strings holding U+0000, the stored spelling of U+0000 being
        /// the only place U+FDD0 meets <c>0</c>; and its <c>NOT</c> of the rest. A raw U+0000 in the pattern is no such
        /// test. Measured on both images on 2026-10-31.
        /// </summary>
        [Fact]
        public async Task TheStoredSpellingOfU0000IsAnExactTest()
        {
            var spelling = await Spelling();

            (await Kept(spelling, "CONTAINS(c.s, @p)", ("@p", EmulatorStoredText.Of("\0")))).Should().Equal(Calcite(s => s.Contains('\0')));
            (await Kept(spelling, "NOT CONTAINS(c.s, @p)", ("@p", EmulatorStoredText.Of("\0")))).Should().Equal(Calcite(s => s.Contains('\0') == false));
            (await Kept(spelling, "CONTAINS(c.s, @p)", ("@p", EmulatorStoredText.Of("\uFDD0")))).Should().Equal(Calcite(s => s.Contains('\uFDD0')), "and U+FDD0 1 of the strings holding U+FDD0");
        }

        /// <summary>
        /// A function that makes U+0000 from a path fails the whole statement with a 500 on the emulator: <c>CONCAT</c> of a
        /// path and U+0000, as a literal or a parameter, and <c>REPLACE</c> whose replacement holds one, wherever the search
        /// is found. A constant <c>CONCAT</c> of U+0000 loses it silently. Azure answers the replacement written as it is,
        /// and there the test skips. Measured on both images on 2026-10-31.
        /// </summary>
        [Fact]
        public async Task AFunctionThatMakesU0000FromAPathFails()
        {
            var strings = Strings_();

            var made = await Status(strings, "SELECT VALUE [c.id, CONCAT(c.s, @nul)] FROM c WHERE c.id = \"ascii\"", ("@nul", "\0"));
            if (made == HttpStatusCode.OK)
                Assert.Skip("This service answers a U+0000 made from a path, as Azure does; the emulator fails the statement.");

            made.Should().Be(HttpStatusCode.InternalServerError);
            (await Status(strings, "SELECT VALUE [c.id, CONCAT(c.s, \"\\u0000\")] FROM c WHERE c.id = \"ascii\"")).Should().Be(HttpStatusCode.InternalServerError, "a literal fails as a parameter does");
            (await Status(strings, "SELECT VALUE [c.id, REPLACE(c.s, \"B\", @nul)] FROM c WHERE c.id = \"ascii\"", ("@nul", "\0"))).Should().Be(HttpStatusCode.InternalServerError, "a replacement made where the search is found");
            (await Values(strings, "SELECT VALUE [c.id, REPLACE(c.s, \"z\", @nul)] FROM c WHERE c.id = \"ascii\"", ("@nul", "\0")))["ascii"].GetString().Should().Be("aBc", "and none where it is not");
            (await Values(strings, "SELECT VALUE [c.id, CONCAT(\"a\", @nul)] FROM c WHERE c.id = \"ascii\"", ("@nul", "\0")))["ascii"].GetString().Should().Be("a", "a constant loses it");
        }

        /// <summary>
        /// What the emulator returns of a constant is decoded as a stored string is: U+FDD0 <c>0</c> comes back U+0000, U+FDD0
        /// <c>1</c> comes back U+FDD0, and a lone U+FDD0 as itself, so a constant sent in the stored spelling comes back as
        /// written, alone, in a conditional's arm, in a coalesce, as an object's or an array's member and inside an object
        /// sent whole. Sent raw, a U+0000 returned fails the statement with a 500, anywhere a value is returned, and a U+FDD0
        /// followed by a digit comes back as the escape it spells. Azure returns U+0000 as written, and there the test skips.
        /// Measured on both images on 2026-10-31.
        /// </summary>
        [Fact]
        public async Task AConstantTheEmulatorReturnsIsDecodedAsStored()
        {
            var strings = Strings_();

            const string Ascii = " FROM c WHERE c.id = \"ascii\"";
            var raw = await Status(strings, "SELECT VALUE [c.id, @p]" + Ascii, ("@p", "\0"));
            if (raw == HttpStatusCode.OK)
                Assert.Skip("This service returns a U+0000 sent raw, as Azure does; the emulator fails the statement.");

            raw.Should().Be(HttpStatusCode.InternalServerError);
            foreach (var returned in new[] { "(c.s = \"aBc\" ? @p : \"x\")", "(c.missing ?? @p)", "{\"k\": @p}", "\"\\u0000\"" })
                (await Status(strings, $"SELECT VALUE [c.id, {returned}]" + Ascii, ("@p", "a\0b"))).Should().Be(HttpStatusCode.InternalServerError, returned);

            foreach (var value in new[] { "\0", "a\0b", "\uFDD0", "a\uFDD0b", "\uFDD00", "\0\uFDD0" })
                foreach (var returned in new[] { "@p", "(c.s = \"aBc\" ? @p : \"x\")", "(c.missing ?? @p)" })
                    (await Values(strings, $"SELECT VALUE [c.id, {returned}]" + Ascii, ("@p", EmulatorStoredText.Of(value))))["ascii"].GetString()
                        .Should().Be(value, $"{returned} with {Spell(value)} sent as stored");

            // Nested where the statement builds the value, or where the parameter is the object: each string decoded.
            foreach (var value in new[] { "\0", "a\0b", "﷐", "\0﷐" })
            {
                var stored = EmulatorStoredText.Of(value);
                (await Values(strings, "SELECT VALUE [c.id, {\"k\": @p}]" + Ascii, ("@p", stored)))["ascii"].GetProperty("k").GetString().Should().Be(value, $"an object member, {Spell(value)}");
                (await Values(strings, "SELECT VALUE [c.id, [@p]]" + Ascii, ("@p", stored)))["ascii"][0].GetString().Should().Be(value, $"an array element, {Spell(value)}");
                var whole = (await Values(strings, "SELECT VALUE [c.id, @p]" + Ascii, ("@p", new Dictionary<string, object> { ["k"] = stored, ["a"] = new[] { stored } })))["ascii"];
                whole.GetProperty("k").GetString().Should().Be(value, $"an object parameter's member, {Spell(value)}");
                whole.GetProperty("a")[0].GetString().Should().Be(value, $"an object parameter's array, {Spell(value)}");
            }

            (await Values(strings, "SELECT VALUE [c.id, @p]" + Ascii, ("@p", "a\uFDD0b")))["ascii"].GetString().Should().Be("a\uFDD0b", "a lone U+FDD0 comes back as itself");
            (await Values(strings, "SELECT VALUE [c.id, @p]" + Ascii, ("@p", "\uFDD00")))["ascii"].GetString().Should().Be("\0", "U+FDD0 0 sent raw comes back as the U+0000 it spells");
        }

        /// <summary>Runs a statement and answers the status its first page came back with.</summary>
        static async Task<HttpStatusCode> Status(Container container, string sql, params (string Name, object Value)[] parameters)
        {
            var query = new QueryDefinition(sql);
            foreach (var (name, value) in parameters)
                query = query.WithParameter(name, value);

            using var iterator = container.GetItemQueryStreamIterator(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("a") });
            using var response = await iterator.ReadNextAsync(TestContext.Current.CancellationToken);
            return response.StatusCode;
        }

        /// <summary>A string as a literal of the service's: U+0000 to U+001F escaped, every other unit as it is.</summary>
        static string Literal(string text) =>
            "\"" + string.Concat(text.Select(c => c switch { '"' => "\\\"", '\\' => "\\\\", < ' ' => $"\\u{(int)c:x4}", _ => c.ToString() })) + "\"";

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
        /// statement, so the statement fails on either target where Calcite answers it. The row's unsent class is read as
        /// data (G1).
        /// </summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.LoneSurrogateParameterIsNeverSent")]
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

        // ── Affixes, LIKE and the functions by band, on the emulator (Azure at probe run 1) ──────────

        /// <summary>Asks expressions over witnesses for a row, each answer spelled as <see cref="CosmosWitnessProbe"/> spells it, the witnesses' joined by spaces.</summary>
        static async Task<string[]> Probe(string row, IReadOnlyList<string?> witnesses, params string[] expressions) =>
            [.. (await CosmosWitnessProbe.Ask(Strings_(), _unavailable, row, witnesses, expressions)).Select(answers => string.Join(" ", answers))];

        /// <summary>The affixes match code points as written, with no normalisation, a true third operand folding ASCII case.</summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.AffixesMatchByCodePoint")]
        public async Task TheAffixesMatchByCodePoint()
        {
            string?[] witnesses = ["\"a\ud83d\ude00b\"", "\"\u00e9x\"", "\"e\u0301x\"", "\"ABC\"", "\"abc\"", "\"\ue000x\"", "\"\""];
            (await Probe("Service.AffixesMatchByCodePoint", witnesses, "STARTSWITH(c.v, \"a\ud83d\ude00\")", "ENDSWITH(c.v, \"\ud83d\ude00b\")", "CONTAINS(c.v, \"\ud83d\ude00\")",
                "STARTSWITH(c.v, \"\u00e9\")", "STARTSWITH(c.v, \"e\")", "STARTSWITH(c.v, \"a\")", "STARTSWITH(c.v, \"a\", false)", "STARTSWITH(c.v, \"a\", true)", "ENDSWITH(c.v, \"\")", "STARTSWITH(c.v, \"\ue000\")")).Should().Equal(
                "true/T false/F false/F false/F false/F false/F false/F",
                "true/T false/F false/F false/F false/F false/F false/F",
                "true/T false/F false/F false/F false/F false/F false/F",
                "false/F true/T false/F false/F false/F false/F false/F",
                "false/F false/F true/T false/F false/F false/F false/F",
                "true/T false/F false/F false/F true/T false/F false/F",
                "true/T false/F false/F false/F true/T false/F false/F",
                "true/T false/F false/F true/T true/T false/F false/F",
                "true/T true/T true/T true/T true/T true/T true/T",
                "false/F false/F false/F false/F false/F true/T false/F");
        }

        /// <summary><c>LIKE</c>'s <c>_</c> matches one code point and <c>%</c> any run, case sensitive.</summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.LikeMatchesByCodePoint")]
        public async Task LikeMatchesByCodePoint()
        {
            string?[] witnesses = ["\"a\ud83d\ude00b\"", "\"axb\"", "\"axxb\"", "\"ab\"", "\"AXB\"", "\"a\u00e9b\"", "\"a\ue000b\""];
            (await Probe("Service.LikeMatchesByCodePoint", witnesses, "c.v LIKE \"a_b\"", "c.v LIKE \"a__b\"", "c.v LIKE \"a%b\"", "c.v LIKE \"a%\"", "c.v LIKE \"%b\"", "c.v LIKE \"%x%\"", "c.v LIKE \"axb\"")).Should().Equal(
                "true/T true/T false/F false/F false/F true/T true/T",
                "false/F false/F true/T false/F false/F false/F false/F",
                "true/T true/T true/T true/T false/F true/T true/T",
                "true/T true/T true/T true/T false/F true/T true/T",
                "true/T true/T true/T true/T false/F true/T true/T",
                "false/F true/T true/T false/F false/F false/F false/F",
                "false/F true/T false/F false/F false/F false/F false/F");
        }

        /// <summary><c>LIKE</c> reads a bracket as a range or its complement, <c>[[]</c> as a literal, an unclosed bracket as matching nothing; <c>ESCAPE</c> makes the next character literal.</summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.LikeReadsBracketsAsARange")]
        public async Task LikeReadsBracketsAsARange()
        {
            string?[] witnesses = ["\"bikes\"", "\"[a-c]x\"", "\"dog\"", "\"[x\"", "\"a%\"", "\"ab\""];
            (await Probe("Service.LikeReadsBracketsAsARange", witnesses, "c.v LIKE \"[a-c]%\"", "c.v LIKE \"[^a-c]%\"", "c.v LIKE \"[[]%\"", "c.v LIKE \"a!%\" ESCAPE \"!\"", "c.v LIKE \"[x\"")).Should().Equal(
                "true/T false/F false/F false/F true/T true/T",
                "false/F true/T true/T true/T false/F false/F",
                "false/F true/T false/F true/T false/F false/F",
                "false/F false/F false/F false/F true/T false/F",
                "false/F false/F false/F false/F false/F false/F");
        }

        /// <summary>
        /// Each function by the name its rule reads it by, as Calcite and as the service write it over <c>s</c>, the one
        /// lower-case <c>s</c> in either, keyed at the operand <c>s</c> stands for; the other operands are adversarial on the
        /// emulator, a <c>0</c> or a <c>1</c> its escapes' second units hold, each keyed as the literal it is where the row
        /// names it, and a function spelled twice answers the worse of the two. The concatenation that reads a null as the
        /// empty string is Calcite's <c>CONCAT</c> under PostgreSQL's library and Oracle's, the service's spelling CONCAT's.
        /// </summary>
        /// <remarks>
        /// Added on 2026-11-06: the two concatenations that read a null as empty, whose service spelling is
        /// <c>CONCAT</c>'s, so that run 1's lines of it judge them on Azure too; and the spellings after the trims, a search,
        /// a replacement, a window's length and the literals the emulator stores escaped, each keyed as it is, which the
        /// probe account has not run and so claim nothing on Azure
        /// (<c>CosmosAxiomsTests.EveryAzureFunctionOutcomeIsWhatTheProbeAccountsRecordMeasures</c>). Keyed the same day: a
        /// concatenation's and a repetition's literal apart from a stored operand (<see cref="FunctionKey.Literal"/>), and
        /// <c>POSITION</c>'s needle, each as the spellings run 1 holds send it, so that its lines judge them on Azure.
        /// </remarks>
        internal static readonly CosmosFunctionSpelling[] Functions =
        [
            new("CHAR_LENGTH", 0, "CHAR_LENGTH(s)", "LENGTH(s)"),
            new("CHARACTER_LENGTH", 0, "CHARACTER_LENGTH(s)", "LENGTH(s)"),
            new("SUBSTRING", 0, "SUBSTRING(s FROM 2 FOR 2)", "SUBSTRING(s, 1, 2)", Fixed: [Key("SUBSTRING", 2, CosmosValueClass.Number, JsonCharacterBand.None, FunctionValueClass.AtLeastOne)]),
            new("LEFT", 0, "LEFT(s, 2)", "LEFT(s, 2)"),
            new("RIGHT", 0, "RIGHT(s, 2)", "RIGHT(s, 2)"),
            new("UPPER", 0, "UPPER(s)", "UPPER(s)"),
            new("LOWER", 0, "LOWER(s)", "LOWER(s)"),
            new("REPLACE", 0, "REPLACE(s, '0', 'x')", "REPLACE(s, \"0\", \"x\")", Fixed: [Search, Replacement]),
            new("REPLACE", 0, "REPLACE(s, '1', 'x')", "REPLACE(s, \"1\", \"x\")", Fixed: [Search, Replacement]),
            new("POSITION", 1, "POSITION('0' IN s)", "INDEX_OF(s, \"0\") + 1", Fixed: [Needle]),
            new("POSITION", 1, "POSITION('1' IN s)", "INDEX_OF(s, \"1\") + 1", Fixed: [Needle]),
            new("REVERSE", 0, "REVERSE(s)", "REVERSE(s)"),
            new("||", FunctionKey.Every, "s || 'z'", "CONCAT(s, \"z\")", Fixed: [Joined("||", JsonCharacterBand.None)]),
            new("CONCAT", FunctionKey.Every, "CONCAT(s, 'z')", "CONCAT(s, \"z\")", Fixed: [Joined("CONCAT", JsonCharacterBand.None)]),
            new(StringCalls.NullAsEmpty, FunctionKey.Every, "CONCAT(s, 'z')", "CONCAT(s, \"z\")", "postgresql", [Joined(StringCalls.NullAsEmpty, JsonCharacterBand.None)]),
            new(StringCalls.NullAsEmpty, FunctionKey.Every, "CONCAT(s, 'z')", "CONCAT(s, \"z\")", "oracle", [Joined(StringCalls.NullAsEmpty, JsonCharacterBand.None)]),
            new("REPEAT", 0, "REPEAT(s, 2)", "REPLICATE(s, 2)", Fixed: [Key("REPEAT", FunctionKey.Literal, CosmosValueClass.Number, JsonCharacterBand.None, FunctionValueClass.AtLeastOne)]),
            new("TRIM BOTH", 1, "TRIM(BOTH ' ' FROM s)", "TRIM(s)"),
            new("TRIM LEADING", 1, "TRIM(LEADING ' ' FROM s)", "LTRIM(s)"),
            new("TRIM TRAILING", 1, "TRIM(TRAILING ' ' FROM s)", "RTRIM(s)"),
            new("REPLACE", 1, "REPLACE('a0b1c', s, 'x')", "REPLACE(\"a0b1c\", s, \"x\")"),
            new("REPLACE", 2, "REPLACE('a0b1c', 'b', s)", "REPLACE(\"a0b1c\", \"b\", s)"),
            new("REPLACE", null, "REPLACE(s, U&'\\0000', 'x')", "REPLACE(s, \"\\u0000\", \"x\")", Fixed: [Search with { Band = JsonCharacterBand.Nul }]),
            new("REPLACE", null, "REPLACE(s, 'a', U&'\\0000')", "REPLACE(s, \"a\", \"\\u0000\")", Fixed: [Replacement with { Band = JsonCharacterBand.Nul }]),
            new("REPLACE", null, "REPLACE(s, 'a', U&'\\FDD0')", "REPLACE(s, \"a\", \"\\uFDD0\")", Fixed: [Replacement with { Band = JsonCharacterBand.HighBmp }]),
            new("SUBSTRING", null, "SUBSTRING(s FROM 2 FOR 0)", "SUBSTRING(s, 1, 0)", Fixed: [Key("SUBSTRING", 2, CosmosValueClass.Number, JsonCharacterBand.None, FunctionValueClass.Zero)]),
            new("SUBSTRING", null, "SUBSTRING(s FROM 2 FOR -1)", "SUBSTRING(s, 1, -1)", Fixed: [Key("SUBSTRING", 2, CosmosValueClass.Number, JsonCharacterBand.None, FunctionValueClass.Negative)]),
            new("||", null, "s || U&'\\0000'", "CONCAT(s, \"\\u0000\")", Fixed: [Joined("||", JsonCharacterBand.Nul)]),
            new("CONCAT", null, "CONCAT(s, U&'\\0000')", "CONCAT(s, \"\\u0000\")", Fixed: [Joined("CONCAT", JsonCharacterBand.Nul)]),
            new(StringCalls.NullAsEmpty, null, "CONCAT(s, U&'\\0000')", "CONCAT(s, \"\\u0000\")", "postgresql", [Joined(StringCalls.NullAsEmpty, JsonCharacterBand.Nul)]),
            new(StringCalls.NullAsEmpty, null, "CONCAT(s, U&'\\0000')", "CONCAT(s, \"\\u0000\")", "oracle", [Joined(StringCalls.NullAsEmpty, JsonCharacterBand.Nul)]),
            new("||", null, "s || U&'\\FDD0' || '1'", "CONCAT(s, \"\\uFDD0\", \"1\")", Fixed: [Joined("||", JsonCharacterBand.HighBmp)]),
            new("CONCAT", null, "CONCAT(s, U&'\\FDD0', '1')", "CONCAT(s, \"\\uFDD0\", \"1\")", Fixed: [Joined("CONCAT", JsonCharacterBand.HighBmp)]),
            new(StringCalls.NullAsEmpty, null, "CONCAT(s, U&'\\FDD0', '1')", "CONCAT(s, \"\\uFDD0\", \"1\")", "postgresql", [Joined(StringCalls.NullAsEmpty, JsonCharacterBand.HighBmp)]),
            new(StringCalls.NullAsEmpty, null, "CONCAT(CONCAT(s, U&'\\FDD0'), '1')", "CONCAT(s, \"\\uFDD0\", \"1\")", "oracle", [Joined(StringCalls.NullAsEmpty, JsonCharacterBand.HighBmp)]),
            new("REPEAT", null, "REPEAT(U&'\\0000', 2)", "REPLICATE(\"\\u0000\", 2)", Fixed: [Key("REPEAT", FunctionKey.Literal, CosmosValueClass.String, JsonCharacterBand.Nul, FunctionValueClass.NonEmpty)]),
            new("REPEAT", null, "REPEAT(U&'\\FDD0' || '0', 2)", "REPLICATE(CONCAT(\"\\uFDD0\", \"0\"), 2)", Fixed: [Key("REPEAT", 0, CosmosValueClass.String, JsonCharacterBand.HighBmp, FunctionValueClass.NonEmpty)]),
        ];

        static FunctionKey Key(string function, long ordinal, CosmosValueClass kind, JsonCharacterBand band, FunctionValueClass value) => FunctionKey.Of(function, ordinal, kind, band, value);

        /// <summary>The search <c>REPLACE</c>'s first spellings hold, a <c>0</c> or a <c>1</c>, and their replacement, <c>x</c>: strings of no band, not empty.</summary>
        static FunctionKey Search => Key("REPLACE", 1, CosmosValueClass.String, JsonCharacterBand.None, FunctionValueClass.NonEmpty);

        static FunctionKey Replacement => Search with { Ordinal = 2 };

        /// <summary>A concatenation's literal of one band, keyed apart from a stored operand (<see cref="FunctionKey.Literal"/>).</summary>
        static FunctionKey Joined(string function, JsonCharacterBand band) => Key(function, FunctionKey.Literal, CosmosValueClass.String, band, FunctionValueClass.NonEmpty);

        /// <summary>The needle <c>POSITION</c>'s spellings hold, a <c>0</c> or a <c>1</c>: a string of no band, not empty.</summary>
        static FunctionKey Needle => Key("POSITION", 0, CosmosValueClass.String, JsonCharacterBand.None, FunctionValueClass.NonEmpty);

        /// <summary>
        /// The witnesses of each key: three strings of each band, holding that band's characters and ASCII alone, at either
        /// end and inside, beside a <c>0</c> or a <c>1</c>; and a value of each other kind.
        /// </summary>
        internal static readonly (CosmosValueClass Kind, JsonCharacterBand Band, string?[] Witnesses)[] FunctionWitnesses =
        [
            (CosmosValueClass.String, JsonCharacterBand.None, [Json("a0b1c"), Json("b"), Json("")]),
            (CosmosValueClass.String, JsonCharacterBand.Surrogates, [Json("a\U0001F600b0\U0001F600"), Json("\U0001F600"), Json("\U0001F600x1")]),
            (CosmosValueClass.String, JsonCharacterBand.HighBmp, [Json("ab0ﬀ"), Json("﷐b1﷐"), Json("ﬀ")]),
            (CosmosValueClass.String, JsonCharacterBand.NonAscii, [Json("aéb0ß"), Json("İb1"), Json("ß")]),
            (CosmosValueClass.String, JsonCharacterBand.Trimmed, [Json("\tab0\u000B"), Json("\u000Cb1\r"), Json("\n")]),
            (CosmosValueClass.String, JsonCharacterBand.Nul, [Json("a\u0000b0\u0000"), Json("\u0000"), Json("\u00001")]),
            (CosmosValueClass.Number, JsonCharacterBand.None, ["30", "0.5"]),
            (CosmosValueClass.True, JsonCharacterBand.None, ["true"]),
            (CosmosValueClass.False, JsonCharacterBand.None, ["false"]),
            (CosmosValueClass.Null, JsonCharacterBand.None, ["null"]),
            (CosmosValueClass.Absent, JsonCharacterBand.None, [null]),
            (CosmosValueClass.Object, JsonCharacterBand.None, ["{\"x\":\"a\"}"]),
            (CosmosValueClass.Array, JsonCharacterBand.None, ["[\"a\"]"]),
        ];

        static string Json(string text) => Newtonsoft.Json.JsonConvert.ToString(text);

        /// <summary>
        /// Each string function over each band of a string's characters and each other kind of value answers against
        /// Calcite's as <c>Service.StringFunctionAnswersByBand</c> records, or better (<see cref="CosmosFunctionComparison"/>):
        /// Calcite's side reads the value as the text accessor does, in process.
        /// </summary>
        [Fact, Apache.Calcite.Cosmos.Adapter.Tests.Sql.CosmosPins("Service.StringFunctionAnswersByBand")]
        public async Task EachStringFunctionAnswersItsRowByBand()
        {
            var measured = await CosmosFunctionComparison.Measure(Strings_(), _unavailable, "Service.StringFunctionAnswersByBand", Functions, FunctionWitnesses);
            CosmosFunctionComparison.Unsound(measured, CosmosAxioms.Get("Service.StringFunctionAnswersByBand").FunctionOutcomes!, CosmosJsonTypeMeasurementTests.Answering).Should().BeEmpty();
        }

        static string Spell(string text) => string.Concat(text.EnumerateRunes().Select(r => r.Value is >= 0x21 and <= 0x7E ? ((char)r.Value).ToString() : $"<U+{r.Value:X4}>"));

    }

}
