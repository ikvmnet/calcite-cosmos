namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What the service stored of a string written holding characters of one class: that what is stored may hold
    /// characters of another, read by <see cref="CosmosAxiomRows.ReadStored"/> into the rows the rules of JSON hold. It
    /// names no target: the rules of JSON have none and read every target's row alike, and which target stored it so is
    /// its axiom's <see cref="CosmosAxiom.MeasuredOn"/>.
    /// </summary>
    /// <param name="Written">The class written, by its member (<c>JsonValues.CharacterClasses</c>).</param>
    /// <param name="Stored">The class what is stored may hold, by its member.</param>
    internal sealed record CosmosStoredClass(string Written, string Stored);

}
