using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// What the service reports of a container's usage to a read that asks for it, asked by the SDK alone: so that a test
    /// of what the adapter reads of it skips on what the service answered, not on what the adapter made of it.
    /// </summary>
    internal static class ContainerUsage
    {

        /// <summary>
        /// The <c>x-ms-resource-usage</c> header a container read asking for quota info is answered with, or <c>null</c>
        /// where none comes.
        /// </summary>
        public static async Task<string?> ReadAsync(Container container, CancellationToken cancellationToken)
        {
            using var response = await container.ReadContainerStreamAsync(new ContainerRequestOptions { PopulateQuotaInfo = true }, cancellationToken);
            response.EnsureSuccessStatusCode();

            return response.Headers.GetValueOrDefault("x-ms-resource-usage") is { Length: > 0 } usage ? usage : null;
        }

        /// <summary>The <c>documentsCount</c> a usage header names, or <c>null</c> where there is no header or it names none.</summary>
        public static long? DocumentsCount(string? usage) =>
            (usage ?? "").Split(';')
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0].Trim() == "documentsCount")
                .Select(pair => (long?)long.Parse(pair[1], CultureInfo.InvariantCulture))
                .FirstOrDefault();

    }

}
