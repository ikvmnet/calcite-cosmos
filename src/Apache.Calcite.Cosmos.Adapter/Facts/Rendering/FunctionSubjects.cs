using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Which operands of a function its rows describe, by the loader's ordinals: the string the function reads, never a
    /// literal count, start or search, whose legality is its spelling's (<c>DESIGN.md</c>, <em>A computed value is a
    /// place</em>).
    /// </summary>
    internal static class FunctionSubjects
    {

        /// <summary>An ordinal standing for every operand: a concatenation reads each.</summary>
        public const long Every = -1;

        /// <summary>The function rows' names and the operand each reads, by the loader's ordinals (<c>TRIM</c>'s flag is no operand).</summary>
        public static readonly IReadOnlyDictionary<string, long> Subject = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["CHAR_LENGTH"] = 0,
            ["CHARACTER_LENGTH"] = 0,
            ["LENGTH"] = 0,
            ["SUBSTRING"] = 0,
            ["LEFT"] = 0,
            ["RIGHT"] = 0,
            ["UPPER"] = 0,
            ["LOWER"] = 0,
            ["REPLACE"] = 0,
            ["POSITION"] = 1,
            ["REVERSE"] = 0,
            ["REPEAT"] = 0,
            ["TRIM BOTH"] = 1,
            ["TRIM LEADING"] = 1,
            ["TRIM TRAILING"] = 1,
            ["CONCAT"] = Every,
            ["||"] = Every,
        };

        /// <summary>Whether the rows of a function describe its operand at an ordinal.</summary>
        public static bool Describes(string function, long ordinal) =>
            Subject.TryGetValue(function, out var subject) && (subject == Every || subject == ordinal);

        /// <summary>Whether a function's rows describe any of its operands: one the rows name.</summary>
        public static bool Named(string function) => Subject.ContainsKey(function);

    }

}
