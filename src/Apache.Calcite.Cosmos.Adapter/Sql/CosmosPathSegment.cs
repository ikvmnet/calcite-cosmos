using System;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A single step in a <see cref="CosmosPath"/>: either a property name or an array index.
    /// </summary>
    public readonly struct CosmosPathSegment : IEquatable<CosmosPathSegment>
    {

        /// <summary>
        /// Creates a segment that accesses a named property.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns>The segment.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        public static CosmosPathSegment Property(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), -1);

        /// <summary>
        /// Creates a segment that accesses an array element.
        /// </summary>
        /// <param name="index">The zero-based index.</param>
        /// <returns>The segment.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
        public static CosmosPathSegment Index(int index) => index >= 0 ? new(null, index) : throw new ArgumentOutOfRangeException(nameof(index));

        readonly string? _name;
        readonly int _index;

        CosmosPathSegment(string? name, int index)
        {
            _name = name;
            _index = index;
        }

        /// <summary>
        /// Gets whether this segment is an array index rather than a property name.
        /// </summary>
        public bool IsIndex => _name is null;

        /// <summary>
        /// Gets the property name, or <c>null</c> when <see cref="IsIndex"/> is <c>true</c>.
        /// </summary>
        public string? Name => _name;

        /// <summary>
        /// Gets the array index, or <c>-1</c> when <see cref="IsIndex"/> is <c>false</c>.
        /// </summary>
        public int ArrayIndex => _index;

        /// <inheritdoc />
        public bool Equals(CosmosPathSegment other) => _index == other._index && string.Equals(_name, other._name, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CosmosPathSegment other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(_name, _index);

    }

}
