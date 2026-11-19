using System;
using System.Collections.Generic;
using System.Text;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// An immutable Cosmos property path rooted at a <c>FROM</c> alias, such as
    /// <c>c.address.city</c> or <c>c["odd name"][0]</c>.
    /// </summary>
    /// <remarks>
    /// Paths are the Cosmos equivalent of a column reference. Because Cosmos documents are
    /// hierarchical, a Calcite field reference generally translates to a path of more than one
    /// segment. Instances are immutable; <see cref="Property"/> and <see cref="Index"/> return
    /// new paths.
    /// </remarks>
    public sealed class CosmosPath : IEquatable<CosmosPath>
    {

        /// <summary>
        /// Creates a path consisting of nothing but the root alias.
        /// </summary>
        /// <param name="alias">The <c>FROM</c> alias the path is rooted at.</param>
        /// <returns>The path.</returns>
        /// <exception cref="ArgumentException"><paramref name="alias"/> is <c>null</c> or empty.</exception>
        public static CosmosPath Root(string alias)
        {
            if (string.IsNullOrEmpty(alias))
                throw new ArgumentException($"'{nameof(alias)}' cannot be null or empty.", nameof(alias));

            return new CosmosPath(alias, Array.Empty<CosmosPathSegment>());
        }

        readonly string _alias;
        readonly CosmosPathSegment[] _segments;

        CosmosPath(string alias, CosmosPathSegment[] segments)
        {
            _alias = alias;
            _segments = segments;
        }

        /// <summary>
        /// Gets the <c>FROM</c> alias this path is rooted at.
        /// </summary>
        public string Alias => _alias;

        /// <summary>
        /// Gets the segments applied to the alias, in order.
        /// </summary>
        public IReadOnlyList<CosmosPathSegment> Segments => _segments;

        /// <summary>
        /// Gets whether this path is the bare alias, with no segments applied.
        /// </summary>
        public bool IsRoot => _segments.Length == 0;

        /// <summary>
        /// Gets whether a segment of this path is the element a traversal yields (<see cref="CosmosPathSegment.IsYielded"/>),
        /// which only the correlate rule's question binds and no request, policy or document read can address.
        /// </summary>
        public bool HasYielded => Array.Exists(_segments, s => s.IsYielded);

        /// <summary>
        /// Refuses a path holding the element a traversal yields where it would be spelled: a defect, since only the correlate
        /// rule's question binds one and nothing it binds is written.
        /// </summary>
        /// <param name="into">What the path would be spelled into: a request, a policy, a document read.</param>
        /// <exception cref="InvalidOperationException">The path holds the element a traversal yields.</exception>
        internal void RefuseYielded(string into)
        {
            if (HasYielded)
                throw new InvalidOperationException($"{this} holds the element a traversal yields, which {into} cannot address: only the correlate rule's question binds one.");
        }

        /// <summary>
        /// Returns a new path with a property access appended.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns>The extended path.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        public CosmosPath Property(string name) => Append(CosmosPathSegment.Property(name));

        /// <summary>
        /// Returns a new path with an array index access appended.
        /// </summary>
        /// <param name="index">The zero-based index.</param>
        /// <returns>The extended path.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
        public CosmosPath Index(int index) => Append(CosmosPathSegment.Index(index));

        /// <summary>
        /// Returns a new path with <paramref name="segment"/> appended.
        /// </summary>
        /// <param name="segment">The segment to append.</param>
        /// <returns>The extended path.</returns>
        internal CosmosPath Append(CosmosPathSegment segment)
        {
            var segments = new CosmosPathSegment[_segments.Length + 1];
            Array.Copy(_segments, segments, _segments.Length);
            segments[_segments.Length] = segment;
            return new CosmosPath(_alias, segments);
        }

        /// <summary>
        /// Returns a new path with <paramref name="segments"/> appended in order, copying this one once,
        /// or this path where there are none.
        /// </summary>
        /// <param name="segments">The segments to append.</param>
        /// <returns>The extended path.</returns>
        internal CosmosPath Append(IReadOnlyList<CosmosPathSegment> segments)
        {
            if (segments.Count == 0)
                return this;

            var appended = new CosmosPathSegment[_segments.Length + segments.Count];
            Array.Copy(_segments, appended, _segments.Length);
            for (var i = 0; i < segments.Count; i++)
                appended[_segments.Length + i] = segments[i];

            return new CosmosPath(_alias, appended);
        }

        /// <summary>
        /// Renders this path in the form used by container indexing policies, such as
        /// <c>/inventory/quantity</c>, discarding the root alias.
        /// </summary>
        /// <remarks>
        /// Array subscripts render as the <c>/[]</c> wildcard, since indexing policies address
        /// array elements collectively rather than by position. Trailing <c>/?</c> and <c>/*</c>
        /// specifiers are not emitted: composite index paths omit them, and callers comparing
        /// against included or excluded paths should account for them separately.
        /// </remarks>
        /// <returns>The policy-form path, or <c>/</c> for a root path.</returns>
        /// <exception cref="InvalidOperationException">The path holds the element a traversal yields.</exception>
        public string ToPolicyPath()
        {
            RefuseYielded("an indexing policy");

            if (_segments.Length == 0)
                return "/";

            var builder = new StringBuilder();

            foreach (var segment in _segments)
                builder.Append('/').Append(segment.IsIndex ? "[]" : segment.Name);

            return builder.ToString();
        }

        /// <summary>
        /// Appends the rendered path to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The buffer to append to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The path holds the element a traversal yields.</exception>
        public void WriteTo(StringBuilder builder)
        {
            if (builder is null)
                throw new ArgumentNullException(nameof(builder));

            RefuseYielded("a request");
            builder.Append(_alias);

            foreach (var segment in _segments)
            {
                if (segment.IsIndex)
                    CosmosSql.WriteIndexAccess(builder, segment.ArrayIndex);
                else
                    CosmosSql.WritePropertyAccess(builder, segment.Name!);
            }
        }

        /// <inheritdoc />
        public bool Equals(CosmosPath? other)
        {
            if (other is null)
                return false;
            if (string.Equals(_alias, other._alias, StringComparison.Ordinal) == false)
                return false;
            if (_segments.Length != other._segments.Length)
                return false;

            for (var i = 0; i < _segments.Length; i++)
                if (_segments[i].Equals(other._segments[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as CosmosPath);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_alias);
            foreach (var segment in _segments)
                hash.Add(segment);

            return hash.ToHashCode();
        }

        /// <summary>
        /// Returns the rendered Cosmos SQL path; for diagnostics, the element a traversal yields spelled as no request spells
        /// it, <c>c.tags[yield VARCHAR 0]</c>.
        /// </summary>
        /// <returns>The path text.</returns>
        public override string ToString()
        {
            var builder = new StringBuilder();
            if (HasYielded == false)
            {
                WriteTo(builder);
                return builder.ToString();
            }

            builder.Append(_alias);
            foreach (var segment in _segments)
            {
                if (segment.IsYielded)
                    builder.Append("[yield ").Append(segment.ElementType).Append(' ').Append(segment.Traversal.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                else if (segment.IsIndex)
                    CosmosSql.WriteIndexAccess(builder, segment.ArrayIndex);
                else
                    CosmosSql.WritePropertyAccess(builder, segment.Name!);
            }

            return builder.ToString();
        }

    }

}
