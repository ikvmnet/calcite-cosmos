using System.Collections.Concurrent;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// In-process bodies for the Cosmos type tests, so a predicate naming one can be evaluated outside
    /// the Cosmos convention instead of pinning the whole statement to the service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these asks what <em>kind</em> of thing a document holds at a path, and the answer
    /// turns on two distinctions SQL does not have: absent against null, and a scalar against a
    /// structure. Both survive in the row model — the document column carries the whole document as
    /// text — and both are lost the moment a value is extracted through <c>JSON_VALUE</c>, which answers
    /// NULL for a missing path, for a JSON null, and for anything that is not a scalar alike.
    /// </para>
    /// <para>
    /// <b>Why these are written here rather than assembled out of Calcite's SQL/JSON functions.</b>
    /// Calcite carries the whole family and can run all of it in process, so <c>IS_DEFINED</c> is
    /// <c>JSON_EXISTS</c> and the type tests look like <c>JSON_TYPE(…) = 'STRING'</c>. But
    /// <c>JSON_TYPE</c> takes a JSON <em>value</em> and has no path form — Calcite offers
    /// <c>jsonLength(json, path)</c> and <c>jsonKeys(json, path)</c> and pointedly no
    /// <c>jsonType(json, path)</c> — so asking the type at a path means
    /// <c>JSON_TYPE(JSON_QUERY(doc, path))</c>, which parses the document, navigates to the value,
    /// serialises that value back to text, and parses it again. Two of those three steps exist only to
    /// get around the missing overload. Reading the kind off the parsed element costs one parse.
    /// </para>
    /// <para>
    /// The path is parsed by <see cref="CosmosRexTranslator.TryExtendByJsonPath"/> and the document
    /// walked by <see cref="CosmosDocument.TryRead(JsonElement, CosmosPath, out JsonElement)"/> — the
    /// same grammar and the same walker the rendered statement is built from, so what is evaluated here
    /// and what the service would have evaluated cannot drift apart by being written twice.
    /// </para>
    /// <para>
    /// A malformed document or an unparseable path answers <c>false</c> rather than throwing. These are
    /// predicates over documents the service has already accepted, and a row that cannot be read is one
    /// the predicate does not select; raising here would fail a statement that Cosmos would have
    /// answered.
    /// </para>
    /// </remarks>
    public static class CosmosFunctionBodies
    {

        /// <summary>
        /// Parsed paths, keyed by the literal they came from.
        /// </summary>
        /// <remarks>
        /// A predicate is evaluated once per row and the path is the same every time, so parsing it per
        /// row is the one cost here worth removing. The document cannot be cached the same way; it is
        /// different on every row, and parsing it is the irreducible part.
        /// </remarks>
        static readonly ConcurrentDictionary<string, CosmosPath?> Paths = new();

        /// <summary>Determines whether the document holds anything at the path, null included.</summary>
        public static bool IsDefined(string? document, string? path) => Kind(document, path) != JsonValueKind.Undefined;

        /// <summary>Determines whether the document holds a JSON null at the path.</summary>
        public static bool IsNull(string? document, string? path) => Kind(document, path) == JsonValueKind.Null;

        /// <summary>Determines whether the document holds a string at the path.</summary>
        public static bool IsString(string? document, string? path) => Kind(document, path) == JsonValueKind.String;

        /// <summary>Determines whether the document holds a number at the path.</summary>
        public static bool IsNumber(string? document, string? path) => Kind(document, path) == JsonValueKind.Number;

        /// <summary>Determines whether the document holds a boolean at the path.</summary>
        public static bool IsBool(string? document, string? path)
        {
            var kind = Kind(document, path);
            return kind is JsonValueKind.True or JsonValueKind.False;
        }

        /// <summary>Determines whether the document holds an array at the path.</summary>
        public static bool IsArray(string? document, string? path) => Kind(document, path) == JsonValueKind.Array;

        /// <summary>Determines whether the document holds an object at the path.</summary>
        public static bool IsObject(string? document, string? path) => Kind(document, path) == JsonValueKind.Object;

        /// <summary>
        /// Determines whether the document holds a primitive at the path — a string, a number, a
        /// boolean or a null, as against an array or an object.
        /// </summary>
        public static bool IsPrimitive(string? document, string? path)
        {
            var kind = Kind(document, path);
            return kind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        }

        /// <summary>
        /// Returns the kind of what the document holds at the path, or <c>Undefined</c> where the path
        /// is absent — which is the one answer <see cref="JsonValueKind"/> and Cosmos agree to call by
        /// the same name.
        /// </summary>
        static JsonValueKind Kind(string? document, string? path)
        {
            if (string.IsNullOrEmpty(document) || string.IsNullOrEmpty(path))
                return JsonValueKind.Undefined;

            if (Resolve(path) is not CosmosPath resolved)
                return JsonValueKind.Undefined;

            try
            {
                // The kind is read out while the document is still alive, so nothing is cloned. An
                // element does not outlive its JsonDocument, and every one of these questions is
                // answered by the kind alone — copying a value to ask what type it is would be the
                // same round trip through a representation that this class exists to avoid.
                using var parsed = JsonDocument.Parse(document);

                return CosmosDocument.TryRead(parsed.RootElement, resolved, out var value)
                    ? value.ValueKind
                    : JsonValueKind.Undefined;
            }
            catch (JsonException)
            {
                return JsonValueKind.Undefined;
            }
        }

        static CosmosPath? Resolve(string path)
        {
            return Paths.GetOrAdd(path, static text =>
                CosmosRexTranslator.TryExtendByJsonPath(CosmosPath.Root(CosmosImplementor.DefaultRootAlias), text, out var resolved)
                    ? resolved
                    : null);
        }

    }

}
