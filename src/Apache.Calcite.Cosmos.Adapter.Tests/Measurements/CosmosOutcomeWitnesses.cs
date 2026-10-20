using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
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
    /// <para>
    /// <b>A connective's undefined is no path:</b> an ordering across types (<see cref="Side"/>), as a comparison over a
    /// path answers, since S keys a path written bare by the class of what it holds and anything else by its truth.
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
        /// U+D800, which the two orders rank apart; integers past 2^53, where a double ties neighbours, up to 2^63 - 1. A
        /// range's are by code point: between and outside bounds the two orders rank apart, and two they rank alike.
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
            [Parted] = new()
            {
                [Between] = ["\"\\ue000\"", "\"\\uff21\"", "\"\\ud83d\\ude00\""],
                [Outside] = ["\"\"", "\"B\"", "\"a\"", "\"\\ud7ff\"", "\"\\ud83d\\ude01\""],
            },
            [Agreeing] = new()
            {
                [Between] = ["\"B\"", "\"C\"", "\"a\""],
                [Outside] = ["\"\"", "\"A\"", "\"bikes\"", "\"\\ue000\"", "\"\\ud83d\\ude00\""],
            },
        };

        /// <summary>
        /// Literals a double rounds, and values either side and at each as the service holds them, an int64 as itself and
        /// anything else as its correctly rounded double: 9007199254740992.5 is 2^53, below 2^53 + 1; 2^63 + 1, which the
        /// emulator holds in [2^63, 2^64), is 2^63. None is an integer spelled as a fraction, which the emulator holds as a
        /// double and Azure as an int64.
        /// </summary>
        static readonly Dictionary<string, Dictionary<CosmosValueClass, string?[]>> Edges = new()
        {
            ["0.1"] = new() { [Below] = ["0.09999999999999999"], [Equal] = ["0.1", "0.1000000000000000055511151231257827021181583404541015625", "0.10000000000000000001"], [Above] = ["0.10000000000000002"] },
            ["9007199254740993"] = new() { [Below] = ["9007199254740992.5"], [Above] = ["9007199254740993.5"] },
            ["1e22"] = new() { [Below] = ["9999999999999997902848"], [Equal] = ["1e22", "9999999999999999999999", "10000000000000000000001"], [Above] = ["10000000000000002097152"] },
            ["9.223372036854776E18"] = new() { [Below] = ["9223372036854775807"], [Equal] = ["9223372036854775808", "9223372036854775809"], [Above] = ["9223372036854777856"] },
        };

        /// <summary>The literals of <see cref="Edges"/>: 0.1, 2^53 + 1, 1e22, and 2^63 as a double.</summary>
        public static IReadOnlyList<string> RoundingEdges { get; } = [.. Edges.Keys];

        /// <summary>Every kind of value a path may hold, and none.</summary>
        public static IReadOnlyCollection<CosmosValueClass> KindCells => Kinds.Keys;

        /// <summary>
        /// The literal of an operand's class, as JSON: U+E000, where code point and UTF-16 order part; 2^53 + 1, where a
        /// double and an int64 part; a boolean; a range's two bounds. <c>null</c> where a test takes none.
        /// </summary>
        public static string? Literal(CosmosValueClass operand) => operand switch
        {
            String => "\"\\ue000\"",
            Number => "9007199254740993",
            True => "true",
            False => "false",
            Parted => "[\"\\ue000\",\"\\ud83d\\ude00\"]",
            Agreeing => "[\"B\",\"a\"]",
            _ => null,
        };

        /// <summary>The witnesses of a cell against an operand: a kind's, a side of the operand's literal, or of an edge.</summary>
        public static IReadOnlyList<string?> Of(CosmosValueClass operand, CosmosValueClass cell, string? edge = null) =>
            edge is not null ? Edges[edge].GetValueOrDefault(cell) ?? []
            : cell == Undefined ? ["1"]
            : Sides.TryGetValue(operand, out var sides) ? sides.GetValueOrDefault(cell) ?? (cell != operand ? Kinds.GetValueOrDefault(cell) : null) ?? []
            : Kinds.GetValueOrDefault(cell) ?? [];

        /// <summary>
        /// Every cell an outcome is witnessed at: an edge's sides; an order whole; a range's by where a value lies; a
        /// connective's every operand, undefined among them; a comparison's kinds, its literal's own split into sides.
        /// </summary>
        public static IEnumerable<CosmosValueClass> Cells(CosmosOutcome outcome, string? edge = null) => outcome switch
        {
            _ when edge is not null => Edges[edge].Keys,
            { Formula: not null } => Kinds.Keys,
            { Test: CosmosServiceTest.Order } => [None],
            { Test: CosmosServiceTest.Not or CosmosServiceTest.And or CosmosServiceTest.Or } => [.. Kinds.Keys, Undefined],
            { Test: CosmosServiceTest.Range } => Sides[outcome.Operand].Keys,
            _ when Sides.TryGetValue(outcome.Operand, out var sides) => Kinds.Keys.Where(kind => kind != outcome.Operand).Concat(sides.Keys.Where(cell => cell != None)),
            _ => Kinds.Keys,
        };

        /// <summary>What stands for a value of a class in a connective: the path, or for undefined an ordering across types over it.</summary>
        public static string Side(CosmosValueClass cell, string path) => cell == Undefined ? $"({path} > \"\")" : path;

    }

}
