namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What Calcite's call answers over the operands a function key holds (G6): a value, or null, which the service reads
    /// as undefined and S's image as absent. Stated by each function row beside its answer, so a function that is null over
    /// some string says so, and read by S's image rules, never derived from the function's name or its result's type.
    /// </summary>
    internal enum CalciteYield
    {

        /// <summary>A value of the call's type, over every operand the key holds.</summary>
        Value,

        /// <summary>Null over some operand the key holds: what the image holds absent.</summary>
        Null,

    }

}
