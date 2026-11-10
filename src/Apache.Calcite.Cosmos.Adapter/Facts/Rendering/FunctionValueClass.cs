namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The class of an operand's value a function row is keyed by beside its kind and band (F2): what a row measured over
    /// some values of an operand alone — a start below 1 or from 1, a length below 0, a count's sign, a search empty or
    /// not — or of its result, a string's length against the bound the service repeats to. A row keyed <see cref="Any"/> holds over every value of its kind and band. Each class holds some atoms, the
    /// finest classes, one a value (<see cref="FunctionValueClasses.Holding"/>): a call's cell is keyed by the atoms its
    /// values are in, and S reads a row as covering each atom its class holds.
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

        /// <summary>A number above 0 and below 1: the atom where <see cref="Positive"/> and <see cref="BelowOne"/> meet.</summary>
        PositiveBelowOne,

        /// <summary>
        /// A string of at most <see cref="FunctionValueClasses.RepetitionBound"/> code points: a call's result, which a
        /// function's rows key by its length where the service bounds what it makes (<see cref="FunctionKey.Result"/>).
        /// </summary>
        WithinRepetitionBound,

        /// <summary>A string of more than <see cref="FunctionValueClasses.RepetitionBound"/> code points: a call's result.</summary>
        PastRepetitionBound,

    }

}
