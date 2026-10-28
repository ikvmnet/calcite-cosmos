using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What the rendering check is told of each conjunct the translator writes for a filter, where a test armed it
    /// (<see cref="CosmosRenderingCapture"/>).
    /// </summary>
    internal interface ICosmosRenderingObserver
    {

        /// <summary>A conjunct a round admitted: the text it rendered as, and the list, its own, that it bound into.</summary>
        void Admitted(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters);

        /// <summary>A conjunct written into the statement sent: the text appended, and the statement's list, shared with every clause.</summary>
        void Written(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters);

        /// <summary>
        /// A residual conjunct's weakening the split rule pushes beside the admitted ones: its text, and the list it bound
        /// into, which also holds what the attempts before it bound.
        /// </summary>
        void Weakened(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters);

        /// <summary>
        /// A statement whose rounds are being admitted, told once a round, whether any conjunct renders or not: which statements
        /// generate mode's run reads, and how often a statement admits.
        /// </summary>
        void Read(CosmosFilterStatement statement, AdmissionRound round);

    }

}
