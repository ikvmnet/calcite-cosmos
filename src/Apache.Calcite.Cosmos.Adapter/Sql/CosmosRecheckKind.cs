namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// What a node of a rechecked conjunct is: see <see cref="CosmosRecheck"/>.
    /// </summary>
    public enum CosmosRecheckKind
    {

        /// <summary>
        /// An <c>AND</c>: true where every operand is.
        /// </summary>
        All,

        /// <summary>
        /// An <c>OR</c>: true where any operand is.
        /// </summary>
        Any,

        /// <summary>
        /// A comparison of a path with a parameter whose exactness the value decides, which the reader makes as Calcite
        /// does: <see cref="CosmosRecheck.Comparison"/> names it among the program's.
        /// </summary>
        Comparison,

        /// <summary>
        /// A predicate the service answers as Calcite does, read back from the member the statement selects it under.
        /// </summary>
        Predicate,

    }

}
