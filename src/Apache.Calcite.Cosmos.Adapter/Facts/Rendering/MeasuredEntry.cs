using System;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One key of a set's table: every answer the targets the set serves gave for it, the union of what they mean — none
    /// where every answer names a comparator — and the rows that say so; and, as S's rule settles them from the meanings'
    /// roles (F4), the answers that place a cell, those an affix matches by, and those no matcher evaluates.
    /// </summary>
    internal sealed record MeasuredEntry(MeasuredKey Key, ValueSet<CosmosAnswer> Answers, ServiceOutcomes Outcomes, ValueSet<string> Evidence) : IComparable<MeasuredEntry>
    {

        /// <summary>The entry of a settled key, its outcomes read through the meanings.</summary>
        public static MeasuredEntry Of(MeasuredKey key, ValueSet<CosmosAnswer> answers, ValueSet<string> evidence, AnswerMeanings means) =>
            new(key, answers, answers.Aggregate(ServiceOutcomes.None, (outcomes, answer) => outcomes | means.Of(answer)), evidence);

        /// <summary>An entry with the roles S's rule settled of its answers (<see cref="AnswerMeanings.Comparators"/>, <see cref="AnswerMeanings.Matchers"/>).</summary>
        public static MeasuredEntry Placed(MeasuredEntry entry, ValueSet<CosmosAnswer> placing, ValueSet<CosmosAnswer> matching, ValueSet<CosmosAnswer> unmatched) =>
            entry with { Placing = placing, Matching = matching, Unmatched = unmatched };

        /// <summary>The answers that name a comparator, each of which places a cell; none where the key orders nothing.</summary>
        public ValueSet<CosmosAnswer> Placing { get; init; } = ValueSet<CosmosAnswer>.Empty;

        /// <summary>The comparators an affix or a <c>LIKE</c> matches by among the answers.</summary>
        public ValueSet<CosmosAnswer> Matching { get; init; } = ValueSet<CosmosAnswer>.Empty;

        /// <summary>The comparators among the answers no matcher evaluates, beside which a match says nothing.</summary>
        public ValueSet<CosmosAnswer> Unmatched { get; init; } = ValueSet<CosmosAnswer>.Empty;

        /// <inheritdoc />
        public int CompareTo(MeasuredEntry? other) =>
            other is null ? 1 : Key.CompareTo(other.Key) is var order and not 0 ? order : string.CompareOrdinal(Rest, other.Rest);

        /// <summary>Everything but the key, spelled one way, which the order reads after it: the roles too, so that the order
        /// parts what equality parts.</summary>
        string Rest => (int)Outcomes + "\n" + string.Join(",", Answers.AsEnumerable()) + "\n" + string.Join("\n", Evidence.AsEnumerable())
            + "\n" + string.Join(",", Placing.AsEnumerable()) + "\n" + string.Join(",", Matching.AsEnumerable()) + "\n" + string.Join(",", Unmatched.AsEnumerable());

    }

}
