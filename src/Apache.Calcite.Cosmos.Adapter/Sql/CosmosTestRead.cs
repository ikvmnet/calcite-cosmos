namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A reading a one-place test's in-process body takes apart rather than evaluates, or raises over, a <c>Calcite</c> row's
    /// (G8): the body is handed the document and the path the accessor names (<c>CosmosTypeTestImplementor</c>), so a type
    /// test or <c>IS_DEFINED</c> of a text, a raw value or a <c>RETURNING</c> asks what the document holds there, whatever the
    /// reading would have made of it, and over a field or a call, which it cannot take apart, it raises. A cast is no
    /// accessor and no row names one. Read by <see cref="CosmosAxiomRows.ReadTestReads"/> into both graphs'
    /// <c>testreads</c>, which the predicate vocabulary's test rules read.
    /// </summary>
    /// <param name="Reader">The reading, by the constructor the IR writes it as: <c>text</c>, <c>raw</c>, <c>typed</c>
    /// (whatever it returns) or <c>call</c> (whatever its function), or a raw value in a column by its own reader
    /// (<c>raw:VARIANT</c>).</param>
    /// <param name="As">What the body reads instead, by the name a reader takes: the raw value, or <c>raises</c>.</param>
    internal sealed record CosmosTestRead(string Reader, string As);

}
