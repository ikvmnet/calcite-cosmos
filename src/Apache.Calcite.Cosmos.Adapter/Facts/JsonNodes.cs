using System.Text.Json;
using System.Text.Json.Nodes;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The handful of questions the schema reader asks of a <see cref="JsonNode"/>.
    /// </summary>
    /// <remarks>
    /// <b>A JSON null is a C# <c>null</c> here</b>, which is also what a missing member reads as.
    /// Wherever a schema is expected the two mean the same — neither is a schema — and
    /// <see cref="Get"/> answers <c>null</c> for both. Where they differ, which is a literal, the
    /// reader asks <see cref="TryMember"/>, which says whether the member is there at all.
    /// </remarks>
    static class JsonNodes
    {

        /// <summary>
        /// Returns a member of an object, or <c>null</c> where the node is not an object, the member is
        /// missing, or the member is a JSON null.
        /// </summary>
        public static JsonNode? Get(this JsonNode? node, string name) =>
            node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value : null;

        /// <summary>
        /// Determines whether an object has a member, which may be a JSON null.
        /// </summary>
        public static bool TryMember(this JsonNode? node, string name, out JsonNode? value)
        {
            value = null;
            return node is JsonObject obj && obj.TryGetPropertyValue(name, out value);
        }

        /// <summary>
        /// Returns an element of an array, or <c>null</c> where there is none or it is a JSON null.
        /// </summary>
        public static JsonNode? At(this JsonNode? node, int index) =>
            node is JsonArray array && index >= 0 && index < array.Count ? array[index] : null;

        /// <summary>
        /// Returns how many members or elements a node has, or zero for a scalar.
        /// </summary>
        public static int Size(this JsonNode? node) => node switch
        {
            JsonObject obj => obj.Count,
            JsonArray array => array.Count,
            _ => 0,
        };

        /// <summary>
        /// Returns the string a node holds, or <c>null</c> where it holds anything else.
        /// </summary>
        public static string? Text(this JsonNode? node) =>
            node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

        /// <summary>
        /// Returns the boolean a node holds, or <c>null</c> where it holds anything else.
        /// </summary>
        public static bool? Boolean(this JsonNode? node) => node is JsonValue value ? value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;

    }

}
