namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Which bound a parameter's slot holds in place of its value, where the value is compared with a path whose strings
    /// nothing states: see <see cref="CosmosOrderComparison"/>.
    /// </summary>
    public enum CosmosAnchorBound
    {

        /// <summary>
        /// The value itself.
        /// </summary>
        None,

        /// <summary>
        /// The value up to its first unit at or above U+D800, below which every string Calcite finds above the value
        /// lies at the service: <see cref="Metadata.CosmosServiceOrder.LowerBound"/>.
        /// </summary>
        Lower,

        /// <summary>
        /// That prefix with its last character raised by one, above which no string Calcite finds below the value lies
        /// at the service; <c>null</c> where the prefix is empty: <see cref="Metadata.CosmosServiceOrder.UpperBound"/>.
        /// </summary>
        Upper,

    }

}
