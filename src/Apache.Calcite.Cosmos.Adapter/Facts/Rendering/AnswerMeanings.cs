namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What each answer means to a <c>WHERE</c>: the outcomes a key holding it may give; and what a value that is no
    /// boolean, as a whole condition, reads as where its row keeps it not. Host data rules read, so that no C# maps an
    /// answer.
    /// </summary>
    internal sealed record AnswerMeanings(ServiceOutcomes True, ServiceOutcomes False, ServiceOutcomes Undefined, ServiceOutcomes NotKept, ServiceOutcomes Fails,
        ServiceOutcomes NonBoolean)
    {

        /// <summary>
        /// The meanings as measured: <see cref="CosmosAnswer.NotKept"/> is false or undefined, its <c>WHERE NOT</c> not
        /// having been run; and a value that is no boolean, kept by neither <c>WHERE</c> nor <c>WHERE NOT</c>
        /// (<c>Service.BarePathKeepsOnlyTrue</c>, <c>Service.NotOverAPathKeepsOnlyFalse</c>), is undefined.
        /// </summary>
        public static AnswerMeanings Measured { get; } = new(ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.Undefined,
            ServiceOutcomes.False | ServiceOutcomes.Undefined, ServiceOutcomes.Fails, ServiceOutcomes.Undefined);

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
