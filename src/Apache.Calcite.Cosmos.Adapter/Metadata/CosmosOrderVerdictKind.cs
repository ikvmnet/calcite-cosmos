namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What <see cref="CosmosServiceOrder"/> answered.
    /// </summary>
    /// <remarks>
    /// Two answers today, both decided when the plan is made. A proof completed when the rows arrive, and an order
    /// split by class into two statements, are each another answer the plan for allowable orders adds; until then
    /// what cannot be proved here is refused, which costs an in-process sort and never a wrong one.
    /// </remarks>
    public enum CosmosOrderVerdictKind
    {

        /// <summary>
        /// The service's order is one Calcite allows, for every database the facts admit.
        /// </summary>
        Proved,

        /// <summary>
        /// Nothing the facts say proves it, and the order stays with Calcite.
        /// </summary>
        Refused,

    }

}
