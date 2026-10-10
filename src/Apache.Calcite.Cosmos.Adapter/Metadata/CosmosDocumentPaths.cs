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
        /// The alias is dropped, being the statement's business. An array subscript answers <c>null</c>
        /// rather than being skipped: <c>$.tags[0]</c> is not <c>$.tags</c>, and silently conflating
        /// them would apply an element's fact to the array or the reverse.
        /// </remarks>
        /// <param name="path">The statement's path.</param>
        /// <returns>The document path, or <c>null</c>.</returns>
        public static JsonDocumentPath? From(CosmosPath? path)
        {
            if (path is null)
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
