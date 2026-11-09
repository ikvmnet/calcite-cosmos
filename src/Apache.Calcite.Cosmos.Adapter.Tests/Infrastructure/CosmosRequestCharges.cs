using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.Cosmos;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// What the service charged a fixture's client, in request units, by the test that sent each request, its setup and
    /// cleanup apart: written to <c>TestResults/measurements/charges.tsv</c> when the fixture ends, which is what a run on
    /// an account is held to beside its estimate (spec §4.8). Every response is counted, a failed one and a database's
    /// creation and deletion among them. Once the charges pass <paramref name="cap"/>, every request but a delete is
    /// refused before it is sent, so a run on an account stops within its budget and still deletes what it made.
    /// </summary>
    internal sealed class CosmosRequestCharges(string fixture, double cap = double.PositiveInfinity) : RequestHandler
    {

        static readonly object Log = new();

        readonly ConcurrentDictionary<string, double> _charged = new();

        public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            if (Total > cap && request.Method != System.Net.Http.HttpMethod.Delete)
                throw new System.InvalidOperationException($"{fixture} has been charged {Total:F2} RU, past its cap of {cap:F2}.");

            var response = await base.SendAsync(request, cancellationToken);
            _charged.AddOrUpdate(TestContext.Current.Test?.TestDisplayName ?? "(setup and cleanup)", response.Headers.RequestCharge, (_, sum) => sum + response.Headers.RequestCharge);
            return response;
        }

        /// <summary>Everything charged so far.</summary>
        public double Total => _charged.Values.Sum();

        /// <summary>Appends a line a test and one for the fixture's total.</summary>
        public void Write()
        {
            lock (Log)
            {
                var path = Path.GetFullPath(Path.Combine("TestResults", "measurements", "charges.tsv"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, _charged.OrderBy(c => c.Key, System.StringComparer.Ordinal).Select(c => $"{fixture}\t{c.Key}\t{c.Value:F2}")
                    .Append($"{fixture}\t(total)\t{Total:F2}"));
            }
        }

    }

}
