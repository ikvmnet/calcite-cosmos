using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What each answer means to a <c>WHERE</c>: the outcomes a key holding it may give; what a value that is no boolean,
    /// as a whole condition, reads as where its row keeps it not, which of its outcomes stand as they are, and the rows
    /// that say so; and the roles an answer naming no outcome plays: the comparators that place a cell against a literal
    /// of its type, and those of them an affix matches by. Host data rules read, so that no C# maps an answer or names one
    /// by its role (F4).
    /// </summary>
    /// <param name="True">What <see cref="CosmosAnswer.True"/> means.</param>
    /// <param name="False">What <see cref="CosmosAnswer.False"/> means.</param>
    /// <param name="Undefined">What <see cref="CosmosAnswer.Undefined"/> means.</param>
    /// <param name="NotKept">What <see cref="CosmosAnswer.NotKept"/> means, its <c>WHERE NOT</c> not having been run.</param>
    /// <param name="Fails">What <see cref="CosmosAnswer.Fails"/> means.</param>
    /// <param name="NonBoolean">What a value that is no boolean, as a whole condition, reads as where its row keeps it not.</param>
    /// <param name="Comparators">The answers that name a comparator, an order key's: each places a cell's values against a
    /// literal or a second path, and the set's table carries those of a key's answers that are one
    /// (<see cref="MeasuredEntry.Placing"/>).</param>
    /// <param name="Matchers">The comparators an affix or a <c>LIKE</c> matches by, those the affix primitive evaluates
    /// (<see cref="MeasuredEntry.Matching"/>); a matcher's key that also names another comparator matches as nothing says
    /// (<see cref="MeasuredEntry.Unmatched"/>).</param>
    /// <param name="Stands">What a value that is no boolean, as a whole condition, keeps of its outcomes as they are; any
    /// other reads as <paramref name="NonBoolean"/> (<see cref="Whole"/>).</param>
    /// <param name="NonBooleanRows">The rows a whole condition over a value that is no boolean rests on.</param>
    internal sealed record AnswerMeanings(ServiceOutcomes True, ServiceOutcomes False, ServiceOutcomes Undefined, ServiceOutcomes NotKept, ServiceOutcomes Fails,
        ServiceOutcomes NonBoolean, ValueSet<CosmosAnswer> Comparators, ValueSet<CosmosAnswer> Matchers, ServiceOutcomes Stands, ValueSet<string> NonBooleanRows)
    {

        /// <summary>
        /// The meanings as measured: <see cref="CosmosAnswer.NotKept"/> is false or undefined, its <c>WHERE NOT</c> not
        /// having been run; a value that is no boolean, kept by neither <c>WHERE</c> nor <c>WHERE NOT</c>
        /// (<c>Service.BarePathKeepsOnlyTrue</c>, <c>Service.NotOverAPathKeepsOnlyFalse</c>), is undefined, where one kept,
        /// or failing the statement, is as it is; every order a row names is a comparator; and the code point orders, the
        /// service's and the emulator's of what it stores, are those an affix matches by (<c>Service.AffixesMatchByCodePoint</c>,
        /// <c>Service.LikeMatchesByCodePoint</c>).
        /// </summary>
        public static AnswerMeanings Measured { get; } = new(ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.Undefined,
            ServiceOutcomes.False | ServiceOutcomes.Undefined, ServiceOutcomes.Fails, ServiceOutcomes.Undefined,
            ValueSet.Of(CosmosAnswer.CodePoint, CosmosAnswer.Utf16, CosmosAnswer.Numeric, CosmosAnswer.StoredCodePoint, CosmosAnswer.NumericPointedAsDouble),
            ValueSet.Of(CosmosAnswer.CodePoint, CosmosAnswer.StoredCodePoint),
            ServiceOutcomes.True | ServiceOutcomes.Fails,
            ValueSet.Of("Service.BarePathKeepsOnlyTrue", "Service.NotOverAPathKeepsOnlyFalse"));

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

        /// <summary>
        /// What a value that is no boolean, as a whole condition, reads as of the outcomes its key gives: those that stand,
        /// and <see cref="NonBoolean"/> where any other may.
        /// </summary>
        public ServiceOutcomes Whole(ServiceOutcomes outcomes) =>
            (outcomes & Stands) | ((outcomes & ~Stands) != ServiceOutcomes.None ? NonBoolean : ServiceOutcomes.None);

    }

}
