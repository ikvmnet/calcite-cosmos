namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What <see cref="CosmosServiceOrder"/> answered.
    /// </summary>
    /// <remarks>
    /// Three answers. A sort split by JSON type into two statements is another the plan for allowable orders adds; until
    /// then what neither the plan nor the rows can prove is refused, which costs an in-process sort and never a wrong one.
    /// </remarks>
    public enum CosmosOrderVerdictKind
    {

        /// <summary>
        /// The service's order is one Calcite allows, for every database the facts admit.
        /// </summary>
        Proved,

        /// <summary>
        /// The service's order is one Calcite allows around every value the verdict's anchors admit, and the reader
        /// checks the values as the rows arrive: where they hold, the service's answer is used; where they do not, a
        /// fallback statement is read in its place. A proof completed at run time, which counts as one.
        /// </summary>
        Anchored,

        /// <summary>
        /// Nothing the facts say proves it, nor anything the rows could, and the order stays with Calcite.
        /// </summary>
        Refused,

    }

}
