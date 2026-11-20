using System;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A single step in a <see cref="CosmosPath"/>: a property name, an array index, or the element a traversal of an array
    /// yields.
    /// </summary>
    /// <remarks>
    /// The third kind is the correlate rule's question's alone (<see cref="CosmosImplementor.TryBindElements"/>): a column
    /// that holds an element a traversal yields, bound to the array's path and this step, which the facts graph reads as a
    /// path of its own and no request, policy or document read can spell. A reader of a segment's kind declines it where it
    /// answers a question and throws where it would write it. See DESIGN.md, <i>An element a traversal yields is a path of
    /// its own</i>.
    /// </remarks>
    public readonly struct CosmosPathSegment : IEquatable<CosmosPathSegment>
    {

        /// <summary>
        /// Creates a segment that accesses a named property.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns>The segment.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        public static CosmosPathSegment Property(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), -1, null, null, -1);

        /// <summary>
        /// Creates a segment that accesses an array element.
        /// </summary>
        /// <param name="index">The zero-based index.</param>
        /// <returns>The segment.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
        public static CosmosPathSegment Index(int index) => index >= 0 ? new(null, index, null, null, -1) : throw new ArgumentOutOfRangeException(nameof(index));

        /// <summary>
        /// Creates the segment of the element a traversal of an array yields: its element type and the traversal's form, as
        /// data, and the traversal's number, so two traversals of one array are two segments.
        /// </summary>
        /// <param name="type">The element type, by its <c>SqlTypeName</c>'s name: <c>VARCHAR</c>.</param>
        /// <param name="form">The traversal's form: the collection's <c>SqlTypeName</c>'s name, and <c>WITH ORDINALITY</c> after it
        /// where the traversal is written so: <c>ARRAY</c>, <c>ARRAY WITH ORDINALITY</c>.</param>
        /// <param name="traversal">The traversal's number, from zero, in the order a binding walks a statement's traversals.</param>
        /// <returns>The segment.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> or <paramref name="form"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="traversal"/> is negative.</exception>
        internal static CosmosPathSegment Yielded(string type, string form, int traversal) =>
            new(null, -1, type ?? throw new ArgumentNullException(nameof(type)), form ?? throw new ArgumentNullException(nameof(form)),
                traversal >= 0 ? traversal : throw new ArgumentOutOfRangeException(nameof(traversal)));

        readonly string? _name;
        readonly int _index;
        readonly string? _type;
        readonly string? _form;
        readonly int _traversal;

        CosmosPathSegment(string? name, int index, string? type, string? form, int traversal)
        {
            _name = name;
            _index = index;
            _type = type;
            _form = form;
            _traversal = traversal;
        }

        /// <summary>
        /// Gets whether this segment is an array index: neither a property name nor the element a traversal yields.
        /// </summary>
        public bool IsIndex => _name is null && _type is null;

        /// <summary>
        /// Gets whether this segment is the element a traversal of an array yields, which no request, policy or document read
        /// can spell.
        /// </summary>
        public bool IsYielded => _type is not null;

        /// <summary>
        /// Gets the property name, or <c>null</c> for an index and for the element a traversal yields.
        /// </summary>
        public string? Name => _name;

        /// <summary>
        /// Gets the array index, or <c>-1</c> when <see cref="IsIndex"/> is <c>false</c>.
        /// </summary>
        public int ArrayIndex => _index;

        /// <summary>
        /// Gets the element type a traversal yields its element as, or <c>null</c> when <see cref="IsYielded"/> is <c>false</c>.
        /// </summary>
        internal string? ElementType => _type;

        /// <summary>
        /// Gets the form of the traversal that yields the element, or <c>null</c> when <see cref="IsYielded"/> is <c>false</c>.
        /// </summary>
        internal string? TraversalForm => _form;

        /// <summary>
        /// Gets the traversal's number, or <c>-1</c> when <see cref="IsYielded"/> is <c>false</c>.
        /// </summary>
        internal int Traversal => _traversal;

        /// <inheritdoc />
        public bool Equals(CosmosPathSegment other) =>
            _index == other._index && _traversal == other._traversal && string.Equals(_name, other._name, StringComparison.Ordinal)
            && string.Equals(_type, other._type, StringComparison.Ordinal) && string.Equals(_form, other._form, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CosmosPathSegment other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(_name, _index, _type, _form, _traversal);

    }

}
