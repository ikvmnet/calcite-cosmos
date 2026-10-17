using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

using System.Text.Json.Nodes;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What a container's schema is read for beyond JSON Schema: the stored forms the adapter can push
    /// a comparison through, the characters a string may hold, and the geographies the service will
    /// measure.
    /// </summary>
    /// <remarks>
    /// The fact theory reads a schema and knows nothing of either; it asks
    /// <see cref="JsonSchemaRecognisers"/>, and these are the adapter's answers. Every schema the
    /// adapter reads is read through <see cref="ReadFrom"/>, so a form is recognised wherever the model
    /// declares one.
    /// </remarks>
    public static class CosmosSchemaRecognition
    {

        /// <summary>
        /// The adapter's recognisers: <see cref="CosmosStoredForms.Recognise"/> for the pattern of a
        /// declared string, and for a subschema <see cref="CosmosPatternAlphabet.Claims"/> for its pattern
        /// and <see cref="CosmosGeographyForms.Recognise"/>.
        /// </summary>
        public static readonly JsonSchemaRecognisers Cosmos = new(
            StringPattern: pattern => CosmosStoredForms.Recognise(pattern) is CosmosRepresentation representation ? representation : null,
            Subschema: Recognise);

        /// <summary>
        /// Reads the facts a container's declared schema states, recognising the adapter's forms.
        /// </summary>
        /// <param name="schema">The schema document, as the tree the model delivered.</param>
        /// <returns>The rules, which may be empty where nothing could be read.</returns>
        public static IReadOnlyList<JsonFactRule> ReadFrom(JsonNode schema) => JsonSchemaFacts.ReadFrom(schema, Cosmos);

        /// <summary>
        /// Reads a subschema as the adapter's own claims about the value at its path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A pattern's characters are read beside any declared type, or none: a pattern is vacuous for
        /// anything but a string, and so is what it says of the characters. A stored form is not, since it
        /// says the value is a string, which is why that one waits for <c>"type": "string"</c>.
        /// </para>
        /// <para>
        /// <b>Where the pattern is a stored form, the form says it.</b> Every registered form is ASCII, and a form
        /// establishes the characters through the claim's own reading of it
        /// (<see cref="CosmosCharacters.IsEntailedBy"/>); read again here, one pattern would say them twice. A form
        /// is stated beside a declared string only, so a form's pattern beside no type is read here as any other.
        /// </para>
        /// </remarks>
        static IReadOnlyList<JsonClaim> Recognise(JsonNode node)
        {
            var pattern = node.Get("pattern").Text();

            var characters = JsonSchemaFacts.ReadType(node) is (JsonType.String, _) && CosmosStoredForms.Recognise(pattern) is not null
                ? System.Array.Empty<JsonClaim>()
                : CosmosPatternAlphabet.Claims(pattern);

            if (CosmosGeographyForms.Recognise(node) == false)
                return characters;

            return new List<JsonClaim>(characters) { new CosmosGeography() };
        }

    }

}
