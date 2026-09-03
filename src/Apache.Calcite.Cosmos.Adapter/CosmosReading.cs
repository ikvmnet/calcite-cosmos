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

        /// <summary>
        /// Read whatever JSON arrived as its own text, exactly as the service sent it. What the
        /// <c>_JSON</c> column is, and the reason it costs nothing: the document arrived as JSON, so
        /// the column is that JSON rather than the map rendered back into it.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Text"/>, which renders a value the way Calcite's cast over an
        /// <c>ANY</c> would — <c>{x=1}</c> for an object, Java's notation rather than JSON's. That is
        /// the right answer for a dropped cast and the wrong one for a document.
        /// </remarks>
        Json,

    }

}
