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
    /// The ordinal is what is carried, because the ordinal is what the plan knows of the value. What
    /// the value will be is the caller's, and what type it is the plan already decided — which is why
    /// nothing here records one. The one addition is a spelling the plan chose from the path the value
    /// is compared with, which <see cref="Form"/> says more about.
    /// </para>
    /// </remarks>
    /// <param name="Ordinal">The dynamic parameter's index, as <c>RexDynamicParam</c> numbers them.</param>
    public readonly record struct CosmosDynamicValue(int Ordinal)
    {

        /// <summary>
        /// Gets the stored form an instant is to be written in when it arrives, or <c>null</c> where the
        /// value is bound as it stands.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The one case where the value is not bound as it stands, and why it is this one.</b> A
        /// comparison of a path read as an instant against a literal is lowered while the plan is
        /// made, the literal written in the path's declared spelling — see
        /// <see cref="Metadata.CosmosFactRewriter"/>. Against a parameter there is nothing to write
        /// yet, and the instant itself is no value the service could compare a string with. So the
        /// slot carries the spelling too, and <see cref="Client.CosmosQueries.Bind"/> writes the value
        /// into it when it arrives, as the plan would have written the literal.
        /// </para>
        /// <para>
        /// Still a decision about types and not about the value: the plan knows the path's form and
        /// which comparison the parameter sits in, and both are settled before any value exists.
        /// </para>
        /// </remarks>
        public Metadata.CosmosRepresentation? Form { get; init; }

        /// <summary>
        /// Gets which way an instant between two of <see cref="Form"/>'s spellings is rounded, which
        /// the comparison it is bound into decides.
        /// </summary>
        public Metadata.CosmosTemporalRounding Rounding { get; init; }

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
