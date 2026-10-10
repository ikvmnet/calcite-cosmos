namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// How an instant that falls between two of a stored form's spellings is written into it, so
    /// that a comparison against what is written answers what the comparison against the instant
    /// would have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A literal that does not land on the form is refused; a parameter cannot be.</b> The plan
    /// decides whether to rewrite a comparison against a literal while it is being made, with the
    /// value in hand, and a literal finer than the form keeps its comparison in process — see
    /// <see cref="CosmosTemporalForms.Render(CosmosRepresentation, System.DateTime)"/>. A parameter's
    /// value arrives after the statement is written, so the statement has to be right for every value
    /// it might be given, and a value between two stored spellings is one of them.
    /// </para>
    /// <para>
    /// <b>Every stored value is on the form's resolution, which is what makes a rounding exact rather
    /// than approximate.</b> Between two consecutive stored spellings there is nothing stored, so
    /// <c>s &gt; v</c> keeps exactly the values <c>s &gt; ⌊v⌋</c> keeps and <c>s &gt;= v</c> exactly
    /// the ones <c>s &gt;= ⌈v⌉</c> does. Which way to round is therefore a property of the
    /// comparison, fixed when the statement is written, and the value only decides how far.
    /// </para>
    /// </remarks>
    public enum CosmosTemporalRounding
    {

        /// <summary>
        /// For an equality or an inequality, which no stored value between two spellings satisfies or
        /// fails: the instant is written out in full, at a precision no spelling of the form has, so
        /// that it equals nothing stored.
        /// </summary>
        None,

        /// <summary>
        /// For <c>&gt;</c> and <c>&lt;=</c>: the latest spelling at or before the instant.
        /// </summary>
        Down,

        /// <summary>
        /// For <c>&gt;=</c> and <c>&lt;</c>: the earliest spelling at or after the instant.
        /// </summary>
        Up,

    }

}
