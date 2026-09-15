using System;

using com.fasterxml.jackson.databind;
using com.networknt.schema;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Resolves the references in a declared schema, so the compiler can walk the structure rather
    /// than the pointers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A library does this and hand-rolling it does not: <c>$ref</c> against <c>$id</c>, anchors, and
    /// the dialect differences between them are a specification in themselves, and a real schema —
    /// certainly one generated from an OpenAPI document — is mostly references.
    /// </para>
    /// <para>
    /// <b>The library resolves; the compiler walks.</b> <c>com.networknt</c> has a walker of its own,
    /// but it is shaped for walking an <em>instance</em> against a schema — it follows the branch a
    /// document selects. The compiler wants every branch, each under the guard that selects it, which
    /// is a different traversal over the same tree. So only resolution is taken from here.
    /// </para>
    /// <para>
    /// <b>References that do not resolve are not errors.</b> A remote reference is not fetched — a URL
    /// in an operand is a network call at schema registration — and an unresolvable pointer yields no
    /// facts rather than refusing the container. The schema is a declaration about documents; failing
    /// to read part of one loses pushdowns and nothing else.
    /// </para>
    /// </remarks>
    sealed class CosmosSchemaResolver
    {

        readonly JsonSchema? _schema;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="root">The schema document.</param>
        public CosmosSchemaResolver(JsonNode root)
        {
            try
            {
                _schema = JsonSchemaFactory.getInstance(DialectOf(root)).getSchema(root);
            }
            catch (Exception)
            {
                // A schema the library will not load still has a tree the compiler can read; it just
                // has no references. Refusing the container over it would turn a declaration meant to
                // add pushdowns into a reason a query stops working.
                _schema = null;
            }
        }

        /// <summary>
        /// Reads the dialect off <c>$schema</c>, defaulting to 2020-12.
        /// </summary>
        /// <remarks>
        /// 2020-12 is the default because it is what OpenAPI 3.1 aligned to, and because the keywords
        /// the compiler reads are stable across every dialect it admits. A dialect this does not know
        /// is read as 2020-12 rather than refused, for the reason the type's remarks give.
        /// </remarks>
        static SpecVersion.VersionFlag DialectOf(JsonNode root)
        {
            var declared = root?.get("$schema") is JsonNode node && node.isTextual() ? node.asText() : null;

            if (declared is null)
                return SpecVersion.VersionFlag.V202012;

            if (declared.Contains("draft-07"))
                return SpecVersion.VersionFlag.V7;
            if (declared.Contains("2019-09"))
                return SpecVersion.VersionFlag.V201909;
            if (declared.Contains("draft-06"))
                return SpecVersion.VersionFlag.V6;
            if (declared.Contains("draft-04"))
                return SpecVersion.VersionFlag.V4;

            return SpecVersion.VersionFlag.V202012;
        }

        /// <summary>
        /// Resolves a reference to the node it names, or <c>null</c>.
        /// </summary>
        /// <param name="reference">The <c>$ref</c> value.</param>
        /// <returns>The target, or <c>null</c> where it could not be reached.</returns>
        public JsonNode? Resolve(string? reference)
        {
            if (_schema is null || string.IsNullOrEmpty(reference))
                return null;

            try
            {
                return _schema.getRefSchemaNode(reference);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the node a schema node stands for: the target where it is a reference, and itself
        /// where it is not.
        /// </summary>
        /// <remarks>
        /// What the compiler reaches for when it needs to look <em>inside</em> a node it is not walking
        /// — a branch's discriminator, a condition's subschema — where a reference in the way would
        /// otherwise read as an absence.
        /// </remarks>
        /// <param name="node">The node.</param>
        /// <returns>The node it stands for, or <c>null</c>.</returns>
        public JsonNode? Follow(JsonNode? node)
        {
            if (node is null || node.isObject() == false)
                return node;

            if (node.get("$ref") is JsonNode reference && reference.isTextual())
                return Resolve(reference.asText());

            return node;
        }

    }

}
