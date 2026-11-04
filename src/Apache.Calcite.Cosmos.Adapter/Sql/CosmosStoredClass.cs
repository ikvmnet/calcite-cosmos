namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What the targets in <see cref="On"/> stored of a string written holding characters of one class: that what is stored
    /// may hold characters of another, read by <see cref="CosmosAxiomRows.ReadStored"/> into the rows the rules of JSON hold.
    /// </summary>
    /// <param name="Written">The class written, by its member (<c>JsonValues.CharacterClasses</c>).</param>
    /// <param name="Stored">The class what is stored may hold, by its member.</param>
    /// <param name="On">The targets that stored it so.</param>
    internal sealed record CosmosStoredClass(string Written, string Stored, CosmosAxiomTarget On);

}
