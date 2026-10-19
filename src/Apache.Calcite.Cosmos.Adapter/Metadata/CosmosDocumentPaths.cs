using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Recovers the document path a statement's path addresses.
    /// </summary>
    /// <remarks>
    /// The bridge between the two path types, kept on the statement's side because a document path
    /// knows nothing of statements. See <see cref="JsonDocumentPath"/>.
    /// </remarks>
    public static class CosmosDocumentPaths
    {

        /// <summary>
        /// Recovers the document path a statement's path addresses, or <c>null</c> where it addresses
        /// something a fact cannot be about.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Rooted at the container, or nothing.</b> A path under a traversal's alias addresses an
        /// element of an array, and <c>t.x</c> is no more the document's <c>x</c> than <c>$.tags[0]</c> is
        /// <c>$.tags</c>: read as one, a fact of the document's property would be applied to every
        /// element's. Every caller hands over the alias its statement binds the container to, so the
        /// question is asked once, here, and no site can forget it — five did, at the self-join's keys,
        /// the lookup join's key, an aggregate's presence and a distance sort's operands.
        /// </para>
        /// <para>
        /// An array subscript answers <c>null</c> rather than being skipped, for the same reason:
        /// silently conflating an element with its array would apply one's fact to the other.
        /// </para>
        /// </remarks>
        /// <param name="path">The statement's path.</param>
        /// <param name="rootAlias">The alias the statement binds the container to.</param>
        /// <returns>The document path, or <c>null</c>.</returns>
        public static JsonDocumentPath? From(CosmosPath? path, string rootAlias)
        {
            if (path is null || string.Equals(path.Alias, rootAlias, System.StringComparison.Ordinal) == false)
                return null;

            var document = JsonDocumentPath.Root;

            for (var i = 0; i < path.Segments.Count; i++)
            {
                if (path.Segments[i].Name is not string name)
                    return null;

                document = document.Property(name);
            }

            return document;
        }

    }

}
