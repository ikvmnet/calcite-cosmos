using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// Resolves the references in a declared schema, so the compiler can walk the structure rather
    /// than the pointers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Within the document, and nowhere else.</b> A reference names a node by URI: the resource an
    /// <c>$id</c> establishes, and a fragment within it — a JSON Pointer, or an anchor. Every resource a
    /// schema can name without fetching anything is a node of the document itself, so resolving is an
    /// index of those, built once, and a lookup. A remote reference names a resource the document does
    /// not hold, and resolves to nothing: nothing in a model causes a network call.
    /// </para>
    /// <para>
    /// <b>A reference is resolved where it sits, not by its text.</b> The same <c>#/$defs/x</c> names a
    /// different node under a nested <c>$id</c>, and a bundle — several resources embedded under
    /// <c>$defs</c>, each with its own <c>$id</c> — is written entirely in references relative to the
    /// resource they appear in. So every object is indexed with the base URI in effect at it, and a
    /// reference is resolved against that.
    /// </para>
    /// <para>
    /// <b>Written here rather than taken from a library.</b> In-document resolution is all the reader
    /// asks for, and the .NET library that does it ships its binaries under terms a package depending on
    /// it would pass on to everyone downstream; see <c>TODO.md</c>. The dialects differ in two places this
    /// honours: before 2019-09 an <c>$id</c> of the form <c>#name</c> is an anchor rather than a base,
    /// and draft 4 spells <c>$id</c> as <c>id</c>.
    /// </para>
    /// <para>
    /// <b>References that do not resolve are not errors.</b> One that names nothing in the document
    /// yields no facts rather than refusing the container. The schema is a declaration about documents;
    /// failing to read part of one loses pushdowns and nothing else.
    /// </para>
    /// </remarks>
    sealed class JsonSchemaResolver
    {

        /// <summary>
        /// The base of a document that states none, so that a relative reference still has something to
        /// resolve against. A reserved domain: nothing is ever fetched from it.
        /// </summary>
        static readonly Uri DefaultBase = new("https://schema.invalid/document");

        /// <summary>
        /// The members whose values are data rather than schemas, which an <c>$id</c> inside does not
        /// make a resource.
        /// </summary>
        /// <remarks>
        /// OpenAPI's <c>discriminator</c> among them: its <c>mapping</c> is keyed by data values, and a
        /// value spelled <c>id</c> would otherwise read as draft 4's identifier keyword. Keyword names and
        /// property names share one namespace in a walk like this, which is the trap the previous
        /// resolver's first draft fell into.
        /// </remarks>
        static readonly HashSet<string> Data = new(StringComparer.Ordinal) { "const", "enum", "examples", "default", "discriminator" };

        readonly bool _legacyIds;
        readonly Dictionary<JsonNode, Uri> _bases = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<string, JsonNode> _resources = new(StringComparer.Ordinal);
        readonly Dictionary<string, JsonNode> _anchors = new(StringComparer.Ordinal);
        readonly Dictionary<JsonNode, (JsonNode Target, string Location)?> _resolved = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="root">The schema document.</param>
        public JsonSchemaResolver(JsonNode root)
        {
            var dialect = root.Get("$schema").Text() ?? "";
            _legacyIds = dialect.Contains("draft-04") || dialect.Contains("draft-06") || dialect.Contains("draft-07");

            Index(root, DefaultBase);
        }

        /// <summary>
        /// The key a resource is held under: its URI without a fragment.
        /// </summary>
        static string Key(Uri uri) => uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);

        /// <summary>
        /// Records the base in effect at every object, and every resource and anchor, by walking the
        /// document once.
        /// </summary>
        /// <remarks>
        /// Structural, except that data is not walked: an <c>$id</c> inside a <c>const</c> is a string in
        /// a value, not a resource. Everything else that is an object might be a schema, and indexing one
        /// that is not costs an entry nothing will ask for.
        /// </remarks>
        void Index(JsonNode? node, Uri @base)
        {
            if (node is JsonArray array)
            {
                foreach (var element in array)
                    Index(element, @base);

                return;
            }

            if (node is not JsonObject obj)
                return;

            var id = obj.Get("$id").Text() ?? (_legacyIds ? obj.Get("id").Text() : null);

            if (id is not null)
            {
                if (id.StartsWith("#", StringComparison.Ordinal))
                {
                    // Before 2019-09, "$id": "#name" names this node within the resource it is in.
                    if (_legacyIds && id.Length > 1)
                        _anchors.TryAdd(Key(@base) + id, obj);
                }
                else if (Uri.TryCreate(@base, id, out var rebased))
                {
                    @base = rebased;
                    _resources.TryAdd(Key(rebased), obj);

                    // An id carrying a fragment, legal before 2019-09, is an anchor as well.
                    if (_legacyIds && rebased.Fragment.Length > 1)
                        _anchors.TryAdd(Key(rebased) + rebased.Fragment, obj);
                }
            }

            if (obj.Get("$anchor").Text() is string anchor)
                _anchors.TryAdd(Key(@base) + "#" + anchor, obj);

            if (obj.Get("$dynamicAnchor").Text() is string dynamic)
                _anchors.TryAdd(Key(@base) + "#" + dynamic, obj);

            // The document root is a resource whatever its $id says, so a reference into a document
            // that states none still finds it.
            if (obj.Parent is null)
                _resources.TryAdd(Key(@base), obj);

            _bases[obj] = @base;

            foreach (var (name, value) in obj)
                if (Data.Contains(name) == false)
                    Index(value, @base);
        }

        /// <summary>
        /// Resolves the <c>$ref</c> a node carries to the node it names.
        /// </summary>
        /// <param name="node">A schema node with a <c>$ref</c>, as it sits in the document.</param>
        /// <param name="location">
        /// The target's position in the document, which identifies it however the reference was spelled.
        /// </param>
        /// <returns>The target, or <c>null</c> where it could not be reached.</returns>
        public JsonNode? Resolve(JsonNode node, out string? location)
        {
            location = null;

            if (_resolved.TryGetValue(node, out var known) == false)
                _resolved[node] = known = ResolveAt(node);

            if (known is not var (target, at))
                return null;

            location = at;
            return target;
        }

        (JsonNode Target, string Location)? ResolveAt(JsonNode node)
        {
            if (node.Get("$ref").Text() is not string reference || _bases.TryGetValue(node, out var @base) == false)
                return null;

            // Before 2019-09 a $ref replaces everything beside it, its own $id included, so it resolves
            // against the base the node was reached under rather than the one its $id would set.
            if (_legacyIds && node.Parent is JsonNode parent && Enclosing(parent) is Uri outer && (node.Get("$id") ?? node.Get("id")) is not null)
                @base = outer;

            if (Uri.TryCreate(@base, reference, out var target) == false)
                return null;

            if (_resources.TryGetValue(Key(target), out var resource) == false)
                return null;

            var fragment = Uri.UnescapeDataString(target.Fragment.Length > 0 ? target.Fragment.Substring(1) : "");

            var found = fragment.Length == 0 ? resource
                : fragment[0] == '/' ? Point(resource, fragment)
                : _anchors.TryGetValue(Key(target) + "#" + fragment, out var anchored) ? anchored
                : null;

            return found is null ? null : (found, found.GetPath());
        }

        /// <summary>
        /// The base in effect at the nearest object at or above a node.
        /// </summary>
        Uri? Enclosing(JsonNode node)
        {
            for (JsonNode? at = node; at is not null; at = at.Parent)
                if (at is JsonObject && _bases.TryGetValue(at, out var found))
                    return found;

            return null;
        }

        /// <summary>
        /// Follows a JSON Pointer from a resource's root, or answers <c>null</c> where it names nothing.
        /// </summary>
        static JsonNode? Point(JsonNode root, string pointer)
        {
            var at = root;

            foreach (var raw in pointer.Substring(1).Split('/'))
            {
                var token = raw.Replace("~1", "/").Replace("~0", "~");

                at = at switch
                {
                    JsonObject obj when obj.TryGetPropertyValue(token, out var member) => member,
                    JsonArray array when int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < array.Count => array[index],
                    _ => null,
                };

                if (at is null)
                    return null;
            }

            return at;
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
            if (node is not JsonObject)
                return node;

            if (node.Get("$ref").Text() is not null)
                return Resolve(node, out _);

            return node;
        }

    }

}
