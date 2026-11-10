namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A cast a <c>Calcite</c> row measured, by its target type and the reading it converts (F3): a cast of the accessor to
    /// <c>INTEGER</c>, <c>BIGINT</c>, <c>DOUBLE</c> or <c>BOOLEAN</c>, and of a raw value to <c>VARCHAR</c>. Read by
    /// <see cref="CosmosAxiomRows.ReadCasts"/> into S's <c>measuredcast</c>, citing its row; a cast no row names is unmeasured.
    /// </summary>
    /// <param name="Target">The type cast to.</param>
    /// <param name="Reader">The reading cast, by the name a reader takes (<c>PredicateAtom.TextReader</c>, <c>PredicateAtom.RawReader</c>).</param>
    internal sealed record CosmosCast(string Target, string Reader);

}
