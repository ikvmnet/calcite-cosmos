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
        readonly bool _rebased;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="root">The schema document.</param>
        public CosmosSchemaResolver(JsonNode root)
        {
            _rebased = Rebases(root, top: true);

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
        /// Determines whether anything below the root moves the base a reference resolves against.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A nested <c>$id</c> starts a new base URI, and a fragment written under it means a fragment
        /// of <em>that</em> document rather than of the one it is embedded in. Resolution here is
        /// against the root, so under a nested <c>$id</c> the same pointer can name a different node
        /// — and a reference resolved to the wrong node is the one failure mode worth refusing
        /// outright, since it yields facts about the wrong path rather than none.
        /// </para>
        /// <para>
        /// <c>$anchor</c> and the dynamic pair go with it: both are resolved against a base this does
        /// not track. Rare in the schemas anyone writes by hand, and cheap to detect.
        /// </para>
        /// </remarks>
        static bool Rebases(JsonNode? node, bool top)
        {
            if (node is null)
                return false;

            if (node.isArray())
            {
                for (var i = 0; i < node.size(); i++)
                    if (Rebases(node.get(i), top: false))
                        return true;

                return false;
            }

            if (node.isObject() == false)
                return false;

            // Keyword names and property names share one namespace in this walk, which is the trap
            // here: a schema describing a property called `id` is not a schema that rebases, and
            // Draft 4 spelled the keyword exactly that. So only the `$` forms are looked for. A
            // document with a property actually called `$id` would be read as rebasing and simply
            // follow no references, which loses facts rather than stating wrong ones.
            if (top == false && node.has("$id"))
                return true;

            if (node.has("$anchor") || node.has("$dynamicAnchor") || node.has("$dynamicRef") || node.has("$recursiveRef"))
                return true;

            var fields = node.fields();
            while (fields.hasNext())
                if (Rebases((JsonNode?)((java.util.Map.Entry)fields.next()).getValue(), top: false))
                    return true;

            return false;
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
            if (_schema is null || _rebased || string.IsNullOrEmpty(reference))
                return null;

            // A root-relative JSON pointer and nothing else. That is what resolution here can answer
            // correctly; an absolute URI, a relative document, or a bare anchor would be resolved
            // against a base this does not track, and a reference pointed at the wrong node states
            // facts about the wrong path.
            if (reference!.StartsWith("#/", StringComparison.Ordinal) == false)
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
