using System;
using System.Collections.Generic;

using com.fasterxml.jackson.databind;
using com.networknt.schema;
using com.networknt.schema.resource;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// Resolves the references in a declared schema, so the compiler can walk the structure rather
    /// than the pointers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A library does this and hand-rolling it does not: <c>$ref</c> against <c>$id</c>, anchors, and
    /// the dialect differences between them are a specification in themselves, and a real schema —
    /// certainly one generated from an OpenAPI document, or bundled from several files — is mostly
    /// references.
    /// </para>
    /// <para>
    /// <b>The library resolves; the compiler walks.</b> <c>com.networknt</c> has a walker of its own,
    /// but it is shaped for walking an <em>instance</em> against a schema — it follows the branch a
    /// document selects. The compiler wants every branch, each under the guard that selects it, which
    /// is a different traversal over the same tree. So only resolution is taken from here.
    /// </para>
    /// <para>
    /// <b>A reference is resolved where it sits, not by its text.</b> The same <c>#/$defs/x</c> names
    /// a different node under a nested <c>$id</c>, and a bundle — several resources embedded under
    /// <c>$defs</c>, each with its own <c>$id</c> — is written entirely in references relative to the
    /// resource they appear in. So a reference is looked up by the node that carries it: the node's
    /// position in the document is handed to the library, which builds the schema there with the base
    /// URI every enclosing <c>$id</c> gives it, and its own <c>$ref</c> keyword names the target.
    /// </para>
    /// <para>
    /// <b>References that do not resolve are not errors.</b> A remote reference is not fetched — the
    /// library's loaders are removed, since a URL in an operand would otherwise be a network call at
    /// schema registration — and a reference that names nothing in the document yields no facts rather
    /// than refusing the container. The schema is a declaration about documents; failing to read part
    /// of one loses pushdowns and nothing else.
    /// </para>
    /// </remarks>
    sealed class JsonSchemaResolver
    {

        readonly JsonSchema? _schema;
        readonly Dictionary<JsonNode, JsonNodePath> _positions = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<JsonNode, (JsonNode Target, string Location)?> _resolved = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="root">The schema document.</param>
        public JsonSchemaResolver(JsonNode root)
        {
            Index(root, new JsonNodePath(PathType.JSON_POINTER));

            try
            {
                // A factory per document rather than one shared: the factory caches what it loads by
                // `$id`, and two containers declaring different schemas under the same `$id` would
                // otherwise each be resolved against whichever registered first.
                var factory = JsonSchemaFactory.getInstance(DialectOf(root), new Consumer(b =>
                    ((JsonSchemaFactory.Builder)b).schemaLoaders(new Consumer(l =>
                        ((SchemaLoaders.Builder)l).values(new Consumer(v => ((java.util.List)v).clear()))))));

                _schema = factory.getSchema(root);
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
        /// Records where every container node sits in the document, by identity.
        /// </summary>
        /// <remarks>
        /// By identity because the same subschema written twice is two positions, possibly under two
        /// bases; Jackson's own equality is structural and would merge them. And purely structurally —
        /// which members are schemas and which are data is the library's question, answered when a
        /// position is handed to it, so nothing here reads a keyword.
        /// </remarks>
        void Index(JsonNode? node, JsonNodePath position)
        {
            if (node is null || node.isContainerNode() == false)
                return;

            _positions[node] = position;

            if (node.isArray())
            {
                for (var i = 0; i < node.size(); i++)
                    Index(node.get(i), position.append(i));

                return;
            }

            var fields = node.fields();
            while (fields.hasNext())
            {
                var field = (java.util.Map.Entry)fields.next();
                Index((JsonNode?)field.getValue(), position.append((string)field.getKey()));
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
        /// Resolves the <c>$ref</c> a node carries to the node it names.
        /// </summary>
        /// <param name="node">A schema node with a <c>$ref</c>, as it sits in the document.</param>
        /// <param name="location">
        /// The target's absolute location — its resource's URI and the pointer within it — which is
        /// what identifies it however the reference was spelled.
        /// </param>
        /// <returns>The target, or <c>null</c> where it could not be reached.</returns>
        public JsonNode? Resolve(JsonNode node, out string? location)
        {
            location = null;

            if (_schema is null || _positions.TryGetValue(node, out var position) == false)
                return null;

            if (_resolved.TryGetValue(node, out var known) == false)
                _resolved[node] = known = ResolveAt(position);

            if (known is not var (target, at))
                return null;

            location = at;
            return target;
        }

        (JsonNode Target, string Location)? ResolveAt(JsonNodePath position)
        {
            try
            {
                var schema = position.getNameCount() == 0 ? _schema! : _schema!.getSubSchema(position);

                var validators = schema.getValidators().iterator();
                while (validators.hasNext())
                    if (validators.next() is RefValidator reference && reference.getSchemaRef().getSchema() is JsonSchema target && target.getSchemaNode() is JsonNode resolved)
                        return (resolved, target.getSchemaLocation().toString());

                return null;
            }
            catch (Exception)
            {
                // Unresolvable, remote, or a position the library will not build a schema at. Each
                // loses the facts behind the reference and nothing more.
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
                return Resolve(node, out _);

            return node;
        }

        /// <summary>
        /// A Java consumer over a delegate, for the library's builder callbacks.
        /// </summary>
        sealed class Consumer : java.util.function.Consumer
        {

            readonly Action<object> _accept;

            public Consumer(Action<object> accept) => _accept = accept;

            public void accept(object value) => _accept(value);

            public java.util.function.Consumer andThen(java.util.function.Consumer after) =>
                java.util.function.Consumer.__DefaultMethods.andThen(this, after);

        }

    }

}
