using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Stands in a bound parameter for a value the plan does not have: the ordinal of one of the
    /// statement's own dynamic parameters, whose value arrives with the execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A prepared statement is compiled once and run many times, so a value written as <c>?</c> is not
    /// a value at plan time — it is an ordinal. The statement text is finished without it, the
    /// parameter is bound under a name like every other, and only the slot behind the name is left
    /// open. <see cref="Client.CosmosQueries.Bind"/> closes it, reading the value out of the data
    /// context the execution supplies.
    /// </para>
    /// <para>
    /// The ordinal is all that is carried, because the ordinal is all the plan knows. What the value
    /// will be is the caller's, and what type it is the plan already decided — which is why nothing
    /// here records one.
    /// </para>
    /// </remarks>
    /// <param name="Ordinal">The dynamic parameter's index, as <c>RexDynamicParam</c> numbers them.</param>
    public readonly record struct CosmosDynamicValue(int Ordinal)
    {

        /// <summary>
        /// Gets the name Calcite binds the value under in the data context.
        /// </summary>
        /// <remarks>
        /// <c>?0</c>, <c>?1</c> and so on — the spelling Calcite's own generated code uses for the
        /// same lookup, so a plan reading it here and a plan reading it there see one value.
        /// </remarks>
        public string VariableName => "?" + Ordinal.ToString(CultureInfo.InvariantCulture);

    }

}
