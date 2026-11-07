using System;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// One key of a set's table: every answer the targets the set serves gave for it, the union of what they mean — none
    /// where every answer names a comparator — and the rows that say so; and, as S's roles rules derive them (G2), the
    /// answers that read the key, each placing or matching a cell, and how each reads a whole number.
    /// </summary>
    internal sealed record MeasuredEntry(MeasuredKey Key, ValueSet<CosmosAnswer> Answers, ServiceOutcomes Outcomes, ValueSet<string> Evidence) : IComparable<MeasuredEntry>
    {

        /// <summary>The entry of a settled key, its outcomes read through the meanings.</summary>
        public static MeasuredEntry Of(MeasuredKey key, ValueSet<CosmosAnswer> answers, ValueSet<string> evidence, AnswerMeanings means) =>
            new(key, answers, answers.Aggregate(ServiceOutcomes.None, (outcomes, answer) => outcomes | means.Of(answer)), evidence);

        /// <summary>An entry with what S's roles rules derived of it: its readers and their readings of a whole number.</summary>
        public static MeasuredEntry Read(MeasuredEntry entry, ValueSet<CosmosAnswer> readers, ValueSet<(CosmosAnswer, string)> readings) =>
            entry with { Readers = readers, Readings = readings };

        /// <summary>
        /// The answers that read the key, each of which places a cell (an order's comparators) or matches one (an affix's
        /// matchers); none where S found none, or a comparator beside them none of its role (<see cref="AnswerMeanings.ReadBy"/>).
        /// </summary>
        public ValueSet<CosmosAnswer> Readers { get; init; } = ValueSet<CosmosAnswer>.Empty;

        /// <summary>How each reader reads a whole number within int64, <c>value</c> or <c>double</c>, as its rows say (G2).</summary>
        public ValueSet<(CosmosAnswer, string)> Readings { get; init; } = ValueSet<(CosmosAnswer, string)>.Empty;

        /// <inheritdoc />
        public int CompareTo(MeasuredEntry? other) =>
            other is null ? 1 : Key.CompareTo(other.Key) is var order and not 0 ? order : string.CompareOrdinal(Rest, other.Rest);

        /// <summary>Everything but the key, spelled one way, which the order reads after it: the readers too, so that the order
        /// parts what equality parts.</summary>
        string Rest => (int)Outcomes + "\n" + string.Join(",", Answers.AsEnumerable()) + "\n" + string.Join("\n", Evidence.AsEnumerable())
            + "\n" + string.Join(",", Readers.AsEnumerable()) + "\n" + string.Join(",", Readings.AsEnumerable());

    }

}
