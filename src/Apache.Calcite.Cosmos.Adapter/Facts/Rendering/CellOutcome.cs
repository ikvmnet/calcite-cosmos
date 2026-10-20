namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What the service makes of a form on one cell.
    /// </summary>
    /// <param name="Outcomes">What a <c>WHERE</c> may read; never none.</param>
    /// <param name="NonBoolean">
    /// The class of the value a path written bare holds where it is no boolean, which a connective over it keys its row
    /// by; <see cref="CosmosValueClass.None"/> otherwise.
    /// </param>
    internal sealed record CellOutcome(ServiceOutcomes Outcomes, CosmosValueClass NonBoolean);

}
