namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The class of an operand's value a function row is keyed by beside its kind and band (F2): what a row measured over
    /// some values of an operand alone — a start below 1 or from 1, a length below 0, a count's sign, a search empty or
    /// not. Each family partitions its kind (<see cref="FunctionValueClasses.Families"/>); a row keyed
    /// <see cref="Any"/> holds over every value of its kind and band.
    /// </summary>
    internal enum FunctionValueClass
    {

        /// <summary>Every value of the key's kind and band: a row that names no class of value.</summary>
        Any,

        /// <summary>A number below 0.</summary>
        Negative,

        /// <summary>The number 0.</summary>
        Zero,

        /// <summary>A number above 0.</summary>
        Positive,

        /// <summary>A number below 1.</summary>
        BelowOne,

        /// <summary>A number of at least 1.</summary>
        AtLeastOne,

        /// <summary>The empty string.</summary>
        Empty,

        /// <summary>A string of at least one unit.</summary>
        NonEmpty,

    }

}
