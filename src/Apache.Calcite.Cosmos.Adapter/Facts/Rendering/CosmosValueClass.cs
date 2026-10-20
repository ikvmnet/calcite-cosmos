namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A class of operand or of value, the second and third columns of a measured row's key. A row is a claim about every
    /// value of its class, so each class is measured with a witness of each type that could answer differently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>As an operand</b>, the class of the literal compared with, or <see cref="None"/>. <b>As a cell</b>, the kind of
    /// value the path holds; against a literal of its own type, where it lies under the type's comparator.
    /// </para>
    /// <para>
    /// <b>For a connective</b>, the operand is its first side's outcome and the cell its second's: <see cref="True"/>,
    /// <see cref="False"/>, <see cref="Undefined"/>, or the kind of a value that is no boolean, which a path written bare
    /// may hold.
    /// </para>
    /// </remarks>
    internal enum CosmosValueClass
    {

        None,
        Absent,
        Null,
        True,
        False,
        Undefined,
        Number,
        String,
        Object,
        Array,

        /// <summary>The string <c>""</c>, below which there is no string.</summary>
        EmptyString,

        /// <summary>A value of the literal's type ordered before it.</summary>
        Below,

        /// <summary>A value of the literal's type ordered with it.</summary>
        Equal,

        /// <summary>A value of the literal's type ordered after it.</summary>
        Above,

        /// <summary>A string an affix or a pattern matches.</summary>
        Matches,

        /// <summary>A string an affix or a pattern does not match.</summary>
        Misses,

        /// <summary>Two bounds the service's order and Calcite's rank alike.</summary>
        Agreeing,

        /// <summary>Two bounds the two orders rank apart.</summary>
        Parted,

        /// <summary>A value between a range's bounds.</summary>
        Between,

        /// <summary>A value outside a range's bounds.</summary>
        Outside,

    }

}
