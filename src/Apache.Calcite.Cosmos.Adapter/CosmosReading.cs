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
        /// <c>DOC</c> column is, and the reason it costs nothing: the document arrived as JSON, so
        /// the column is that JSON as the service sent it, rather than anything rendered back into it.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Text"/>, which renders a value the way Calcite's cast over an
        /// <c>ANY</c> would — <c>{x=1}</c> for an object, Java's notation rather than JSON's. That is
        /// the right answer for a dropped cast and the wrong one for a document.
        /// </remarks>
        Json,

        /// <summary>
        /// Read whatever JSON arrived and write it back as compact JSON text, which is what
        /// <c>JSON_QUERY</c> answers. See <see cref="Client.CosmosJson.GetJsonTextProperty"/>.
        /// </summary>
        /// <remarks>
        /// <b>Distinct from <see cref="Json"/>, and the difference is whitespace.</b> That one hands
        /// back the service's own bytes, which is right for the document column — it cannot then
        /// differ from what is stored. This one is a value inside a document being returned as text
        /// by an operator whose in-process form re-serialises: measured, Calcite answers
        /// <c>["a","b"]</c> for a path stored as <c>[ "a" ,   "b" ]</c>. Handing the raw bytes over
        /// would make the pushed column differ from the in-process one by exactly the spaces the
        /// document happened to carry.
        /// </remarks>
        JsonText,

    }

}
