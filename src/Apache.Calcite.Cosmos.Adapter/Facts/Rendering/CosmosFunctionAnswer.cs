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

        /// <summary>Undefined on some operand, where Calcite may answer a value.</summary>
        Undefined,

        /// <summary>
        /// Never measured on the target, where another target measured the key: S reads it as anything, written by S's
        /// load so that the rows are total over the targets and one rule reads a key no target of a set may answer
        /// otherwise (h10). No row states it: Calcite's library <c>LENGTH</c>, the one function declared so, has measured
        /// rows since rows2.
        /// </summary>
        Unmeasured,

        /// <summary>
        /// A value the service defines where Calcite has no function of its own to answer one, a service-defined (D-class)
        /// function's (F3): distinct from <see cref="Undefined"/>, which is undefined where Calcite may answer a value. The
        /// declared rows state it (<c>CosmosAxiomRows.Declared</c>, G6), and no key it answers disagrees.
        /// </summary>
        Defined,

    }

}
