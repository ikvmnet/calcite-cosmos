using System;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One key of a set's table: every answer the targets the set serves gave for it, the union of what they mean — none
    /// where every answer names a comparator — and the rows that say so.
    /// </summary>
    internal sealed record MeasuredEntry(MeasuredKey Key, ValueSet<CosmosAnswer> Answers, ServiceOutcomes Outcomes, ValueSet<string> Evidence) : IComparable<MeasuredEntry>
    {

        /// <summary>The entry of a settled key, its outcomes read through the meanings.</summary>
        public static MeasuredEntry Of(MeasuredKey key, ValueSet<CosmosAnswer> answers, ValueSet<string> evidence, AnswerMeanings means) =>
            new(key, answers, answers.Aggregate(ServiceOutcomes.None, (outcomes, answer) => outcomes | means.Of(answer)), evidence);

        /// <inheritdoc />
        public int CompareTo(MeasuredEntry? other) =>
            other is null ? 1 : Key.CompareTo(other.Key) is var order and not 0 ? order : string.CompareOrdinal(Rest, other.Rest);

        /// <summary>Everything but the key, spelled one way, which the order reads after it.</summary>
        string Rest => (int)Outcomes + "\n" + string.Join(",", Answers.AsEnumerable()) + "\n" + string.Join("\n", Evidence.AsEnumerable());

    }

}
