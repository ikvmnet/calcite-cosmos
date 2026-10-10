using System;

using System.Text.Json.Nodes;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What a schema reader is told to recognise beyond JSON Schema's own vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>JSON Schema says what type a value has; it does not say how a typed value is spelled.</b> A
    /// <c>pattern</c> beside <c>"type": "string"</c> can pin a spelling, and only a consumer knows which
    /// spellings it can use and what each licenses — so <see cref="StringPattern"/> is asked, and the
    /// reader states <see cref="JsonClaim.Represents"/> with whatever form it answers. Without one, a
    /// pattern reads as nothing, which loses facts rather than inventing them.
    /// </para>
    /// <para>
    /// <see cref="Subschema"/> is the same idea one step wider: a consumer may read a whole subschema as
    /// a claim of its own, the way the adapter reads a pinned GeoJSON shape as a geography the service
    /// can measure. It is asked of every subschema the walk reaches and answers <c>null</c> for most.
    /// </para>
    /// </remarks>
    /// <param name="StringPattern">
    /// Recognises the <c>pattern</c> of a subschema declaring a string, answering its form or <c>null</c>.
    /// </param>
    /// <param name="Subschema">
    /// Recognises a whole subschema as a claim about the value at its path, or answers <c>null</c>.
    /// </param>
    public sealed record JsonSchemaRecognisers(
        Func<string?, IJsonStoredForm?>? StringPattern = null,
        Func<JsonNode, JsonClaim?>? Subschema = null)
    {

        /// <summary>
        /// Recognises nothing beyond JSON Schema: types, presence, constants and domains.
        /// </summary>
        public static readonly JsonSchemaRecognisers None = new();

    }

}
