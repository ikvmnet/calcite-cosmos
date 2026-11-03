namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What one target computes for one function key (<see cref="FunctionKey"/>) against Calcite's own answer, over every
    /// operand of the key's kind and band its measurement holds; null and undefined alike, as a reader reads them.
    /// </summary>
    internal enum CosmosFunctionAnswer
    {

        /// <summary>Calcite's value on every operand measured.</summary>
        Same,

        /// <summary>A value other than Calcite's on some operand.</summary>
        Differs,

        /// <summary>The statement fails on some operand, where Calcite answers.</summary>
        Fails,

        /// <summary>The statement is refused before it runs, whatever the operand.</summary>
        Refuses,

        /// <summary>Undefined on some operand, where Calcite may answer a value: what the service defines alone, too.</summary>
        Undefined,

    }

}
