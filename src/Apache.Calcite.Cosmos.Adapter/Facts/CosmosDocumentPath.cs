using System;
using System.Collections.Generic;
using System.Text;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A property path from the root of a document, which is what a declared fact is about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a statement's path. That is rooted at a <c>FROM</c> alias because it is what a
    /// statement writes; a fact read out of a container's schema is a claim about the
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
    /// <para>
    /// <b>A value, and a struct for that reason.</b> Two paths naming the same properties are the same
    /// path, wherever each was built, and a path is a dictionary key far more often than it is anything
    /// else. Its <c>default</c> is <see cref="Root"/>, so a path nothing initialised is the document
    /// rather than a value that cannot be asked anything. Where a caller has no path to give, it says so
    /// with <c>CosmosDocumentPath?</c>.
    /// </para>
    /// <para>
    /// <b>A list that shares its prefixes.</b> Every path is built from a shorter one by
    /// <see cref="Property"/>, so a path is its last name and a reference to the path before it: one
    /// small node per step, and the paths a schema walk builds under one object share that object's
    /// nodes rather than each copying its names. The hash is built the same way, from the parent's, so
    /// nothing is ever recomputed; and two paths built from the same prefix answer equality at the
    /// first node they share.
    /// </para>
    /// </remarks>
    public readonly struct CosmosDocumentPath : IEquatable<CosmosDocumentPath>
    {

        /// <summary>
        /// One step of a path: its last name, and the path it extends.
        /// </summary>
        sealed class Node
        {

            public Node(Node? parent, string name)
            {
                Parent = parent;
                Name = name;
                Depth = (parent?.Depth ?? 0) + 1;
                Hash = HashCode.Combine(parent?.Hash ?? 0, StringComparer.Ordinal.GetHashCode(name));
            }

            public Node? Parent { get; }

            public string Name { get; }

            public int Depth { get; }

            public int Hash { get; }

        }

        /// <summary>
        /// The document itself, which is the path every walk starts from. The same as <c>default</c>.
        /// </summary>
        public static readonly CosmosDocumentPath Root = default;

        // Null for the root, which is what the default value holds.
        readonly Node? _last;

        CosmosDocumentPath(Node last)
        {
            _last = last;
        }

        /// <summary>
        /// Gets the property names, outermost first.
        /// </summary>
        /// <remarks>
        /// Built when asked, the list being stored innermost first.
        /// </remarks>
        public IReadOnlyList<string> Names
        {
            get
            {
                var names = new string[Depth];
                for (var node = _last; node is not null; node = node.Parent)
                    names[node.Depth - 1] = node.Name;

                return names;
            }
        }

        /// <summary>
        /// Gets how many property names the path has; the root has none.
        /// </summary>
        public int Depth => _last?.Depth ?? 0;

        /// <summary>
        /// Gets whether this is the document itself.
        /// </summary>
        public bool IsRoot => _last is null;

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

            return new CosmosDocumentPath(new Node(_last, name));
        }

        /// <inheritdoc />
        public bool Equals(CosmosDocumentPath other)
        {
            var left = _last;
            var right = other._last;

            while (true)
            {
                // A shared node shares everything before it too.
                if (ReferenceEquals(left, right))
                    return true;
                if (left is null || right is null)
                    return false;
                if (left.Hash != right.Hash || left.Depth != right.Depth || string.Equals(left.Name, right.Name, StringComparison.Ordinal) == false)
                    return false;

                left = left.Parent;
                right = right.Parent;
            }
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CosmosDocumentPath other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => _last?.Hash ?? 0;

        /// <summary>
        /// Determines whether two paths name the same properties.
        /// </summary>
        public static bool operator ==(CosmosDocumentPath left, CosmosDocumentPath right) => left.Equals(right);

        /// <summary>
        /// Determines whether two paths name different properties.
        /// </summary>
        public static bool operator !=(CosmosDocumentPath left, CosmosDocumentPath right) => left.Equals(right) == false;

        /// <summary>
        /// Renders the path the way a JSON pointer expression reads, for diagnostics.
        /// </summary>
        /// <returns>The path, such as <c>$.shipment.id</c>.</returns>
        public override string ToString()
        {
            var builder = new StringBuilder("$");

            foreach (var name in Names)
                builder.Append('.').Append(name);

            return builder.ToString();
        }

    }

}
