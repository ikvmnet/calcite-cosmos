using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// The in-process bodies of the <c>IS_*</c> tests against the service, over the kinds the registry's outcomes are
    /// witnessed with: what <c>Sql.CosmosFunctionBodiesTests</c> asks of a real account, asked of whatever account the
    /// suite resolves (<see cref="CosmosEmulator"/>).
    /// </summary>
    /// <remarks>
    /// A body answers over the document text the query returns, as there, so neither side is handed a tidied input. The
    /// database is the test's own, and is deleted after.
    /// </remarks>
    public class CosmosFunctionBodiesMeasurementTests
    {

        static readonly (string Name, Func<string, string, bool> Body)[] Tests =
        [
            ("IS_DEFINED", CosmosFunctionBodies.IsDefined),
            ("IS_NULL", CosmosFunctionBodies.IsNull),
            ("IS_STRING", CosmosFunctionBodies.IsString),
            ("IS_NUMBER", CosmosFunctionBodies.IsNumber),
            ("IS_BOOL", CosmosFunctionBodies.IsBool),
            ("IS_ARRAY", CosmosFunctionBodies.IsArray),
            ("IS_OBJECT", CosmosFunctionBodies.IsObject),
            ("IS_PRIMITIVE", CosmosFunctionBodies.IsPrimitive),
        ];

        [Fact]
        public async Task TheBodiesAnswerWhatTheServiceAnswersOverEveryKind()
        {
            var (endpoint, key) = (CosmosEmulator.Endpoint, CosmosEmulator.Key);
            if (endpoint is null || key is null)
                Assert.Skip("This needs a service.");

            var options = new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway };
            if (CosmosEmulator.IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            var cancellation = TestContext.Current.CancellationToken;
            using var client = new CosmosClient(endpoint!, key!, options);
            var database = (await client.CreateDatabaseIfNotExistsAsync("calcite_cosmos_bodies_" + Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_"), cancellationToken: cancellation)).Database;
            try
            {
                var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("kinds", "/pk"), cancellationToken: cancellation)).Container;
                var witnesses = CosmosOutcomeWitnesses.KindCells.SelectMany(kind => CosmosOutcomeWitnesses.Of(CosmosValueClass.None, kind)).ToList();
                foreach (var (json, i) in witnesses.Select((json, i) => (json, i)))
                    using (var response = await container.CreateItemStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes($"{{\"id\":\"w{i}\",\"pk\":\"a\"" + (json is null ? "" : ",\"v\":" + json) + "}")), new PartitionKey("a"), cancellationToken: cancellation))
                        response.EnsureSuccessStatusCode();

                // A test the service answers as undefined is absent from the row, which reads as false here as there.
                var sql = "SELECT c AS doc, " + string.Join(", ", Tests.Select(t => $"{t.Name}(c.v) AS {t.Name}")) + " FROM c";
                var disagreements = new List<string>();
                var seen = 0;
                using var iterator = container.GetItemQueryStreamIterator(new QueryDefinition(sql), requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("a") });
                while (iterator.HasMoreResults)
                {
                    using var page = await iterator.ReadNextAsync(cancellation);
                    page.EnsureSuccessStatusCode();
                    using var rows = await JsonDocument.ParseAsync(page.Content, cancellationToken: cancellation);
                    foreach (var row in rows.RootElement.GetProperty("Documents").EnumerateArray())
                    {
                        seen++;
                        var document = row.GetProperty("doc").GetRawText();
                        foreach (var (name, body) in Tests)
                            if ((row.TryGetProperty(name, out var answer) && answer.ValueKind == JsonValueKind.True) != body(document, "$.v"))
                                disagreements.Add($"{name} over {document}");
                    }
                }

                seen.Should().Be(witnesses.Count);
                disagreements.Should().BeEmpty("the bodies answer what the service answers");
            }
            finally
            {
                await database.DeleteAsync(cancellationToken: cancellation);
            }
        }

    }

}
