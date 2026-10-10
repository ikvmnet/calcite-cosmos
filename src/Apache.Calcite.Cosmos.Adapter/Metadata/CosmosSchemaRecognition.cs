using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

using com.fasterxml.jackson.databind;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What a container's schema is read for beyond JSON Schema: the stored forms the adapter can push
    /// a comparison through, and the geographies the service will measure.
    /// </summary>
    /// <remarks>
    /// The fact theory reads a schema and knows nothing of either; it asks
    /// <see cref="CosmosSchemaRecognisers"/>, and these are the adapter's answers. Every schema the
    /// adapter reads is read through <see cref="ReadFrom"/>, so a form is recognised wherever the model
    /// declares one.
    /// </remarks>
    public static class CosmosSchemaRecognition
    {

        /// <summary>
        /// The adapter's recognisers: <see cref="CosmosStoredForms.Recognise"/> for a pattern, and
        /// <see cref="CosmosGeographyForms.Recognise"/> for a subschema.
        /// </summary>
        public static readonly CosmosSchemaRecognisers Cosmos = new(
            StringPattern: pattern => CosmosStoredForms.Recognise(pattern) is CosmosRepresentation representation ? representation : null,
            Subschema: node => CosmosGeographyForms.Recognise(node) ? new CosmosGeography() : null);

        /// <summary>
        /// Reads the facts a container's declared schema states, recognising the adapter's forms.
        /// </summary>
        /// <param name="schema">The schema document, as the tree the model delivered.</param>
        /// <returns>The rules, which may be empty where nothing could be read.</returns>
        public static IReadOnlyList<CosmosFactRule> ReadFrom(JsonNode schema) => CosmosSchemaFacts.ReadFrom(schema, Cosmos);

    }

}
