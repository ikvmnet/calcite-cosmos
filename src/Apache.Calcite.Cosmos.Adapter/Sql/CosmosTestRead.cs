namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A reading a one-place test's in-process body takes apart rather than evaluates, a <c>Calcite</c> row's (G8): the
    /// body is handed the document and the path the accessor names (<c>CosmosTypeTestImplementor</c>), so a type test or
    /// <c>IS_DEFINED</c> of a text, a raw value or a <c>RETURNING</c> asks what the document holds there, whatever the
    /// reading would have made of it. A cast is no accessor and no row names one. Read by
    /// <see cref="CosmosAxiomRows.ReadTestReads"/> into both graphs' <c>testreads</c>, which the predicate vocabulary's
    /// test rules read.
    /// </summary>
    /// <param name="Reader">The reading taken apart, by the constructor the IR writes it as: <c>text</c>, <c>raw</c> (through
    /// any column) or <c>typed</c> (whatever it returns).</param>
    /// <param name="As">The reading the body reads instead, by the name a reader takes: the raw value.</param>
    internal sealed record CosmosTestRead(string Reader, string As);

}
