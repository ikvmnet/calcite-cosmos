using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What each answer means to a <c>WHERE</c>: the outcomes a key holding it may give; what a value that is no boolean,
    /// as a whole condition, reads as where its row keeps it not, which of its outcomes stand as they are, and the rows
    /// that say so; and the roles an answer naming no outcome plays: the comparators that place a cell against a literal
    /// of its type, those of them an affix matches by, and which test's entry each reads. Host data rules read, so that no
    /// C# maps an answer or names one by its role (F4, G2).
    /// </summary>
    /// <param name="True">What <see cref="CosmosAnswer.True"/> means.</param>
    /// <param name="False">What <see cref="CosmosAnswer.False"/> means.</param>
    /// <param name="Undefined">What <see cref="CosmosAnswer.Undefined"/> means.</param>
    /// <param name="NotKept">What <see cref="CosmosAnswer.NotKept"/> means, its <c>WHERE NOT</c> not having been run.</param>
    /// <param name="Fails">What <see cref="CosmosAnswer.Fails"/> means.</param>
    /// <param name="NonBoolean">What a value that is no boolean, as a whole condition, reads as where its row keeps it not.</param>
    /// <param name="Comparators">The answers that name a comparator, an order key's: each places a cell's values against a
    /// literal or a second path.</param>
    /// <param name="Matchers">The comparators an affix or a <c>LIKE</c> matches by, those the affix primitive evaluates.</param>
    /// <param name="Stands">What a value that is no boolean, as a whole condition, keeps of its outcomes as they are; any
    /// other reads as <paramref name="NonBoolean"/> (<see cref="Read"/>).</param>
    /// <param name="NonBooleanRows">The rows a whole condition over a value that is no boolean rests on.</param>
    /// <param name="ReadBy">The answers that read each test's entry over each operand class: a string order's comparators the
    /// order of strings', a number order's the order of numbers', the matchers an affix's or a <c>LIKE</c>'s over a string.
    /// S's roles rule hands a key those of its answers that read its test over its operand, where it names one and no
    /// comparator beside them, so that a comparator a row names for another kind's order reads nothing
    /// (<see cref="MeasuredEntry.Readers"/>, G2).</param>
    internal sealed record AnswerMeanings(ServiceOutcomes True, ServiceOutcomes False, ServiceOutcomes Undefined, ServiceOutcomes NotKept, ServiceOutcomes Fails,
        ServiceOutcomes NonBoolean, ValueSet<CosmosAnswer> Comparators, ValueSet<CosmosAnswer> Matchers, ServiceOutcomes Stands, ValueSet<string> NonBooleanRows,
        ValueSet<(CosmosServiceTest Test, CosmosValueClass Operand, CosmosAnswer Answer)> ReadBy)
    {

        static readonly ValueSet<CosmosAnswer> StringOrders = ValueSet.Of(CosmosAnswer.CodePoint, CosmosAnswer.Utf16, CosmosAnswer.StoredCodePoint);

        static readonly ValueSet<CosmosAnswer> NumberOrders = ValueSet.Of(CosmosAnswer.Numeric, CosmosAnswer.NumericPointedAsDouble);

        static readonly ValueSet<CosmosAnswer> Matching = ValueSet.Of(CosmosAnswer.CodePoint, CosmosAnswer.StoredCodePoint);

        /// <summary>
        /// The meanings as measured: <see cref="CosmosAnswer.NotKept"/> is false or undefined, its <c>WHERE NOT</c> not
        /// having been run; a value that is no boolean, kept by neither <c>WHERE</c> nor <c>WHERE NOT</c>
        /// (<c>Service.BarePathKeepsOnlyTrue</c>, <c>Service.NotOverAPathKeepsOnlyFalse</c>), is undefined, where one kept,
        /// or failing the statement, is as it is; every order a row names is a comparator; and the code point orders, the
        /// service's and the emulator's of what it stores, are those an affix matches by (<c>Service.AffixesMatchByCodePoint</c>,
        /// <c>Service.LikeMatchesByCodePoint</c>); the code point, UTF-16 and stored code point orders read the order of
        /// strings' entry, the two numeric orders the order of numbers', and every matcher the entry of each affix and of
        /// <c>LIKE</c> over a string.
        /// </summary>
        public static AnswerMeanings Measured { get; } = new(ServiceOutcomes.True, ServiceOutcomes.False, ServiceOutcomes.Undefined,
            ServiceOutcomes.False | ServiceOutcomes.Undefined, ServiceOutcomes.Fails, ServiceOutcomes.Undefined, StringOrders.Union(NumberOrders), Matching,
            ServiceOutcomes.True | ServiceOutcomes.Fails,
            ValueSet.Of("Service.BarePathKeepsOnlyTrue", "Service.NotOverAPathKeepsOnlyFalse"),
            ValueSet.From(StringOrders.AsEnumerable().Select(c => (Test: CosmosServiceTest.Order, Operand: CosmosValueClass.String, Answer: c))
                .Concat(NumberOrders.AsEnumerable().Select(c => (Test: CosmosServiceTest.Order, Operand: CosmosValueClass.Number, Answer: c)))
                .Concat(from test in new[] { CosmosServiceTest.StartsWith, CosmosServiceTest.EndsWith, CosmosServiceTest.Contains, CosmosServiceTest.Like }
                        from matcher in Matching.AsEnumerable()
                        select (Test: test, Operand: CosmosValueClass.String, Answer: matcher))));

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
        /// The class a cell holding a value that is no boolean is read in as a whole condition, which S's rule applies to such a
        /// class: the outcomes that stand, and <see cref="NonBoolean"/> where any other may.
        /// </summary>
        public CellClass Read(CellClass raw) => raw with
        {
            Service = (raw.Service & Stands) | ((raw.Service & ~Stands) != ServiceOutcomes.None ? NonBoolean : ServiceOutcomes.None),
            NonBoolean = false,
        };

    }

}
