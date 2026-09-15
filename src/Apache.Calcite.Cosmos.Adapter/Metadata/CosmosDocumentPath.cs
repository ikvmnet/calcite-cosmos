using System;
using System.Collections.Generic;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Sql;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// A property path from the root of a document, which is what a declared fact is about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a <see cref="CosmosPath"/>. That type is rooted at a <c>FROM</c> alias because
    /// it is what a statement writes; a fact read out of a container's schema is a claim about the
    /// document's shape and knows nothing about the alias a query happens to bind. Keeping them apart
    /// means a fact never has to be rebuilt when the same path is reached through a different alias,
    /// and a lookup is a plain dictionary hit.
    /// </para>
    /// <para>
    /// Property names only. A schema can describe array elements and one day the facts should reach
    /// them, but nothing consults an element fact yet and a path model that admits indices would have
    /// to answer what <c>$.tags[0]</c> means against a fact declared for <c>items</c>. See
    /// <c>TODO.md</c> under <em>Facts about array elements</em>.
    /// </para>
    /// </remarks>
    public sealed class CosmosDocumentPath : IEquatable<CosmosDocumentPath>
    {

        /// <summary>
        /// The document itself, which is the path every walk starts from.
        /// </summary>
        public static readonly CosmosDocumentPath Root = new(Array.Empty<string>());

        readonly string[] _names;
        readonly int _hash;

        CosmosDocumentPath(string[] names)
        {
            _names = names;

            var hash = new HashCode();
            foreach (var name in names)
                hash.Add(name, StringComparer.Ordinal);

            _hash = hash.ToHashCode();
        }

        /// <summary>
        /// Gets the property names, outermost first.
        /// </summary>
        public IReadOnlyList<string> Names => _names;

        /// <summary>
        /// Gets whether this is the document itself.
        /// </summary>
        public bool IsRoot => _names.Length == 0;

        /// <summary>
        /// Returns this path with a property access appended.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns>The extended path.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        public CosmosDocumentPath Property(string name)
        {
            if (name is null)
                throw new ArgumentNullException(nameof(name));

            var names = new string[_names.Length + 1];
            Array.Copy(_names, names, _names.Length);
            names[_names.Length] = name;

            return new CosmosDocumentPath(names);
        }

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
        public static CosmosDocumentPath? From(CosmosPath? path)
        {
            if (path is null)
                return null;

            var names = new string[path.Segments.Count];

            for (var i = 0; i < path.Segments.Count; i++)
            {
                if (path.Segments[i].Name is not string name)
                    return null;

                names[i] = name;
            }

            return new CosmosDocumentPath(names);
        }

        /// <inheritdoc />
        public bool Equals(CosmosDocumentPath? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            if (_hash != other._hash || _names.Length != other._names.Length)
                return false;

            for (var i = 0; i < _names.Length; i++)
                if (string.Equals(_names[i], other._names[i], StringComparison.Ordinal) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CosmosDocumentPath other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => _hash;

        /// <summary>
        /// Renders the path the way a JSON pointer expression reads, for diagnostics.
        /// </summary>
        /// <returns>The path, such as <c>$.shipment.id</c>.</returns>
        public override string ToString()
        {
            var builder = new StringBuilder("$");

            foreach (var name in _names)
                builder.Append('.').Append(name);

            return builder.ToString();
        }

    }

}
