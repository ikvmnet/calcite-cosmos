using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// What an outcome's cells are measured over: the one literal each class of operand is compared with, and the values,
    /// as JSON, a path holds in each class of cell; <c>null</c> for none, an absent path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A class is claimed from its witnesses, so each is witnessed as the measurements that first ran it were</b> — a
    /// string spelling a boolean and a number that might read as one beside the plain ones — and widened where a type
    /// could matter: <c>false</c> and a fraction.
    /// </para>
    /// <para>
    /// <b>Below, at and above a literal are by code point,</b> the comparator Azure's order names, and every witness either
    /// side lies there by UTF-16 unit as well or tells the two apart. None holds U+0000: the emulator orders it as it
    /// stores it, so its side would hang on the target, which a catalogue that never asks cannot know. The order's
    /// pinning test measures that apart.
    /// </para>
    /// </remarks>
    internal static class CosmosOutcomeWitnesses
    {

        static readonly Dictionary<CosmosValueClass, string?[]> Kinds = new()
        {
            [Absent] = [null],
            [Null] = ["null"],
            [True] = ["true"],
            [False] = ["false"],
            [Number] = ["1", "30", "30.5"],
            [String] = ["\"bikes\"", "\"true\""],
            [Object] = ["{\"x\":1}"],
            [Array] = ["[1]"],
        };

        /// <summary>
        /// Each literal's sides, and under <see cref="None"/> the values an order is measured over: strings either side of
        /// U+D800, which the two orders rank apart; integers past 2^53, where a double ties neighbours, up to 2^63 - 1.
        /// </summary>
        static readonly Dictionary<CosmosValueClass, Dictionary<CosmosValueClass, string?[]>> Sides = new()
        {
            [String] = new()
            {
                [Below] = ["\"\"", "\"B\"", "\"a\"", "\"bikes\"", "\"\\ud7ff\""],
                [Equal] = ["\"\\ue000\""],
                [Above] = ["\"\\uff21\"", "\"\\ud83d\\ude00\""],
                [None] = ["\"\\ud7ff\"", "\"a\"", "\"B\"", "\"\\ue000\"", "\"\\uff21\"", "\"\\ud83d\\ude00\"", "\"\""],
            },
            [Number] = new()
            {
                [Below] = ["30", "9007199254740992"],
                [Equal] = ["9007199254740993"],
                [Above] = ["9007199254740994", "9223372036854775807"],
                [None] = ["9007199254740992", "9007199254740993", "9007199254740994", "9223372036854775806", "9223372036854775807"],
            },
        };

        /// <summary>
        /// The literal of an operand's class, as JSON: U+E000, where code point and UTF-16 order part; 2^53 + 1, where a
        /// double and an int64 part; <c>true</c>. <c>null</c> where a test takes none.
        /// </summary>
        public static string? Literal(CosmosValueClass operand) => operand switch
        {
            String => "\"\\ue000\"",
            Number => "9007199254740993",
            True => "true",
            _ => null,
        };

        /// <summary>The witnesses of a cell against an operand: a kind's, or a side of the operand's literal.</summary>
        public static IReadOnlyList<string?> Of(CosmosValueClass operand, CosmosValueClass cell) =>
            Sides.TryGetValue(operand, out var sides) ? sides.GetValueOrDefault(cell) ?? (cell != operand ? Kinds.GetValueOrDefault(cell) : null) ?? []
            : Kinds.GetValueOrDefault(cell) ?? [];

        /// <summary>Every cell a key with this operand is witnessed at: each kind, the literal's own split into its sides.</summary>
        public static IEnumerable<CosmosValueClass> Cells(CosmosValueClass operand) => Sides.TryGetValue(operand, out var sides)
            ? Kinds.Keys.Where(kind => kind != operand).Concat(sides.Keys.Where(cell => cell != None))
            : Kinds.Keys;

    }

}
