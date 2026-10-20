namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What each answer means to a <c>WHERE</c>: the outcomes a key holding it may give. Host data a rule reads, so that
    /// no C# maps an answer.
    /// </summary>
    internal sealed record AnswerMeanings(ServiceOutcomes True, ServiceOutcomes False, ServiceOutcomes Undefined, ServiceOutcomes NotKept, ServiceOutcomes Fails)
    {

        /// <summary>
        /// The meanings as measured: <see cref="CosmosAnswer.NotKept"/> is false or undefined, its <c>WHERE NOT</c> not
        /// having been run.
        /// </summary>
        public static AnswerMeanings Measured { get; } = new(ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.Undefined,
            ServiceOutcomes.False | ServiceOutcomes.Undefined, ServiceOutcomes.Fails);

        /// <summary>The outcomes an answer means; none for one that names a comparator.</summary>
        public ServiceOutcomes Of(CosmosAnswer answer) => answer switch
        {
            CosmosAnswer.True => True,
            CosmosAnswer.False => False,
            CosmosAnswer.Undefined => Undefined,
            CosmosAnswer.NotKept => NotKept,
            CosmosAnswer.Fails => Fails,
            _ => ServiceOutcomes.None,
        };

    }

}
