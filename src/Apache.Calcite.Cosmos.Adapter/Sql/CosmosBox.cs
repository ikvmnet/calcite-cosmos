namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The box a value is held in when it is cast to <c>VARIANT</c>, a <c>Calcite</c> row's (F3): a stored integer is boxed
    /// <c>BIGINT</c>, a fraction <c>DOUBLE</c>, a string and a text reading a string. Read by
    /// <see cref="CosmosAxiomRows.ReadBoxes"/> into S's <c>boxof</c>; within one box a comparison the row names reads the
    /// values inside (<see cref="CosmosAxiom.SeesThrough"/>).
    /// </summary>
    /// <param name="Kind">A reading, by the name a reader takes (<c>PredicateAtom.TextReader</c>, <c>PredicateAtom.TypedReader</c>),
    /// a kind of stored value, by its type (<c>JsonValues.IntegerType</c>), or a value cast, a literal among them, by its SQL
    /// type (<c>BIGINT</c>, <c>CHAR</c>).</param>
    /// <param name="Box">The box, by its SQL type.</param>
    internal sealed record CosmosBox(string Kind, string Box);

}
