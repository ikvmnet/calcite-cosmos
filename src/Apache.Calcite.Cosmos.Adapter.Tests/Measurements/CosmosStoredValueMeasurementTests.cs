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

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Data;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What the service stores of an integer outside int64 and of text that is not UTF-8, measured, and what the adapter
    /// reads of each: the facts <see cref="CosmosJson"/>'s readings rest on, each a row of <c>CosmosAxioms</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Azure's facts, and the emulator's answers detected.</b> Azure stores an integer written outside int64 as a
    /// double, and every query prints that double one way; the emulator keeps one written in [2^63, 2^64) as it was
    /// written and prints it to 17 digits where a statement projects it. Azure stores whatever bytes a client writes in a
    /// document body; the emulator refuses a document holding bytes that are not UTF-8, with a 403, or a 400 on the image
    /// CI runs. Each test detects the emulator's answer and skips rather than asking which endpoint answered, so that an
    /// emulator that starts behaving as Azure does makes it assert. Measured on the probe account on 2026-10-19
    /// (<c>reports/verify-bytes.md</c>).
    /// </para>
    /// <para>
    /// <b>What the adapter reads is asked through a connection</b> — a model document naming the two containers, the
    /// route a caller takes — so what is asserted is the error a caller receives, not the one a planner a test assembled
    /// would. <c>CosmosJsonTests</c> and <c>CosmosToClrCursorConverterTests</c> hold the same readings to canned bytes,
    /// which run anywhere.
    /// </para>
    /// <para>
    /// One partition and two containers, sharing the database's 400 RU/s; the database is deleted when the class ends.
    /// </para>
    /// </remarks>
    public class CosmosStoredValueMeasurementTests : IClassFixture<CosmosStoredValueMeasurementTests.Fixture>
    {

        /// <summary>
        /// The class's one-time setup and teardown.
        /// </summary>
        public sealed class Fixture : IAsyncLifetime
        {

            public async ValueTask InitializeAsync() => await Initialize();

            public ValueTask DisposeAsync() { Cleanup(); return default; }

        }

        static readonly string DatabaseName = "calcite_cosmos_stored_" +
            Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>
        /// The integers written outside int64, by id, and the last int64 beside them.
        /// </summary>
        static readonly (string Id, string Written)[] Wide =
        [
            ("p63", "9223372036854775808"),
            ("p63p1", "9223372036854775809"),
            ("u64max", "18446744073709551615"),
            ("m63m1", "-9223372036854775809"),
        ];

        /// <summary>
        /// Strings written as raw bytes that are not UTF-8, by id, at <c>s</c>: a WTF-8 surrogate of either half, a
        /// CESU-8 pair, a byte no sequence begins with, an overlong form and a lone continuation byte.
        /// </summary>
        static readonly (string Id, byte[] Text)[] Malformed =
        [
            ("raw", [(byte)'x', 0xED, 0xA0, 0x80, (byte)'y']),
            ("rawlow", [0xED, 0xB0, 0x80]),
            ("cesu", [0xED, 0xA0, 0xBD, 0xED, 0xB8, 0x80]),
            ("ff", [(byte)'x', 0xFF, (byte)'y']),
            ("overlong", [0xC0, 0xAF]),
            ("cont", [0x80]),
        ];

        static CosmosClient? _client;
        static Database? _database;
        static string? _unavailable;

        /// <summary>
        /// The status the service refused the malformed documents with, or <c>null</c> where it stored them.
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

                var numbers = await Fresh("numbers", cts.Token);
                foreach (var (id, written) in Wide.Append(("p63m1", "9223372036854775807")))
                    (await Write(numbers, Encoding.UTF8.GetBytes($$"""{"id":"{{id}}","pk":"a","v":{{written}}}"""), cts.Token)).Should().BeTrue();

                var bytes = await Fresh("bytes", cts.Token);
                (await Write(bytes, Encoding.UTF8.GetBytes("""{"id":"good","pk":"a","n":1,"s":"ok"}"""), cts.Token)).Should().BeTrue();

                // The prefix ends with the quote that opens the value: a raw literal closes at the first three quotes, which
                // took that one, and the body sent was `"s":x…`, which Azure refuses as an invalid payload.
                var malformed = Malformed.Select(m => (m.Id, Document: Concat("{\"id\":\"" + m.Id + "\",\"pk\":\"a\",\"n\":5,\"s\":\"", m.Text, "\"}")))
                    .Append(("nested", Concat("""{"id":"nested","pk":"a","n":5,"o":{"t":"x""", [0xED, 0xA0, 0x80], "y\"}}")))
                    .Append(("badkey", Concat("""{"id":"badkey","pk":"a","n":5,"o":{"k""", [0xFF], "\":1}}")));

                foreach (var (_, document) in malformed)
                    if (await Write(bytes, document, cts.Token) == false)
                        break;
            }
            catch (Exception e)
            {
                _unavailable = e.Message;
                _database = null;
            }
        }

        static byte[] Concat(string before, byte[] bytes, string after) =>
            [.. Encoding.UTF8.GetBytes(before), .. bytes, .. Encoding.UTF8.GetBytes(after)];

        static async Task<Container> Fresh(string name, CancellationToken cancellation)
        {
            try { await _database!.GetContainer(name).DeleteContainerAsync(cancellationToken: cancellation); } catch (CosmosException) { }
            return (await _database!.CreateContainerIfNotExistsAsync(new ContainerProperties(name, "/pk"), cancellationToken: cancellation)).Container;
        }

        /// <summary>
        /// Writes a document as the bytes given, and answers whether the service stored it: an emulator's refusal of the
        /// bytes is recorded rather than thrown, since that is a measurement too.
        /// </summary>
        /// <remarks>
        /// Only the emulator's refusal: the vnext-preview image answers 403 and the one CI runs 400, both "Failed to parse
        /// Json request". Any other refusal is a document the test wrote wrongly, and fails the fixture rather than skipping
        /// every test that reads it — Azure's "The request payload is invalid" was once taken for the emulator's, and both
        /// tests of what Azure stores skipped on Azure.
        /// </remarks>
        static async Task<bool> Write(Container container, byte[] document, CancellationToken cancellation)
        {
            using var stream = new MemoryStream(document);
            using var response = await container.CreateItemStreamAsync(stream, new PartitionKey("a"), cancellationToken: cancellation);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest && response.ErrorMessage?.Contains("Failed to parse Json request", StringComparison.Ordinal) == true)
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

        static Container Container(string name)
        {
            if (_database is null)
                Assert.Skip("These need a service. " + _unavailable);

            return _database!.GetContainer(name);
        }

        static void RequireMalformedStored()
        {
            if (_refused is HttpStatusCode refused)
                Assert.Skip($"This service refuses a document holding bytes that are not UTF-8 ({(int)refused}), as an emulator does; Azure stores them as written.");
        }

        /// <summary>
        /// Runs a statement and answers each value the service returned, as it sent it: the bytes, which a decoded
        /// string could not carry, beside the value.
        /// </summary>
        static async Task<List<(byte[] Raw, JsonElement Value)>> Values(Container container, string sql)
        {
            var values = new List<(byte[], JsonElement)>();

            using var iterator = container.GetItemQueryStreamIterator(new QueryDefinition(sql), requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("a") });
            while (iterator.HasMoreResults)
            {
                using var response = await iterator.ReadNextAsync(TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();

                using var page = await JsonDocument.ParseAsync(response.Content, cancellationToken: TestContext.Current.CancellationToken);
                foreach (var value in page.RootElement.GetProperty("Documents").EnumerateArray())
                    values.Add((System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value).ToArray(), value.Clone()));
            }

            return values;
        }

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
                "containers": [ "numbers", "bytes" ],
                "connectionMode": "gateway"
              }
            }
          ]
        }
        """;

        /// <summary>
        /// Runs a statement through a connection and answers its first column as text, sorted.
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

            var values = new List<string>();

            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                values.Add(reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "");

            values.Sort(StringComparer.Ordinal);
            return values;
        }

        // ── Numbers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// An integer written outside int64 is stored as a double, which every query prints one way, the document read
        /// whole and the value projected alike.
        /// </summary>
        /// <remarks>
        /// What <c>CosmosJson.GetInteger</c>'s bound is for: <c>9223372036854775808</c> written comes back
        /// <c>9.223372036854776E+18</c>, the double 2^63, which is no <c>BIGINT</c>. The emulator keeps an integer written
        /// in [2^63, 2^64) as written, in a document read whole, and prints it to 17 digits where a statement projects it,
        /// so there the test skips.
        /// </remarks>
        [Fact]
        public async Task AnIntegerOutsideLongIsStoredAsADouble()
        {
            var numbers = Container("numbers");

            var whole = (await Values(numbers, "SELECT VALUE c FROM c")).ToDictionary(v => v.Value.GetProperty("id").GetString()!, v => v.Value.GetProperty("v"));
            var projected = (await Values(numbers, "SELECT VALUE { \"id\": c.id, \"v\": c.v } FROM c")).ToDictionary(v => v.Value.GetProperty("id").GetString()!, v => v.Value.GetProperty("v"));

            if (whole["p63"].GetRawText() == "9223372036854775808")
                Assert.Skip($"This service keeps an integer written past int64 as it was written, and prints it projected as {projected["p63"].GetRawText()}, as the emulator does; Azure stores it as a double.");

            foreach (var (id, written) in Wide)
            {
                projected[id].GetRawText().Should().Be(whole[id].GetRawText(), $"{written} is printed one way, projected or read whole");
                whole[id].TryGetInt64(out _).Should().BeFalse($"{written} is not stored as the integer written, but as {whole[id].GetRawText()}");
                whole[id].GetDouble().Should().Be(double.Parse(written, System.Globalization.CultureInfo.InvariantCulture), $"{written} is stored as the double nearest it");
            }

            whole["p63m1"].GetRawText().Should().Be("9223372036854775807", "an integer within int64 is stored as written");
        }

        /// <summary>
        /// A <c>BIGINT</c> read of a double at or past 2^63 is refused, where it answered 9223372036854775807; the least
        /// int64, which a double holds exactly, reads.
        /// </summary>
        /// <remarks>
        /// The statement pushes, so the reading is <c>CosmosJson.GetInteger</c>'s; in process Calcite raises over the
        /// double. The emulator prints 2^63 projected as <c>9223372036854775800</c>, which is int64 text that no reader can
        /// tell from a stored 9223372036854775800, so there the refusal is not asked.
        /// </remarks>
        [Fact]
        public async Task ABigintReadOfADoubleOutsideLongIsRefused()
        {
            var numbers = Container("numbers");

            (await QueryAsync("""SELECT JSON_VALUE(c."DOC", '$.v' RETURNING BIGINT) FROM "numbers" AS c WHERE c."id" = 'm63m1'"""))
                .Should().Equal("-9223372036854775808");

            var printed = (await Values(numbers, "SELECT VALUE c.v FROM c WHERE c.id = 'p63'")).Single().Value;
            if (printed.TryGetInt64(out var integral))
                Assert.Skip($"This service prints 2^63 projected as the integer {integral}, as the emulator does: int64 text, which no reader can tell from a stored integer.");

            foreach (var id in new[] { "p63", "p63p1", "u64max" })
            {
                var act = () => QueryAsync($"""SELECT JSON_VALUE(c."DOC", '$.v' RETURNING BIGINT) FROM "numbers" AS c WHERE c."id" = '{id}'""");
                await act.Should().ThrowAsync<CosmosMaterializationException>().WithMessage("A JSON number of '*' is outside the range of BIGINT.", id);
            }
        }

        // ── Text that is not UTF-8 ───────────────────────────────────────────────────────────

        /// <summary>
        /// Bytes that are not UTF-8 are stored as a client wrote them, in a value and in a name, returned as written by a
        /// query and a point read, and compared as bytes: the raw document is not the one U+FFFD would make of it.
        /// </summary>
        /// <remarks>
        /// What <c>CosmosJson.Undecodable</c> refuses, and why no reading of them is right: the service validates nothing
        /// about UTF-8 in a document body, so any sequence reaches the reader, not only a surrogate. A lone surrogate
        /// written as a JSON escape is another matter, stored as U+FFFD
        /// (<c>CosmosServiceOrderMeasurementTests.ALoneSurrogateIsStoredAsTheReplacementCharacter</c>).
        /// </remarks>
        [Fact]
        public async Task MalformedTextIsStoredAsWritten()
        {
            var bytes = Container("bytes");
            RequireMalformedStored();

            foreach (var (id, text) in Malformed)
            {
                byte[] quoted = [(byte)'"', .. text, (byte)'"'];

                (await Values(bytes, $"SELECT VALUE c.s FROM c WHERE c.id = '{id}'")).Single().Raw.Should().Equal(quoted, $"{id} is returned by a query as written");

                using var read = await bytes.ReadItemStreamAsync(id, new PartitionKey("a"), cancellationToken: TestContext.Current.CancellationToken);
                read.EnsureSuccessStatusCode();

                using var document = await JsonDocument.ParseAsync(read.Content, cancellationToken: TestContext.Current.CancellationToken);
                System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(document.RootElement.GetProperty("s")).ToArray().Should().Equal(quoted, $"{id} is returned by a point read as written");
            }

            var nested = (await Values(bytes, "SELECT VALUE c.o FROM c WHERE c.id = 'nested'")).Single().Value;
            System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(nested.GetProperty("t")).ToArray().Should().Equal(new byte[] { (byte)'"', (byte)'x', 0xED, 0xA0, 0x80, (byte)'y', (byte)'"' });

            var named = (await Values(bytes, "SELECT VALUE c.o FROM c WHERE c.id = 'badkey'")).Single().Value;
            System.Runtime.InteropServices.JsonMarshal.GetRawUtf8PropertyName(named.EnumerateObject().Single()).ToArray().Should().Equal(new byte[] { (byte)'k', 0xFF });

            var replaced = "x" + (char)0xFFFD + "y";
            (await Values(bytes, $"SELECT VALUE c.id FROM c WHERE c.s = \"{replaced}\"")).Should().BeEmpty("the bytes are compared as they are, not as U+FFFD");
        }

        /// <summary>
        /// A document holding text that is not UTF-8 fails a statement exactly where its rows carry the bytes into a
        /// reading, and names the column, the document and the bytes; a statement whose rows leave them out answers, in the
        /// same logical partition.
        /// </summary>
        /// <remarks>
        /// Through a connection, which is where the decoder's own error used to arrive unnamed and mid-stream; and a
        /// projected <c>JSON_QUERY</c>, which answered U+FFFD without a word, refuses as the rest do
        /// (<c>reports/verify-bytes.md</c>, L2, L7, L11 and L15).
        /// </remarks>
        [Fact]
        public async Task ADocumentHoldingMalformedTextFailsByNameWhereItIsRead()
        {
            Container("bytes");
            RequireMalformedStored();

            (await QueryAsync("""SELECT c."id" FROM "bytes" AS c""")).Should().HaveCount(Malformed.Length + 3, "a statement whose rows leave the value out answers");

            var projected = () => QueryAsync("""SELECT JSON_VALUE(c."DOC", '$.s') AS "s", c."id" FROM "bytes" AS c WHERE c."id" = 'raw'""");
            await projected.Should().ThrowAsync<CosmosMaterializationException>()
                .WithMessage("""Column 's' of document "raw" holds text that is not UTF-8 at $: the bytes ED A0 80 in "x\xED\xA0\x80y".*""");

            var whole = () => QueryAsync("""SELECT * FROM "bytes" AS c WHERE c."id" = 'nested'""");
            await whole.Should().ThrowAsync<CosmosMaterializationException>()
                .WithMessage("""Column 'DOC' of document "nested" holds text that is not UTF-8 at $.o.t: the bytes ED A0 80 in "x\xED\xA0\x80y".*""");

            var fragment = () => QueryAsync("""SELECT JSON_QUERY(c."DOC", '$.o') AS "o", c."id" FROM "bytes" AS c WHERE c."id" = 'badkey'""");
            await fragment.Should().ThrowAsync<CosmosMaterializationException>()
                .WithMessage("""Column 'o' of document "badkey" holds text that is not UTF-8 at $, in a property name: the bytes FF in "k\xFF".*""");
        }

    }

}
