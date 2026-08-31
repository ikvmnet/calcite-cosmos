namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// How one output field of a pushed-down statement is to be read back.
    /// </summary>
    /// <remarks>
    /// A statement's output fields are ordinarily read as the SQL type the plan declared for them, and
    /// that is what <see cref="Typed"/> says. The one exception is a projection that dropped a cast to
    /// text: the service returns the document value as it stands, and the reading is what puts the cast
    /// back. Recording it per ordinal is what keeps the two apart — the same <c>VARCHAR</c> column is
    /// read one way when it came from a string property and another when it came from a cast.
    /// </remarks>
    public enum CosmosReading
    {

        /// <summary>
        /// Read the value as the output field's declared SQL type.
        /// </summary>
        Typed,

        /// <summary>
        /// Read whatever JSON arrived and render it as text, the way Calcite's cast over an <c>ANY</c>
        /// value does. See <see cref="Client.CosmosJson.GetText"/>.
        /// </summary>
        Text,

    }

}
