using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts.Rendering;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure
{

    /// <summary>
    /// A request that reads whole documents, <c>SELECT VALUE { "DOC": c, … } FROM items c</c> with or without a <c>WHERE</c>,
    /// answered over documents as the service answers it: the <c>WHERE</c> read by <see cref="CosmosSqlFormReader"/> and
    /// evaluated on each document by <see cref="CosmosEval"/>, which <c>CosmosEvalServiceTests</c> holds to the service, and
    /// the projection's paths read off each document kept.
    /// </summary>
    /// <remarks>
    /// Anything else is refused, loudly: another statement shape, a point read or a partition's request, a projection of
    /// anything but a path, a <c>WHERE</c> the reader does not read back as sent or reads any part of as opaque, and a
    /// document on which no row says what the service makes of it.
    /// </remarks>
    internal static partial class DocumentRequests
    {

        /// <summary>Answers a request over documents, each a JSON object, on a target: each row the service returns, as JSON.</summary>
        public static List<string> Answer(CosmosQuery query, PartitionKey? partitionKey, IEnumerable<string> documents, CosmosServiceTarget target)
        {
            if (query.PointReadId is not null || query.PointReadIds is not null || partitionKey is not null || query.PartitionKeyValues is { Count: > 0 } || query.Order is not null)
                throw Refused(query, "a point read, a partition's request or an ordered one");

            var shape = Shape().Match(query.Sql);
            if (shape.Success == false)
                throw Refused(query, "not a read of whole documents");

            var projected = Projection(shape.Groups["items"].Value) ?? throw Refused(query, "a projection of anything but paths");
            var where = shape.Groups["where"].Success ? Where(query, shape.Groups["where"].Value) : null;
            var on = target == CosmosServiceTarget.Emulator ? RenderingTarget.Emulator : RenderingTarget.Azure;

            var rows = new List<string>();
            foreach (var json in documents)
            {
                if (where is not null)
                {
                    var document = (J.Obj)CosmosEvalServiceTests.Fixture.Read(JsonDocument.Parse(json).RootElement)!;
                    switch (CosmosEval.On(where, document, on))
                    {
                        case ServiceOutcomes.True:
                            break;
                        case ServiceOutcomes.False or ServiceOutcomes.Undefined:
                            continue;
                        case var other:
                            throw Refused(query, $"no row says what the service makes of it on {json} ({other?.ToString() ?? "nothing"})");
                    }
                }

                var node = JsonNode.Parse(json)!;
                var row = new JsonObject();
                foreach (var (name, path) in projected)
                    if (At(node, path) is (true, var value))
                        row[name] = value?.DeepClone();

                rows.Add(row.ToJsonString());
            }

            return rows;
        }

        /// <summary>Reads a <c>WHERE</c> as the service's form, refusing one read back otherwise than as sent or holding an opaque part.</summary>
        static ServiceForm Where(CosmosQuery query, string text)
        {
            var read = CosmosSqlFormReader.Read(text, query.Parameters);
            if (read.Written != text)
                throw Refused(query, $"its WHERE read back as {read.Written}");

            if (read.Form.Subforms.Any(f => f.Kind == ServiceFormKind.Opaque))
                throw Refused(query, "a part of its WHERE no row reads");

            return read.Form;
        }

        /// <summary>The projection's names and the paths they read, or <c>null</c> where an item is anything but a path.</summary>
        static List<(string Name, List<object> Path)>? Projection(string items)
        {
            var projected = new List<(string, List<object>)>();
            var at = 0;
            foreach (Match item in Item().Matches(items))
            {
                if (item.Index != at)
                    return null;

                var path = new List<object>();
                foreach (Match step in Step().Matches(item.Groups["path"].Value))
                    path.Add(step.Groups["name"].Success ? step.Groups["name"].Value
                        : step.Groups["quoted"].Success ? JsonSerializer.Deserialize<string>(step.Groups["quoted"].Value)!
                        : int.Parse(step.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture));

                projected.Add((JsonSerializer.Deserialize<string>(item.Groups["name"].Value)!, path));
                at = item.Index + item.Length;
            }

            return at == items.Length && projected.Count > 0 ? projected : null;
        }

        /// <summary>The value at a path, or not found where a step reads nothing, as the service leaves an undefined property out.</summary>
        static (bool Found, JsonNode? Value) At(JsonNode document, List<object> path)
        {
            var at = document;
            foreach (var step in path)
            {
                switch (step, at)
                {
                    case (string name, JsonObject o) when o.TryGetPropertyValue(name, out var child):
                        at = child;
                        break;
                    case (int index, JsonArray a) when index < a.Count:
                        at = a[index];
                        break;
                    default:
                        return (false, null);
                }
            }

            return (true, at);
        }

        /// <summary>Whether an exception, or one it wraps, is this evaluator's refusal, which a test reports rather than reads as a run's failure.</summary>
        public static bool IsRefusal(Exception? e)
        {
            for (; e is not null; e = e.InnerException)
                if (e.Data.Contains(typeof(DocumentRequests)))
                    return true;

            return false;
        }

        static InvalidOperationException Refused(CosmosQuery query, string why)
        {
            var refused = new InvalidOperationException($"Not answered over documents, {why}: {query.Sql} | " + string.Join(", ", query.Parameters.Select(p => $"{p.Name}={p.Value}")));
            refused.Data[typeof(DocumentRequests)] = true;
            return refused;
        }

        [GeneratedRegex("""^SELECT VALUE \{ (?<items>.+) \} FROM [A-Za-z_][A-Za-z0-9_]* c(?: WHERE (?<where>.+))?$""", RegexOptions.Singleline)]
        private static partial Regex Shape();

        [GeneratedRegex("""(?:, )?(?<name>"(?:[^"\\]|\\.)*"): (?<path>c(?:\.[A-Za-z_][A-Za-z0-9_]*|\["(?:[^"\\]|\\.)*"\]|\[[0-9]+\])*)(?=, "|$)""")]
        private static partial Regex Item();

        [GeneratedRegex("""\.(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[(?<quoted>"(?:[^"\\]|\\.)*")\]|\[(?<index>[0-9]+)\]""")]
        private static partial Regex Step();

    }

}
